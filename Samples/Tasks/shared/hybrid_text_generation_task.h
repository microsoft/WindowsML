// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared hybrid Text Generation helpers for the native text-generation sample.
// They demonstrate the Task API prefill/decode profile over two caller-built
// Runtime pipelines and the caller-owned state handoff between them.

#pragma once

#include "text_generation_task.h"

#include "../../Runtime/shared/common.h"
#include "../../Runtime/shared/execution_target_utils.h"

#include <winml/tasks/text_generation/PrefillDecodeTextGeneration.hpp>

#include <d3d12.h>

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace winmlsamples::tasks
{

// The Task prefill/decode configuration accepts two independent pipelines. That
// lets the sample express hybrid placement: the prompt is prefilled by a dynamic
// ORT graph, and every generated token is decoded by a fixed single-token graph.
//
// The two pipelines own separate state, so the packed KV cache is caller-owned
// on both sides and handed over once, after the prompt has been evaluated.
struct HybridTextGenerationObjects
{
    ComPtr<IWinMLExecutionTarget> prefillTarget;
    ComPtr<IWinMLExecutionTarget> decodeTarget;
    ComPtr<IWinMLExecutionTarget> decodeTensorTarget;
    ComPtr<IWinMLExecutionTarget> tokenTarget;
    ComPtr<IWinMLModel> prefillModel;
    ComPtr<IWinMLModel> decodeModel;
    ComPtr<IWinMLPipeline> prefillPipeline;
    ComPtr<IWinMLPipeline> decodePipeline;
    ComPtr<IWinMLStage> prefillStage;
    ComPtr<IWinMLStage> decodeStage;
    ComPtr<IWinMLTensor> prefillStateIn;
    ComPtr<IWinMLTensor> prefillStateOut;
    ComPtr<IWinMLTensor> prefillLogits;
    ComPtr<IWinMLTensor> decodeStateIn;
    ComPtr<IWinMLTensor> decodeStateOut;
    ComPtr<IWinMLTensor> decodeLogits;
    ComPtr<IWinMLTokenizer> tokenizer;
    winml::tasks::text_generation::PrefillDecodeTextGeneration task;
    UINT64 sequenceCapacity = 0;
    UINT64 vocabularySize = 0;
    std::wstring decodePlacement;
};

#define HYBRID_STEP(expression, label) \
    do \
    { \
        const HRESULT hybridResult = (expression); \
        if (FAILED(hybridResult)) \
        { \
            std::fwprintf(stderr, L"Hybrid text generation failed at %ls: 0x%08X\n", (label), \
                          static_cast<unsigned int>(hybridResult)); \
            return hybridResult; \
        } \
    } while (false)

// A model schema reports UINT64_MAX for a dimension the exporter left free.
// Every tensor here is allocated up front and copied between the two pipelines,
// so each dimension must be resolved and the element count must be accumulated
// without overflowing.
inline HRESULT GetStaticElementCount(const UINT64* dimensions, UINT32 dimensionCount,
                                     UINT64& elementCount) noexcept
{
    RETURN_HR_IF_NULL(E_POINTER, dimensions);
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), dimensionCount == 0);
    elementCount = 1;
    for (UINT32 index = 0; index < dimensionCount; ++index)
    {
        const UINT64 dimension = dimensions[index];
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                     dimension == 0 || dimension == UINT64_MAX);
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), elementCount > UINT64_MAX / dimension);
        elementCount *= dimension;
    }

    return S_OK;
}

inline HRESULT CreatePackedStateTensor(IWinMLExecutionTarget* target, UINT64 stateChannels,
                                       UINT64 sequenceCapacity, UINT64 headDimension,
                                       IWinMLTensor** tensor) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, target);
    RETURN_HR_IF_NULL(E_POINTER, tensor);
    *tensor = nullptr;

    const UINT64 dimensions[] = {
        1,
        stateChannels,
        sequenceCapacity,
        headDimension,
    };

    UINT64 elementCount = 0;
    RETURN_IF_FAILED(GetStaticElementCount(dimensions, ARRAYSIZE(dimensions), elementCount));
    RETURN_HR_IF(E_OUTOFMEMORY, elementCount > SIZE_MAX / sizeof(UINT16));
    // A zeroed packed state means an empty KV cache and sequence position zero,
    // which is the state the prompt must be evaluated against.
    std::vector<UINT16> zeros(static_cast<size_t>(elementCount), 0);
    WINML_TENSOR_DESC desc = {};
    desc.dataType = WINML_TENSOR_DATA_TYPE_FLOAT16;
    desc.dimensionCount = ARRAYSIZE(dimensions);
    desc.dimensions = dimensions;
    return CreateTensorOnTarget(target, &desc, zeros.data(), zeros.size() * sizeof(UINT16), tensor);
}
CATCH_RETURN()

