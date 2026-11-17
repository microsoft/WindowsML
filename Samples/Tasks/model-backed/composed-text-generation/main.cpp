// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Bring your own model: describe a composition of models that the app loaded
// and let the text generation configuration compose it. This sample composes
// the exported Qwen ONNX decoder that the language text-generation sample
// wires by hand, as one UNIFIED stage.

#include "model_task.h"
#include "text_generation_task.h"

#include "../../../Runtime/shared/common.h"

#include <string>

int wmain(int argumentCount, wchar_t** arguments)
try
{
    using namespace winmlsamples::tasks;

    LPCWSTR modelPath = nullptr;
    LPCWSTR tokenizerPath = nullptr;
    LPCWSTR prompt = kDefaultChatPrompt;
    LPCWSTR executionProvider = nullptr;
    UINT32 maxNewTokens = 64;
    bool useGpu = false;
    int positionalCount = 0;
    bool validArguments = true;

    for (int index = 1; index < argumentCount && validArguments; ++index)
    {
        const std::wstring argument = arguments[index];
        if (argument == L"--device" && index + 1 < argumentCount)
        {
            const std::wstring device = arguments[++index];
            useGpu = device == L"gpu";
            validArguments = useGpu || device == L"cpu";
        }
        else if (argument == L"--ep" && index + 1 < argumentCount)
        {
            executionProvider = arguments[++index];
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
            tokenizerPath = arguments[index];
            ++positionalCount;
        }
        else if (positionalCount == 2)
        {
            prompt = arguments[index];
            ++positionalCount;
        }
        else if (positionalCount == 3)
        {
            validArguments = ParsePositiveTokenCount(arguments[index], maxNewTokens);
            ++positionalCount;
        }
        else
        {
            validArguments = false;
        }
    }

    if (!validArguments || tokenizerPath == nullptr)
    {
        std::fwprintf(stderr, L"Usage: task-composed-text-generation.exe <task_model.onnx> "
                              L"<tokenizer.json> [prompt] [max-new-tokens] [--device cpu|gpu] "
                              L"[--ep <provider>]\n");
        return 2;
    }

    // WebGPU is the default GPU provider because it works across GPU vendors,
    // and the Runtime acquires it if it isn't installed. --ep pins another
    // provider, such as trtrtx for TensorRT for RTX on an NVIDIA RTX GPU.
    if (useGpu && executionProvider == nullptr)
    {
        executionProvider = L"webgpu";
    }

    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    const WINML_EXECUTION_TARGET_KIND kind =
        useGpu ? WINML_EXECUTION_TARGET_KIND_GPU : WINML_EXECUTION_TARGET_KIND_CPU;
    ComPtr<IWinMLExecutionTarget> target;
    if (useGpu)
    {
        THROW_IF_FAILED(runtime->CreateExecutionTarget(
            kind, WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE, target.GetAddressOf()));
    }
    else
    {
        THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(target.GetAddressOf()));
    }

    if (executionProvider != nullptr)
    {
        // Pin the provider on the device's target. The Runtime acquires a
        // pinned provider when it builds the composition. The composition
        // needs the device's target to create its token tensors.
        const std::string requestedProvider = WideToUtf8(executionProvider);
        const std::wstring providerName =
            Utf8ToWide(ResolveExecutionProviderName(requestedProvider.c_str()));
        ComPtr<IWinMLOrtCompatibility> compatibility;
        THROW_IF_FAILED(runtime.As(&compatibility));
        ComPtr<IWinMLExecutionTarget> pinnedTarget;
        THROW_IF_FAILED(compatibility->CreateExecutionTarget(
            providerName.c_str(), kind, target.Get(), pinnedTarget.GetAddressOf()));
        target = pinnedTarget;
        std::wprintf(L"Pinned execution provider: %ls\n", providerName.c_str());
    }

    ComPtr<IWinMLModel> model;
    THROW_IF_FAILED(runtime->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));
    ComPtr<IWinMLTokenizer> tokenizer;
    THROW_IF_FAILED(WinMLCreateTokenizerFromFile(tokenizerPath, tokenizer.GetAddressOf()));

    ComPtr<IWinMLTasks> tasks;
    THROW_IF_FAILED(WinMLCreateTasks(runtime.Get(), IID_PPV_ARGS(tasks.GetAddressOf())));
    ComPtr<IWinMLTextGenerationTaskFactory> factory;
    THROW_IF_FAILED(tasks.As(&factory));
    ComPtr<IWinMLTextGenerationConfiguration> configuration;
    THROW_IF_FAILED(factory->CreateTextGenerationConfiguration(configuration.GetAddressOf()));
    ComPtr<IWinMLModelTextGenerationConfiguration> modelConfiguration;
    THROW_IF_FAILED(configuration.As(&modelConfiguration));

    // A composition has no tokenizer files, so set the tokenizer first.
    THROW_IF_FAILED(configuration->SetTokenizer(tokenizer.Get()));

    // The exported decoder takes token IDs at input 0 and produces logits at
    // output 0. Input 1 and output 1 are its key/value state, which the stage
    // feeds back after each step.
    const UINT32 stateInputs[] = {1};
    const UINT32 stateOutputs[] = {1};
    WINML_TEXT_GENERATION_COMPOSED_MODEL description{};
    description.kind = WINML_TEXT_GENERATION_COMPOSITION_KIND_UNIFIED;
    description.model.model = model.Get();
    description.model.tokenInputIndex = 0;
    description.model.outputIndex = 0;
    description.model.outputKind = WINML_TEXT_GENERATION_OUTPUT_KIND_LOGITS;
    description.model.statePairCount = ARRAYSIZE(stateInputs);
    description.model.stateInputIndices = stateInputs;
    description.model.stateOutputIndices = stateOutputs;
    THROW_IF_FAILED(modelConfiguration->SetComposition(target.Get(), &description, nullptr));

    // The export fixes its sequence capacity in the shape of the state input.
    THROW_IF_FAILED(configuration->SetLogicalCapacity(
        ReadDeclaredSequenceCapacity(model.Get(), kDefaultSequenceCapacity)));

    // The task uses the same tokenizer that the configuration holds.
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
    winmlsamples::tasks::ReportFailure(L"Composed text generation",
                                       wil::ResultFromCaughtException());
    return 1;
}
