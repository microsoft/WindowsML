// Copyright (C) Microsoft Corporation. All rights reserved.
//
// In-process server: host the OpenAI-compatible Windows ML Server inside an
// application and give its endpoint to a client process.
//
// Serve a local GGUF model over loopback HTTP from this process and run one
// of the client samples against it.
//
//   The application, not the server, loads the model and picks CPU or GPU.
//   It builds one stage, loads the tokenizer from the GGUF, and configures a
//   Text Generation lane. A short probe session reports whether generation
//   can be constrained, which decides whether tool calls and JSON schema are
//   advertised. The server is created for one active request (others
//   queue), and RegisterModel takes over the lane configuration. Start
//   listens on 127.0.0.1 and [::1] at a free port with a new access key.
//   The client inherits the URL and key as environment variables, which the
//   host removes from itself right after launch; Stop cancels open requests
//   and discards the key.
//
// Run it
//   ..\..\run_in_process_server.ps1 [-Client cpp|python|csharp] [-ModelPath <model.gguf>]
//                                   [-ModelId <id>] [-ContextTokens <count>]
//                                   [-Device cpu|gpu]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../client/README.md
//   ../../../../docs/api-reference/IWinMLServer.md

#include <WinMLRuntime.h>
#include <WinMLTasks.h>
#include <WinMLTokenizer.h>
#include <winml/server/WinMLServer.hpp>
#include <winml/tasks/WinMLTasks.hpp>

#include <algorithm>
#include <cerrno>
#include <cstdio>
#include <cwchar>
#include <cwctype>
#include <filesystem>
#include <string>

#include <wil/result.h>
#include <wil/resource.h>
#include <wrl/client.h>

namespace
{

using Microsoft::WRL::ComPtr;

// The client finds the server through these variables. The environment keeps
// the access key off the client's command line, where other users on the
// machine could read it.
constexpr char BaseUrlVariable[] = "WINMLSERVER_BASE_URL";
constexpr char AccessKeyVariable[] = "WINMLSERVER_ACCESS_KEY";

// Longer replies end with finish_reason "length". The context window also has
// to hold the prompt, so the reply limit stays well below it.
constexpr UINT64 DefaultMaximumOutputTokens = 2048;

struct Arguments
{
    std::wstring modelPath;
    std::wstring modelId;
    UINT64 contextTokens = 0;
    bool useGpu = false;
    std::wstring clientCommandLine;
};

bool ParsePositiveCount(LPCWSTR value, UINT64& count) noexcept
{
    if (*value == L'\0')
    {
        return false;
    }

    for (LPCWSTR cursor = value; *cursor != L'\0'; ++cursor)
    {
        if (!std::iswdigit(*cursor))
        {
            return false;
        }
    }

    errno = 0;
    const unsigned long long parsed = std::wcstoull(value, nullptr, 10);
    if (errno == ERANGE || parsed == 0)
    {
        return false;
    }

    count = parsed;
    return true;
}

// Quote one argument so the client's command-line parser reads it back
// unchanged, including spaces, quotes, and trailing backslashes.
void AppendArgument(std::wstring& commandLine, const std::wstring& argument)
{
    if (!commandLine.empty())
    {
        commandLine.push_back(L' ');
    }

    if (!argument.empty() && argument.find_first_of(L" \t\n\v\"") == std::wstring::npos)
    {
        commandLine.append(argument);
        return;
    }

    commandLine.push_back(L'"');
    for (auto cursor = argument.begin();; ++cursor)
    {
        size_t backslashCount = 0;
        while (cursor != argument.end() && *cursor == L'\\')
        {
            ++cursor;
            ++backslashCount;
        }

        if (cursor == argument.end())
        {
            commandLine.append(backslashCount * 2, L'\\');
            break;
        }

        if (*cursor == L'"')
        {
            commandLine.append(backslashCount * 2 + 1, L'\\');
        }
        else
        {
            commandLine.append(backslashCount, L'\\');
        }

        commandLine.push_back(*cursor);
    }

    commandLine.push_back(L'"');
}

// server-in-process.exe <model.gguf> [--model-id <id>] [--context-tokens <count>]
//                       [--device cpu|gpu] -- <client> [client arguments]
bool ParseArguments(int argumentCount, wchar_t** arguments, Arguments& parsed)
{
    if (argumentCount < 2)
    {
        return false;
    }

    parsed.modelPath = arguments[1];
    int index = 2;
    for (; index < argumentCount; ++index)
    {
        const std::wstring option = arguments[index];
        if (option == L"--")
        {
            ++index;
            break;
        }

        if (index + 1 >= argumentCount)
        {
            return false;
        }

        if (option == L"--model-id")
        {
            parsed.modelId = arguments[++index];
        }
        else if (option == L"--context-tokens")
        {
            if (!ParsePositiveCount(arguments[++index], parsed.contextTokens))
            {
                return false;
            }
        }
        else if (option == L"--device")
        {
            const std::wstring device = arguments[++index];
            if (device != L"cpu" && device != L"gpu")
            {
                return false;
            }

            parsed.useGpu = device == L"gpu";
        }
        else
        {
            return false;
        }
    }

    for (; index < argumentCount; ++index)
    {
        AppendArgument(parsed.clientCommandLine, arguments[index]);
    }

    // Clients select a model by this id, so default to the file name.
    if (parsed.modelId.empty())
    {
        parsed.modelId = std::filesystem::path(parsed.modelPath).stem().wstring();
    }

    return !parsed.modelId.empty() && !parsed.clientCommandLine.empty();
}

// Load the GGUF model and build a one-stage pipeline on the target. llama.cpp
// sizes the context window when the pipeline is built, so a requested window
// is passed to the stage as a hint first.
HRESULT BuildPipeline(IWinMLRuntime* runtime, IWinMLExecutionTarget* target, LPCWSTR modelPath,
                      UINT64 contextTokens, ComPtr<IWinMLPipeline>& pipeline,
                      ComPtr<IWinMLStage>& stage) noexcept
{
    ComPtr<IWinMLModel> model;
    RETURN_IF_FAILED(runtime->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));

