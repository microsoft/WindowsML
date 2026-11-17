// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Bring your own weights: generate text from a published checkpoint folder or a
// single GGUF file. Windows ML plans the model for the GPU, creates the model's
// own tokenizer from its files and streams the reply. The app builds no pipeline.

#include "model_task.h"
#include "text_generation_task.h"

#include <filesystem>
#include <string>

int wmain(int argumentCount, wchar_t** arguments)
try
{
    using namespace winmlsamples::tasks;

    LPCWSTR modelPath = nullptr;
    LPCWSTR prompt = kDefaultChatPrompt;
    LPCWSTR programCache = nullptr;
    bool cacheWeights = false;
    UINT32 maxNewTokens = 64;
    UINT64 capacity = 0;
    int positionalCount = 0;
    bool validArguments = true;

    for (int index = 1; index < argumentCount && validArguments; ++index)
    {
        const std::wstring argument = arguments[index];
        if (argument == L"--capacity" && index + 1 < argumentCount)
        {
            validArguments = ParseUInt64Option(arguments[++index], capacity) && capacity != 0;
        }
        else if (argument == L"--program-cache" && index + 1 < argumentCount)
        {
            programCache = arguments[++index];
        }
        else if (argument == L"--cache-weights")
        {
            cacheWeights = true;
        }
        else if (argument.starts_with(L"--"))
        {
            validArguments = false;
        }
        else if (positionalCount == 0)
        {
            modelPath = arguments[index];
            ++positionalCount;
        }
        else if (positionalCount == 1)
        {
            prompt = arguments[index];
            ++positionalCount;
        }
        else if (positionalCount == 2)
        {
            validArguments = ParsePositiveTokenCount(arguments[index], maxNewTokens);
            ++positionalCount;
        }
        else
        {
            validArguments = false;
        }
    }

    if (!validArguments || modelPath == nullptr || (cacheWeights && programCache == nullptr))
    {
        std::fwprintf(stderr, L"Usage: task-model-text-generation.exe "
                              L"<checkpoint-folder|model.gguf> [prompt] [max-new-tokens] "
                              L"[--capacity <tokens>] "
                              L"[--program-cache <directory> [--cache-weights]]\n");
        return 2;
    }

    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    ComPtr<IWinMLExecutionTarget> target;
    THROW_IF_FAILED(runtime->CreateExecutionTarget(WINML_EXECUTION_TARGET_KIND_GPU,
                                                   WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE,
                                                   target.GetAddressOf()));

    // A checkpoint folder (config.json, safetensors weights, tokenizer.json and
    // tokenizer_config.json) loads with LoadModelFromPath; a single GGUF file
    // with LoadModelFromFile.
    ComPtr<IWinMLModel> model;
    if (std::filesystem::is_directory(modelPath))
    {
        THROW_IF_FAILED(runtime->LoadModelFromPath(modelPath, nullptr, model.GetAddressOf()));
    }
    else
    {
        THROW_IF_FAILED(runtime->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));
    }

    ComPtr<IWinMLTasks> tasks;
    THROW_IF_FAILED(WinMLCreateTasks(runtime.Get(), IID_PPV_ARGS(tasks.GetAddressOf())));
    ComPtr<IWinMLTextGenerationTaskFactory> factory;
    THROW_IF_FAILED(tasks.As(&factory));
    ComPtr<IWinMLTextGenerationConfiguration> configuration;
    THROW_IF_FAILED(factory->CreateTextGenerationConfiguration(configuration.GetAddressOf()));
    ComPtr<IWinMLModelTextGenerationConfiguration> modelConfiguration;
    THROW_IF_FAILED(configuration.As(&modelConfiguration));

    // Sessions compile programs for the GPU. A program cache directory keeps
    // them, so later runs start faster. With cacheWeights it also keeps the
    // weights prepared for each program, so later runs read them instead of
    // preparing them again, at the cost of about one copy of the prepared
    // weights per program on disk.
    WINML_TEXT_GENERATION_MODEL_OPTIONS modelOptions{};
    if (programCache != nullptr)
    {
        modelOptions.presentFields =
            WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY;
        modelOptions.compiledProgramDirectory = programCache;
        if (cacheWeights)
        {
            modelOptions.presentFields |=
                WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE;
            modelOptions.compiledProgramStorage =
                WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS_AND_WEIGHTS;
        }
    }

    // No SetTokenizer: SetModel creates the model's own tokenizer from its files.
    const HRESULT bound = modelConfiguration->SetModel(target.Get(), model.Get(), &modelOptions);
    if (IsNotSupported(bound))
    {
        std::fwprintf(stderr,
                      L"Windows ML cannot run this model for text generation "
                      L"(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)).\n"
                      L"Use a supported checkpoint, such as Qwen2.5-0.5B-Instruct as a "
                      L"safetensors folder or GGUF file. To run a GGUF file that Windows ML "
                      L"does not support on llama.cpp instead, load it with the llama.cpp "
                      L"loader; see the llama-text-generation sample.\n");
        return 1;
    }

    THROW_IF_FAILED(bound);

    if (capacity != 0)
    {
        THROW_IF_FAILED(configuration->SetLogicalCapacity(capacity));
    }

    // The task must use the configuration's tokenizer: the one SetModel created.
    ComPtr<IWinMLTokenizer> tokenizer;
    THROW_IF_FAILED(configuration->GetTokenizer(tokenizer.GetAddressOf()));
    ComPtr<IWinMLTextGenerationTask> task;
    THROW_IF_FAILED(factory->CreateTextGenerationTask(
        tokenizer.Get(), WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT, 0, nullptr,
        task.GetAddressOf()));
    ComPtr<IWinMLTextGenerationSession> session;
    THROW_IF_FAILED(task->CreateSession(configuration.Get(), session.GetAddressOf()));
    StreamCloser sessionCloser(session.Get());

    TextGenerationResult result;
    THROW_IF_FAILED(StreamChatReply(tasks.Get(), factory.Get(), session.Get(), tokenizer.Get(),
                                    prompt, maxNewTokens, result));
    THROW_IF_FAILED(session->Close());
    return 0;
}
catch (...)
{
    winmlsamples::tasks::ReportFailure(L"Text generation", wil::ResultFromCaughtException());
    return 1;
}