// Both stages publish the same packed state layout, so the same stateful
// configuration applies to each. Caller ownership is what allows the warmed
// prompt state to cross from the prefill pipeline into the decode pipeline.
//
// State is bound as a distinct input and output tensor rather than one tensor
// used in place: a stage read-locks its inputs and write-locks its outputs, so
// a single tensor on both ends is rejected as a lock conflict.
inline HRESULT CreateLogitsTensor(IWinMLExecutionTarget* target, UINT64 positionCount,
                                  UINT64 vocabularySize, IWinMLTensor** tensor) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, target);
    RETURN_HR_IF_NULL(E_POINTER, tensor);
    *tensor = nullptr;

    const UINT64 dimensions[] = {1, positionCount, vocabularySize};
    UINT64 elementCount = 0;
    RETURN_IF_FAILED(GetStaticElementCount(dimensions, ARRAYSIZE(dimensions), elementCount));
    RETURN_HR_IF(E_OUTOFMEMORY, elementCount > SIZE_MAX / sizeof(float));
    std::vector<float> zeros(static_cast<size_t>(elementCount), 0.0f);

    WINML_TENSOR_DESC desc = {};
    desc.dataType = WINML_TENSOR_DATA_TYPE_FLOAT32;
    desc.dimensionCount = ARRAYSIZE(dimensions);
    desc.dimensions = dimensions;
    return CreateTensorOnTarget(target, &desc, zeros.data(), zeros.size() * sizeof(float), tensor);
}
CATCH_RETURN()

// A stateful stage returns its packed state output only when the caller owns the
// state tensors. Handing warmed prompt state to a second pipeline therefore
// requires caller-owned state on both sides.
//
// Every output must be bound, not merely requested: a backend materialises its
// own outputs only when the caller has bound none of them, so binding the state
// output alone would leave the logits output unproduced.
//
// State is bound as a distinct input and output tensor rather than one tensor
// used in place, because a stage read-locks its inputs and write-locks its
// outputs and rejects a single tensor on both ends as a lock conflict.
inline HRESULT ConfigureStatefulStage(IWinMLStage* stage, IWinMLTensor* stateIn,
                                      IWinMLTensor* stateOut, UINT64 sequenceCapacity) noexcept
{
    RETURN_HR_IF_NULL(E_POINTER, stage);
    RETURN_HR_IF_NULL(E_POINTER, stateIn);
    RETURN_HR_IF_NULL(E_POINTER, stateOut);

    ComPtr<IWinMLStatefulStageOptions> statefulOptions;
    RETURN_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(statefulOptions.GetAddressOf())));
    RETURN_IF_FAILED(statefulOptions->AddStateTensorPair(1, 1));
    RETURN_IF_FAILED(statefulOptions->SetSequenceCapacityHint(sequenceCapacity));
    RETURN_IF_FAILED(statefulOptions->SetCallerOwnsStateTensors(TRUE));
    RETURN_IF_FAILED(stage->BindInput(1, stateIn));
    RETURN_IF_FAILED(stage->BindOutput(1, stateOut));
    return stage->RequestOutput(0);
}

// Advances a caller-owned KV cache by one step: the state a stage just produced
// becomes the state it consumes next.
inline HRESULT AdvanceCallerOwnedState(IWinMLTensor* stateIn, IWinMLTensor* stateOut) noexcept
{
    RETURN_HR_IF_NULL(E_POINTER, stateIn);
    RETURN_HR_IF_NULL(E_POINTER, stateOut);
    ComPtr<IWinMLMutableTensor> destination;
    RETURN_IF_FAILED(stateIn->QueryInterface(IID_PPV_ARGS(destination.GetAddressOf())));
    return destination->CopyFrom(stateOut);
}