    ComPtr<IWinMLPipelineBuilder> builder;
    RETURN_IF_FAILED(runtime->CreatePipelineBuilder(builder.GetAddressOf()));
    RETURN_IF_FAILED(
        builder->AddModelStage(model.Get(), target, L"server-lane", stage.GetAddressOf()));
    if (contextTokens != 0)
    {
        ComPtr<IWinMLStatefulStageOptions> options;
        RETURN_IF_FAILED(stage.As(&options));
        RETURN_IF_FAILED(options->SetSequenceCapacityHint(contextTokens));
    }

    return builder->Build(pipeline.GetAddressOf());
}

// The configuration names the pipeline, the stage that takes token ids and
// returns logits, the stage that holds the sequence state, the target for
// token tensors, and the tokenizer.
HRESULT CreateLaneConfiguration(IWinMLTextGenerationTaskFactory* factory, IWinMLPipeline* pipeline,
                                IWinMLStage* stage, IWinMLExecutionTarget* target,
                                IWinMLTokenizer* tokenizer,
                                ComPtr<IWinMLTextGenerationConfiguration>& configuration) noexcept
{
    configuration.Reset();
    RETURN_IF_FAILED(factory->CreateTextGenerationConfiguration(configuration.GetAddressOf()));
    RETURN_IF_FAILED(configuration->SetMode(WINML_TEXT_GENERATION_CONFIGURATION_MODE_UNIFIED));
    RETURN_IF_FAILED(
        configuration->SetPipeline(WINML_TEXT_GENERATION_PIPELINE_PHASE_UNIFIED, pipeline));
    RETURN_IF_FAILED(
        configuration->SetTokenInput(WINML_TEXT_GENERATION_PIPELINE_PHASE_UNIFIED, stage, 0));
    RETURN_IF_FAILED(configuration->SetOutput(WINML_TEXT_GENERATION_PIPELINE_PHASE_UNIFIED, stage,
                                              0, WINML_TEXT_GENERATION_OUTPUT_KIND_LOGITS));
    RETURN_IF_FAILED(configuration->SetStateOwnerStage(stage));
    RETURN_IF_FAILED(configuration->SetTokenTarget(target));
    RETURN_IF_FAILED(configuration->SetTokenizer(tokenizer));
    return configuration->Validate();
}

