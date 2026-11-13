// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Explicit llama.cpp: load a GGUF file through the llama.cpp model loader and
// generate text with it. This is an engine-specific opt-in for text
// generation; nothing selects llama.cpp unless the app loads the model this
// way. A draft model loaded the same way enables speculative decoding.

#include "model_task.h"
#include "text_generation_task.h"

#include <WinMLRuntimeLlama.h>

#include <string>

int wmain(int argumentCount, wchar_t** arguments)
try
{
    using namespace winmlsamples::tasks;

    LPCWSTR modelPath = nullptr;
    LPCWSTR draftModelPath = nullptr;
    LPCWSTR prompt = kDefaultChatPrompt;
    UINT32 maxNewTokens = 64;
    bool useGpu = false;
    int positionalCount = 0;
    bool validArguments = true;

    for (int index = 1; index < argumentCount && validArguments; ++index)
    {
        const std::wstring argument = arguments[index];
        if (argument == L"--draft-model" && index + 1 < argumentCount)
        {
            draftModelPath = arguments[++index];
        }
        else if (argument == L"--device" && index + 1 < argumentCount)
        {
            const std::wstring device = arguments[++index];
            useGpu = device == L"gpu";
            validArguments = useGpu || device == L"cpu";
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

    if (!validArguments || modelPath == nullptr)
    {
        std::fwprintf(stderr, L"Usage: task-llama-text-generation.exe <model.gguf> [prompt] "
                              L"[max-new-tokens] [--draft-model <draft.gguf>] "
                              L"[--device cpu|gpu]\n");
        return 2;
    }

    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));

    // The loader is available when the Windows ML package includes the
    // llama.cpp engine.
    ComPtr<IWinMLLlamaModelLoader> loader;
    const HRESULT loaderResult = runtime.As(&loader);
    if (loaderResult == E_NOINTERFACE)
    {
        std::fwprintf(stderr, L"The llama.cpp model loader is not available (E_NOINTERFACE). "
                              L"Use a Windows ML package that includes the llama.cpp engine.\n");
        return 1;
    }

    THROW_IF_FAILED(loaderResult);

    // The packaged llama.cpp backend runs on the CPU. A GPU target needs a GPU
    // backend that you deploy yourself.
    ComPtr<IWinMLExecutionTarget> target;
    if (useGpu)
    {
        THROW_IF_FAILED(runtime->CreateExecutionTarget(
            WINML_EXECUTION_TARGET_KIND_GPU, WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE,
            target.GetAddressOf()));
    }
    else
    {
        THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(target.GetAddressOf()));
    }

    ComPtr<IWinMLModel> model;
    THROW_IF_FAILED(loader->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));

    // A draft model must be loaded through the same loader and share the
    // model's tokenizer.
    ComPtr<IWinMLModel> draftModel;
    WINML_TEXT_GENERATION_MODEL_OPTIONS modelOptions{};
    if (draftModelPath != nullptr)
    {
        THROW_IF_FAILED(
            loader->LoadModelFromFile(draftModelPath, nullptr, draftModel.GetAddressOf()));
        modelOptions.presentFields = WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_DRAFT_MODEL;
        modelOptions.draftModel = draftModel.Get();
    }

    ComPtr<IWinMLTasks> tasks;
    THROW_IF_FAILED(WinMLCreateTasks(runtime.Get(), IID_PPV_ARGS(tasks.GetAddressOf())));
    ComPtr<IWinMLTextGenerationTaskFactory> factory;
    THROW_IF_FAILED(tasks.As(&factory));
    ComPtr<IWinMLTextGenerationConfiguration> configuration;
    THROW_IF_FAILED(factory->CreateTextGenerationConfiguration(configuration.GetAddressOf()));
    ComPtr<IWinMLModelTextGenerationConfiguration> modelConfiguration;
    THROW_IF_FAILED(configuration.As(&modelConfiguration));

    // SetModel reads the tokenizer from the GGUF file. Each session loads the weights.
    THROW_IF_FAILED(modelConfiguration->SetModel(target.Get(), model.Get(), &modelOptions));

    if (draftModel)
    {
        ComPtr<IWinMLTextGenerationSpeculativeConfiguration> speculative;
        THROW_IF_FAILED(configuration.As(&speculative));
        WINML_TEXT_GENERATION_SPECULATIVE_OPTIONS speculativeOptions{};
        speculativeOptions.method = WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL;
        THROW_IF_FAILED(speculative->SetSpeculativeOptions(&speculativeOptions));
    }

    ComPtr<IWinMLTokenizer> tokenizer;
    THROW_IF_FAILED(configuration->GetTokenizer(tokenizer.GetAddressOf()));
    ComPtr<IWinMLTextGenerationTask> task;
    THROW_IF_FAILED(factory->CreateTextGenerationTask(
        tokenizer.Get(), WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT, 0, nullptr,
        task.GetAddressOf()));
    ComPtr<IWinMLTextGenerationSession> session;
    const HRESULT created = task->CreateSession(configuration.Get(), session.GetAddressOf());
    if (useGpu && IsNotSupported(created))
    {
        std::fwprintf(stderr, L"No deployed llama.cpp backend runs on this GPU "
                              L"(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). Use --device cpu, or "
                              L"deploy a llama.cpp GPU backend with the app.\n");
        return 1;
    }

    if (created == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) ||
        created == HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND))
    {
        // Loading and binding succeed without the llama.cpp payload; the first
        // session loads it.
        std::fwprintf(stderr,
                      L"This sample requires the llama.cpp payload, which was not found "
                      L"(0x%08X). Deploy it beside the app by referencing "
                      L"Microsoft.Windows.AI.MachineLearning.LibLlama.Core.\n",
                      static_cast<unsigned int>(created));
        return 1;
    }

    THROW_IF_FAILED(created);
    StreamCloser sessionCloser(session.Get());

    TextGenerationResult result;
    THROW_IF_FAILED(StreamChatReply(tasks.Get(), factory.Get(), session.Get(), tokenizer.Get(),
                                    prompt, maxNewTokens, result));

    if (draftModel)
    {
        const auto& statistics = result.speculative;
        std::wprintf(L"Speculative decoding: steps=%u drafted=%u accepted=%u\n",
                     statistics.verificationStepCount, statistics.draftTokenCount,
                     statistics.acceptedDraftTokenCount);
    }

    THROW_IF_FAILED(session->Close());
    return 0;
}
catch (...)
{
    winmlsamples::tasks::ReportFailure(L"llama.cpp text generation",
                                       wil::ResultFromCaughtException());
    return 1;
}