// Print the execution target details exposed by the prefill and decode targets.
inline HRESULT ReportPlacementEvidence(const HybridTextGenerationObjects& objects) noexcept
{
    auto describe = [](LPCWSTR label, IWinMLExecutionTarget* target) -> HRESULT {
        RETURN_HR_IF_NULL(E_POINTER, target);
        WINML_EXECUTION_TARGET_KIND kind{};
        RETURN_IF_FAILED(target->GetKind(&kind));
        ComPtr<IWinMLD3D12ExecutionTarget> d3d12Target;
        ComPtr<IUnknown> deviceUnknown;
        if (SUCCEEDED(target->QueryInterface(IID_PPV_ARGS(d3d12Target.GetAddressOf()))))
        {
            if (FAILED(d3d12Target->GetDevice(deviceUnknown.GetAddressOf())))
            {
                deviceUnknown.Reset();
            }
        }

        std::wprintf(L"  %ls target: kind=%ls d3d12=%ls", label,
                     kind == WINML_EXECUTION_TARGET_KIND_GPU   ? L"GPU"
                     : kind == WINML_EXECUTION_TARGET_KIND_NPU ? L"NPU"
                                                               : L"CPU",
                     deviceUnknown ? L"yes" : L"no");
        if (deviceUnknown)
        {
            ComPtr<ID3D12Device> device;
            RETURN_IF_FAILED(deviceUnknown.As(&device));
            ComPtr<IUnknown> queue;
            RETURN_IF_FAILED(d3d12Target->GetCommandQueue(queue.GetAddressOf()));
            const LUID adapter = device->GetAdapterLuid();
            std::wprintf(L" adapterLuid=%08X:%08X queue=%p",
                         static_cast<unsigned int>(adapter.HighPart), adapter.LowPart, queue.Get());
        }

        std::wprintf(L"\n");
        return S_OK;
    };

    std::wprintf(L"Execution targets:\n");
    RETURN_IF_FAILED(describe(L"prefill", objects.prefillTarget.Get()));
    RETURN_IF_FAILED(describe(L"decode ", objects.decodeTarget.Get()));
    return S_OK;
}