// The server guarantees well-formed tool calls and JSON schema output only
// when the backend can constrain which tokens it generates, and a Text
// Generation session reports whether it can. A session uses up the
// configuration it is created from, so the probe gets its own configuration
// and closes the session before the server takes over the pipeline.
HRESULT ProbeCapabilityFlags(IWinMLTextGenerationTask* task,
                             IWinMLTextGenerationConfiguration* probeConfiguration,
                             WINML_SERVER_MODEL_CAPABILITY_FLAGS& flags) noexcept
{
    ComPtr<IWinMLTextGenerationSession> session;
    RETURN_IF_FAILED(task->CreateSession(probeConfiguration, session.GetAddressOf()));
    WINML_TEXT_GENERATION_CAPABILITIES capabilities{};
    const HRESULT capabilityResult = session->GetCapabilities(&capabilities);
    RETURN_IF_FAILED(session->Close());
    RETURN_IF_FAILED(capabilityResult);

    flags = capabilities.supportsConstraints
                ? WINML_SERVER_MODEL_CAPABILITY_FLAGS_TOOL_CALLS |
                      WINML_SERVER_MODEL_CAPABILITY_FLAGS_PARALLEL_TOOL_CALLS |
                      WINML_SERVER_MODEL_CAPABILITY_FLAGS_JSON_SCHEMA |
                      WINML_SERVER_MODEL_CAPABILITY_FLAGS_CONSTRAINED_LOGITS
                : WINML_SERVER_MODEL_CAPABILITY_FLAGS_NONE;
    return S_OK;
}

// The server checks each request against the effective window: the window the
// pipeline was built with. The window the model file declares can be larger,
// and the server's model list reports both.
struct ContextWindow
{
    UINT64 effectiveTokens = 0;
    WINML_SERVER_MODEL_CONTEXT declared{};
};

HRESULT ReadContextWindow(IWinMLStage* stage, ContextWindow& window) noexcept
{
    ComPtr<IWinMLStatefulStage> stateful;
    RETURN_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(stateful.GetAddressOf())));
    RETURN_IF_FAILED(stateful->GetSequenceCapacity(&window.effectiveTokens));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), window.effectiveTokens == 0);

    WINML_SEQUENCE_CAPACITY_DISCLOSURE disclosure = WINML_SEQUENCE_CAPACITY_DISCLOSURE_UNKNOWN;
    UINT64 declaredTokens = 0;
    RETURN_IF_FAILED(stateful->GetDeclaredSequenceCapacity(&disclosure, &declaredTokens));
    if (disclosure == WINML_SEQUENCE_CAPACITY_DISCLOSURE_ARTIFACT_DECLARED)
    {
        window.declared.declaredContextDisclosure =
            WINML_SERVER_CONTEXT_DISCLOSURE_ARTIFACT_DECLARED;
        window.declared.declaredContextWindowTokens = declaredTokens;
    }

    return S_OK;
}

// Ctrl+C reaches every process attached to the console. The client handles it
// and exits, while the host keeps running so it can stop the server.
BOOL WINAPI IgnoreInterrupt(DWORD controlType) noexcept
{
    return controlType == CTRL_C_EVENT || controlType == CTRL_BREAK_EVENT;
}

// Start the client with the endpoint and key in its environment, wait for it
// to exit, and return its exit code. The key leaves this process's environment
// as soon as the client has its own copy.
HRESULT RunClient(std::wstring commandLine, const std::string& baseUrl,
                  const std::string& accessKey, DWORD& exitCode) noexcept
{
    RETURN_IF_WIN32_BOOL_FALSE(SetEnvironmentVariableA(BaseUrlVariable, baseUrl.c_str()));
    RETURN_IF_WIN32_BOOL_FALSE(SetEnvironmentVariableA(AccessKeyVariable, accessKey.c_str()));
    auto clearEnvironment = wil::scope_exit([]() noexcept {
        SetEnvironmentVariableA(AccessKeyVariable, nullptr);
        SetEnvironmentVariableA(BaseUrlVariable, nullptr);
    });

    RETURN_IF_WIN32_BOOL_FALSE(SetConsoleCtrlHandler(IgnoreInterrupt, TRUE));
    auto restoreInterrupt = wil::scope_exit([]() noexcept {
        SetConsoleCtrlHandler(IgnoreInterrupt, FALSE);
    });

    // The client inherits this console and its standard handles. The server's
    // sockets are not inheritable, so they stay in this process.
    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    wil::unique_process_information process;
    if (!CreateProcessW(nullptr, commandLine.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr,
                        &startupInfo, &process))
    {
        const DWORD error = GetLastError();
        std::fwprintf(stderr, L"Could not start the client: %ls\n", commandLine.c_str());
        return HRESULT_FROM_WIN32(error);
    }

    clearEnvironment.reset();
    RETURN_LAST_ERROR_IF(WaitForSingleObject(process.hProcess, INFINITE) != WAIT_OBJECT_0);
    RETURN_IF_WIN32_BOOL_FALSE(GetExitCodeProcess(process.hProcess, &exitCode));
    return S_OK;
}

} // namespace