// Build the prefill/decode Runtime pipelines, configure caller-owned state, and
// create the Text Generation Task session for the hybrid sample.
inline HRESULT BuildHybridTextGeneration(IWinMLRuntime* runtime, LPCWSTR prefillModelPath,
                                         LPCWSTR decodeModelPath, LPCWSTR tokenizerSource,
                                         UINT32 maxNewTokens, const SamplingArguments& sampling,
                                         HybridTextGenerationObjects& objects) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, runtime);
    RETURN_HR_IF_NULL(E_POINTER, prefillModelPath);
    RETURN_HR_IF_NULL(E_POINTER, decodeModelPath);
    RETURN_HR_IF_NULL(E_POINTER, tokenizerSource);
    RETURN_HR_IF(E_INVALIDARG, maxNewTokens == 0);

    // Prefill stays on ORT because the prompt length is only known at runtime.
    HYBRID_STEP(runtime->CreateCpuExecutionTarget(objects.prefillTarget.GetAddressOf()),
                L"create prefill CPU target");
    objects.tokenTarget = objects.prefillTarget;

    HYBRID_STEP(runtime->CreateCpuExecutionTarget(objects.decodeTarget.GetAddressOf()),
                L"create decode CPU target");
    HYBRID_STEP(
        runtime->LoadModelFromFile(decodeModelPath, nullptr, objects.decodeModel.GetAddressOf()),
        L"load decode model");
    objects.decodePlacement = L"ORT CPU";
    objects.decodeTensorTarget = objects.decodeTarget;

    const auto prefillLoadStarted = std::chrono::steady_clock::now();
    HYBRID_STEP(
        runtime->LoadModelFromFile(prefillModelPath, nullptr, objects.prefillModel.GetAddressOf()),
        L"load prefill model");
    std::wprintf(L"Prefill model ready in %.2f s\n",
                 std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() -
                                                           prefillLoadStarted)
                         .count() /
                     1000.0);

    // The packed state layout is published by the model itself, so the sample
    // reads it rather than restating the exporter's channel arithmetic.
    WINML_TENSOR_SCHEMA_DESC stateDesc = {};
    ComPtr<IWinMLModelSchema> prefillSchema;
    HYBRID_STEP(objects.prefillModel.As(&prefillSchema), L"query prefill schema");
    HYBRID_STEP(prefillSchema->GetInputTensorDesc(1, &stateDesc), L"read packed state schema");
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 stateDesc.dataType != WINML_TENSOR_DATA_TYPE_FLOAT16 ||
                     stateDesc.dimensionCount != 4 || stateDesc.dimensions[0] != 1 ||
                     stateDesc.dimensions[1] == 0 || stateDesc.dimensions[1] == UINT64_MAX ||
                     stateDesc.dimensions[2] == 0 || stateDesc.dimensions[2] == UINT64_MAX ||
                     stateDesc.dimensions[3] == UINT64_MAX || stateDesc.dimensions[3] < 4);
    const UINT64 sequenceCapacity = stateDesc.dimensions[2];
    objects.sequenceCapacity = sequenceCapacity;

    ComPtr<IWinMLModelSchema> decodeSchema;
    HYBRID_STEP(objects.decodeModel.As(&decodeSchema), L"query decode schema");
    WINML_TENSOR_SCHEMA_DESC decodeStateDesc = {};
    HYBRID_STEP(decodeSchema->GetInputTensorDesc(1, &decodeStateDesc),
                L"read decode packed state schema");
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 decodeStateDesc.dataType != stateDesc.dataType ||
                     decodeStateDesc.dimensionCount != stateDesc.dimensionCount);
    for (UINT32 dimension = 0; dimension < stateDesc.dimensionCount; ++dimension)
    {
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                     decodeStateDesc.dimensions[dimension] != stateDesc.dimensions[dimension]);
    }

    HYBRID_STEP(CreatePackedStateTensor(objects.prefillTarget.Get(), stateDesc.dimensions[1],
                                        stateDesc.dimensions[2], stateDesc.dimensions[3],
                                        objects.prefillStateIn.GetAddressOf()),
                L"create prefill state input tensor");
    HYBRID_STEP(CreatePackedStateTensor(objects.prefillTarget.Get(), stateDesc.dimensions[1],
                                        stateDesc.dimensions[2], stateDesc.dimensions[3],
                                        objects.prefillStateOut.GetAddressOf()),
                L"create prefill state output tensor");
    HYBRID_STEP(CreatePackedStateTensor(objects.decodeTensorTarget.Get(), stateDesc.dimensions[1],
                                        stateDesc.dimensions[2], stateDesc.dimensions[3],
                                        objects.decodeStateIn.GetAddressOf()),
                L"create decode state input tensor");
    HYBRID_STEP(CreatePackedStateTensor(objects.decodeTensorTarget.Get(), stateDesc.dimensions[1],
                                        stateDesc.dimensions[2], stateDesc.dimensions[3],
                                        objects.decodeStateOut.GetAddressOf()),
                L"create decode state output tensor");

    WINML_TENSOR_SCHEMA_DESC logitsDesc = {};
    HYBRID_STEP(prefillSchema->GetOutputTensorDesc(0, &logitsDesc), L"read logits schema");
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 logitsDesc.dataType != WINML_TENSOR_DATA_TYPE_FLOAT32 ||
                     logitsDesc.dimensionCount != 3 || logitsDesc.dimensions[0] != 1 ||
                     logitsDesc.dimensions[2] == 0 || logitsDesc.dimensions[2] == UINT64_MAX);
    const UINT64 vocabularySize = logitsDesc.dimensions[2];
    objects.vocabularySize = vocabularySize;
    WINML_TENSOR_SCHEMA_DESC decodeLogitsDesc = {};
    HYBRID_STEP(decodeSchema->GetOutputTensorDesc(0, &decodeLogitsDesc),
                L"read decode logits schema");
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 decodeLogitsDesc.dataType != logitsDesc.dataType ||
                     decodeLogitsDesc.dimensionCount != 3 || decodeLogitsDesc.dimensions[0] != 1 ||
                     decodeLogitsDesc.dimensions[1] != 1 ||
                     decodeLogitsDesc.dimensions[2] != vocabularySize);
    HYBRID_STEP(CreateLogitsTensor(objects.decodeTensorTarget.Get(), 1, vocabularySize,
                                   objects.decodeLogits.GetAddressOf()),
                L"create decode logits tensor");

    const auto pipelineStarted = std::chrono::steady_clock::now();
    ComPtr<IWinMLPipelineBuilder> prefillBuilder;
    HYBRID_STEP(runtime->CreatePipelineBuilder(prefillBuilder.GetAddressOf()),
                L"create prefill pipeline builder");
    HYBRID_STEP(prefillBuilder->AddModelStage(objects.prefillModel.Get(),
                                              objects.prefillTarget.Get(), L"task-hybrid-prefill",
                                              objects.prefillStage.GetAddressOf()),
                L"add prefill stage");
    HYBRID_STEP(ConfigureStatefulStage(objects.prefillStage.Get(), objects.prefillStateIn.Get(),
                                       objects.prefillStateOut.Get(), sequenceCapacity),
                L"configure prefill state");
    HYBRID_STEP(prefillBuilder->Build(objects.prefillPipeline.GetAddressOf()),
                L"build prefill pipeline");

    ComPtr<IWinMLPipelineBuilder> decodeBuilder;
    HYBRID_STEP(runtime->CreatePipelineBuilder(decodeBuilder.GetAddressOf()),
                L"create decode pipeline builder");
    HYBRID_STEP(decodeBuilder->AddModelStage(objects.decodeModel.Get(), objects.decodeTarget.Get(),
                                             L"task-hybrid-decode",
                                             objects.decodeStage.GetAddressOf()),
                L"add decode stage");
    HYBRID_STEP(ConfigureStatefulStage(objects.decodeStage.Get(), objects.decodeStateIn.Get(),
                                       objects.decodeStateOut.Get(), sequenceCapacity),
                L"configure decode state");
    HYBRID_STEP(decodeBuilder->Build(objects.decodePipeline.GetAddressOf()),
                L"build decode pipeline");

    // Bind the logits output after Build. Binding that terminal output
    // before Build makes it planner-owned, which a caller-owned-state stage
    // rejects at execution time. The state bindings above are part of the
    // pre-Build stateful-stage setup.
    HYBRID_STEP(objects.decodeStage->BindOutput(0, objects.decodeLogits.Get()),
                L"bind decode logits");

    HYBRID_STEP(WinMLCreateTokenizerFromFile(tokenizerSource, objects.tokenizer.GetAddressOf()),
                L"load tokenizer");

    winml::tasks::text_generation::Options options;
    options.values = MakeSampledOptions(maxNewTokens, sampling);
    winml::tasks::text_generation::PrefillDecodeTextGenerationArguments arguments;
    arguments.runtime = runtime;
    arguments.prefillPipeline = objects.prefillPipeline.Get();
    arguments.prefillTokenInput = {objects.prefillStage.Get(), 0};
    arguments.prefillOutput = {
        objects.prefillStage.Get(),
        0,
        WINML_TEXT_GENERATION_OUTPUT_KIND_LOGITS,
    };

    arguments.decodePipeline = objects.decodePipeline.Get();
    arguments.decodeTokenInput = {objects.decodeStage.Get(), 0};
    arguments.decodeOutput = {
        objects.decodeStage.Get(),
        0,
        WINML_TEXT_GENERATION_OUTPUT_KIND_LOGITS,
    };

    arguments.decodeInputMode = WINML_TEXT_GENERATION_DECODE_INPUT_MODE_CALLER_BOUND;
    arguments.logicalCapacity = sequenceCapacity;
    arguments.tokenTarget = objects.tokenTarget.Get();
    arguments.tokenizer = objects.tokenizer.Get();
    arguments.eosPolicy = WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT;
    arguments.options = &options;
    HYBRID_STEP(
        winml::tasks::text_generation::BuildPrefillDecodeTextGeneration(arguments, objects.task),
        L"build prefill/decode Task");
    HYBRID_STEP(ReportPlacementEvidence(objects), L"report placement");
    std::wprintf(L"Pipelines and Task built in %.2f s\n",
                 std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() -
                                                           pipelineStarted)
                         .count() /
                     1000.0);
    return S_OK;
}
CATCH_RETURN()

// The prompt state is produced by the prefill pipeline and consumed by the
// decode pipeline. Because the two pipelines own their state separately, the
// caller copies it once, after the Task has evaluated the prompt and emitted the
// first token.
inline HRESULT HandOffPackedState(HybridTextGenerationObjects& objects) noexcept
{
    return AdvanceCallerOwnedState(objects.decodeStateIn.Get(), objects.prefillStateOut.Get());
}

// The prefill graph emits one logits row per prompt token, and a bound output
// tensor must match the shape the graph will actually produce. The tensor is
// sized from the same prompt tokens the Task receives, so the prompt is never
// padded or truncated.
inline HRESULT BindPrefillLogitsForPrompt(HybridTextGenerationObjects& objects,
                                          UINT32 promptTokenCount) noexcept
try
{
    RETURN_HR_IF(E_INVALIDARG, promptTokenCount == 0);
    // Generation needs at least one free position after the prompt, because the
    // first generated token is produced by the prefill pass itself. A prompt
    // that fills capacity leaves nowhere to put it.
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 promptTokenCount >= objects.sequenceCapacity);

    objects.prefillLogits.Reset();
    RETURN_IF_FAILED(CreateLogitsTensor(objects.prefillTarget.Get(), promptTokenCount,
                                        objects.vocabularySize,
                                        objects.prefillLogits.GetAddressOf()));
    return objects.prefillStage->BindOutput(0, objects.prefillLogits.Get());
}
CATCH_RETURN()