int wmain(int argumentCount, wchar_t** arguments)
try
{
    Arguments parsed;
    if (!ParseArguments(argumentCount, arguments, parsed))
    {
        std::fwprintf(stderr, L"Usage: server-in-process.exe <model.gguf> [--model-id <id>] "
                              L"[--context-tokens <count>] [--device cpu|gpu] -- <client> "
                              L"[client arguments]\n");
        return 2;
    }

    // The application owns the Runtime objects the server runs. This sample
    // builds one Text Generation lane for a GGUF model on the CPU, or on the
    // GPU with --device gpu. On a GPU, llama.cpp runs the model with a GPU
    // backend deployed beside the executable.
    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    ComPtr<IWinMLExecutionTarget> target;
    if (parsed.useGpu)
    {
        THROW_IF_FAILED(runtime->CreateExecutionTarget(WINML_EXECUTION_TARGET_KIND_GPU,
                                                       WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE,
                                                       target.GetAddressOf()));
    }
    else
    {
        THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(target.GetAddressOf()));
    }

    ComPtr<IWinMLPipeline> pipeline;
    ComPtr<IWinMLStage> stage;
    THROW_IF_FAILED(BuildPipeline(runtime.Get(), target.Get(), parsed.modelPath.c_str(),
                                  parsed.contextTokens, pipeline, stage));

    // A GGUF file carries its tokenizer and chat template. The server formats
    // conversations and tool definitions with this tokenizer.
    ComPtr<IWinMLTokenizer> tokenizer;
    THROW_IF_FAILED(
        WinMLCreateTokenizerFromFile(parsed.modelPath.c_str(), tokenizer.GetAddressOf()));

    ComPtr<IWinMLTasks> tasks;
    THROW_IF_FAILED(winml::tasks::Create(runtime.Get(), tasks));
    ComPtr<IWinMLTextGenerationTaskFactory> factory;
    THROW_IF_FAILED(winml::tasks::GetTextGenerationFactory(tasks.Get(), factory.GetAddressOf()));
    ComPtr<IWinMLTextGenerationTask> task;
    THROW_IF_FAILED(factory->CreateTextGenerationTask(
        tokenizer.Get(), WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT, 0, nullptr,
        task.GetAddressOf()));

    WINML_SERVER_MODEL_CAPABILITY_FLAGS capabilityFlags = WINML_SERVER_MODEL_CAPABILITY_FLAGS_NONE;
    {
        ComPtr<IWinMLTextGenerationConfiguration> probeConfiguration;
        THROW_IF_FAILED(CreateLaneConfiguration(factory.Get(), pipeline.Get(), stage.Get(),
                                                target.Get(), tokenizer.Get(), probeConfiguration));
        THROW_IF_FAILED(
            ProbeCapabilityFlags(task.Get(), probeConfiguration.Get(), capabilityFlags));
    }

    ComPtr<IWinMLTextGenerationConfiguration> configuration;
    THROW_IF_FAILED(CreateLaneConfiguration(factory.Get(), pipeline.Get(), stage.Get(),
                                            target.Get(), tokenizer.Get(), configuration));
    ContextWindow context;
    THROW_IF_FAILED(ReadContextWindow(stage.Get(), context));
    const UINT32 maximumOutputTokens =
        static_cast<UINT32>((std::min)(context.effectiveTokens, DefaultMaximumOutputTokens));

    // One lane runs one request at a time. Other requests wait in the model's
    // queue, within the default queue limits.
    WINML_SERVER_OPTIONS options = winml::server::MakeDefaultOptions();
    options.maximumActiveRequests = 1;
    options.maximumCompletionTokens = maximumOutputTokens;
    ComPtr<IWinMLServer> server;
    THROW_IF_FAILED(winml::server::Create(options, server));

    // A lane keeps the Task runtime, tokenizer, and configuration together so
    // they cannot be mismatched. Registration takes over the configuration;
    // from here on only the server runs this pipeline.
    WINML_SERVER_LANE lane{};
    lane.taskRuntime = tasks.Get();
    lane.tokenizer = tokenizer.Get();
    lane.configuration = configuration.Get();

    WINML_SERVER_MODEL_REGISTRATION registration{};
    registration.modelId = parsed.modelId.c_str();
    registration.ownedBy = L"winml";
    registration.capabilities.contextWindowTokens = context.effectiveTokens;
    registration.capabilities.maximumOutputTokens = maximumOutputTokens;
    registration.capabilities.capabilityFlags = capabilityFlags;
    registration.capabilities.reasoningEffortMask = WINML_SERVER_REASONING_EFFORT_FLAGS_DISABLED;
    registration.laneCount = 1;
    registration.lanes = &lane;
    THROW_IF_FAILED(server->RegisterModel(&registration));

    ComPtr<IWinMLServerModelContext> modelContext;
    THROW_IF_FAILED(server.As(&modelContext));
    THROW_IF_FAILED(modelContext->SetModelContext(parsed.modelId.c_str(), &context.declared));

    // Start listens on 127.0.0.1 and [::1] at a free port and creates the
    // access key for this run. Every request must send the key as a bearer
    // token.
    THROW_IF_FAILED(server->Start());
    auto stopServer = wil::scope_exit([&]() noexcept {
        LOG_IF_FAILED(server->Stop());
    });
    UINT16 port = 0;
    THROW_IF_FAILED(server->GetPort(&port));
    std::string accessKey;
    THROW_IF_FAILED(winml::server::GetAccessKey(server.Get(), accessKey));
    auto clearAccessKey = wil::scope_exit([&]() noexcept {
        SecureZeroMemory(accessKey.data(), accessKey.size());
    });

    const std::string baseUrl = "http://127.0.0.1:" + std::to_string(port) + "/v1";
    std::wprintf(L"Model: %ls\n", parsed.modelId.c_str());
    std::wprintf(L"  Device: %ls\n", parsed.useGpu ? L"GPU" : L"CPU");
    std::wprintf(L"  Context window: %llu tokens", context.effectiveTokens);
    if (context.declared.declaredContextDisclosure ==
        WINML_SERVER_CONTEXT_DISCLOSURE_ARTIFACT_DECLARED)
    {
        std::wprintf(L" (the model declares %llu)", context.declared.declaredContextWindowTokens);
    }

    std::wprintf(L"\n  Maximum output: %u tokens\n", maximumOutputTokens);
    std::wprintf(L"  Tool calls: %ls\n",
                 (capabilityFlags & WINML_SERVER_MODEL_CAPABILITY_FLAGS_TOOL_CALLS) != 0
                     ? L"supported"
                     : L"not supported");
    std::wprintf(L"Serving at %hs\n\n", baseUrl.c_str());
    std::fflush(stdout);

    DWORD clientExitCode = 0;
    THROW_IF_FAILED(RunClient(parsed.clientCommandLine, baseUrl, accessKey, clientExitCode));

    // Stop cancels any request still running, joins the lane threads, and
    // discards the access key.
    stopServer.release();
    THROW_IF_FAILED(server->Stop());
    std::wprintf(L"\nClient exited with code %lu. Server stopped.\n", clientExitCode);
    return static_cast<int>(clientExitCode);
}
catch (...)
{
    const HRESULT result = wil::ResultFromCaughtException();
    std::fprintf(stderr, "In-process server failed: 0x%08X\n", result);
    return 1;
}