// Start hybrid generation from the prompt tokens, copy the warmed prompt state
// once, advance decode state after each later token, and verify the terminal
// result.
inline HRESULT GenerateHybridText(HybridTextGenerationObjects& objects,
                                  IWinMLCancellationSource* cancellation,
                                  const std::vector<UINT32>& promptTokens, bool printFragments,
                                  TextGenerationResult& generation) noexcept
try
{
    generation = {};
    const UINT32 promptTokenCount = static_cast<UINT32>(promptTokens.size());
    HYBRID_STEP(BindPrefillLogitsForPrompt(objects, promptTokenCount), L"bind prefill logits");

    ComPtr<IWinMLTextGenerationPullStream> stream;
    const auto started = std::chrono::steady_clock::now();
    HYBRID_STEP(objects.task.session->GenerateTokens(promptTokenCount, promptTokens.data(),
                                                     objects.task.options.Get(), cancellation,
                                                     stream.GetAddressOf()),
                L"start generation");
    StreamCloser streamCloser(stream.Get());

    std::wstring streamedText;
    bool handedOffState = false;
    bool producedFirstToken = false;
    std::vector<UINT32> streamedTokens;
    for (;;)
    {
        WINML_TEXT_GENERATION_READ_STATUS status{};
        UINT32 tokenId = 0;
        LPCWSTR fragment = nullptr;
        HYBRID_STEP(stream->ReadNext(&status, &tokenId, &fragment), L"read stream");
        if (status == WINML_TEXT_GENERATION_READ_STATUS_COMPLETED)
        {
            break;
        }

        RETURN_HR_IF(E_UNEXPECTED, status != WINML_TEXT_GENERATION_READ_STATUS_UPDATE);
        RETURN_HR_IF_NULL(E_UNEXPECTED, fragment);
        if (!producedFirstToken)
        {
            generation.timeToFirstTokenMilliseconds =
                std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() -
                                                          started)
                    .count();
            producedFirstToken = true;
        }

        streamedTokens.push_back(tokenId);
        streamedText += fragment;
        if (printFragments)
        {
            std::wprintf(L"%ls", fragment);
            std::fflush(stdout);
        }

        // The first emitted token comes from the prefill pipeline, so the warmed
        // prompt state is final and is handed to decode. Every later token comes
        // from the decode pipeline, whose new state becomes its next input.
        if (!handedOffState)
        {
            HYBRID_STEP(HandOffPackedState(objects), L"hand off packed state");
            handedOffState = true;
        }
        else
        {
            HYBRID_STEP(
                AdvanceCallerOwnedState(objects.decodeStateIn.Get(), objects.decodeStateOut.Get()),
                L"advance decode state");
        }
    }

    const auto reportStep = [](HRESULT hr, LPCWSTR label) {
        if (FAILED(hr))
        {
            std::fwprintf(stderr, L"Hybrid text generation failed at %ls: 0x%08X\n", label,
                          static_cast<unsigned int>(hr));
        }

        return hr;
    };
    RETURN_IF_FAILED(ReadCompletedTextGenerationResult(stream.Get(), generation, reportStep));
    generation.totalMilliseconds =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started)
            .count();
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 generation.text != streamedText || generation.generatedTokenIds != streamedTokens);

    std::wprintf(L"\nGenerated token ids:");
    for (const UINT32 id : generation.generatedTokenIds)
    {
        std::wprintf(L" %u", id);
    }

    std::wprintf(L"\n");
    return S_OK;
}
CATCH_RETURN()

#undef HYBRID_STEP

} // namespace winmlsamples::tasks
