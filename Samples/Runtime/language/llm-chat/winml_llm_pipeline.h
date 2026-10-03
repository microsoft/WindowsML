// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Split language pipeline helper used by language/llm-chat/main.cpp.
//
// This file wraps the Runtime API surface that builds emb.onnx -> decoder.onnx
// -> head.onnx, resolves ONNX names to positional ordinals with
// IWinMLOrtModelSchema, declares decoder KV state with
// IWinMLStatefulStageOptions, then builds the pipeline.
//
// Learn more (paths relative to this file)
//   ../../../../docs/Runtime/tutorials/03-language-models.md
//   ../../../../docs/api-reference/CommonPatterns.md (patterns 3, 4, and 11)
//   ../../../../docs/api-reference/IWinMLStatefulStageOptions.md
//   ../../../../docs/api-reference/IWinMLOrtModelSchema.md

#pragma once

#include <string>
#include <functional>
#include <limits>
#include <memory>
#include <utility>
#include <vector>

#include <Windows.h>
#include <wrl/client.h>

#include <WinMLRuntime.h>
#include <WinMLRuntimeOrt.h>
#include <WinMLTokenizer.h>

#include "common.h"
#include "execution_target_utils.h" // CreateExecutionTarget
#include "llm_stateful_inputs.h"    // context-length / vocab readers, per-step mask binding

using Microsoft::WRL::ComPtr;

using LlmTokenSink = std::function<bool(const wchar_t*)>;

// Everything the per-step decode loop needs after the graph is built. Stage
// pointers are owned by the pipeline; the indices are resolved once by name
// before Build.
struct LlmPipeline
{
    ComPtr<IWinMLModel> embModel;
    ComPtr<IWinMLModel> decoderModel;
    ComPtr<IWinMLModel> headModel;
    ComPtr<IWinMLRuntime> runtime;
    ComPtr<IWinMLPipeline> pipeline;
    ComPtr<IWinMLStage> embStage;
    ComPtr<IWinMLStage> decoderStage;
    ComPtr<IWinMLStage> headStage;
    ComPtr<IWinMLStatefulStage> statefulDecoder;
    ComPtr<IWinMLExecutionTarget> cpuTarget;     // creates CPU-visible input tensors
    ComPtr<IWinMLExecutionTarget> decoderTarget; // exact placement target for decoderStage
    ComPtr<IWinMLExecutionTarget>
        decoderTensorTarget; // underlying hardware target for decoder input tensors

    UINT32 embInputIdsIndex = 0;
    UINT32 decoderPositionIdIndex = 0;
    UINT32 decoderAttentionMaskIndex = 0;
    UINT32 headLogitsIndex = 0;
    WINML_TENSOR_DATA_TYPE attentionMaskDataType = WINML_TENSOR_DATA_TYPE_FLOAT16;

    UINT32 contextLength = 4096; // fixed decode capacity
    UINT32 vocabSize = 0;        // read from the head stage's logits dimension
};

namespace winml_llm_detail
{
inline std::wstring JoinPath(const std::wstring& dir, const wchar_t* file)
{
    std::wstring path = dir;
    if (!path.empty() && path.back() != L'\\' && path.back() != L'/')
    {
        path += L'\\';
    }

    return path + file;
}

// Setup-time ONNX name lookup: execution remains positional after this.
inline HRESULT FindModelInputIndex(IWinMLModel* model, LPCWSTR name, UINT32* index)
{
    ComPtr<IWinMLOrtModelSchema> schema;
    CHECK_HR(model->QueryInterface(IID_PPV_ARGS(schema.GetAddressOf())));
    return schema->FindInputIndex(name, index);
}

// Resolve the logits output once so the hot loop can use stage ordinals.
inline HRESULT FindModelOutputIndex(IWinMLModel* model, LPCWSTR name, UINT32* index)
{
    ComPtr<IWinMLOrtModelSchema> schema;
    CHECK_HR(model->QueryInterface(IID_PPV_ARGS(schema.GetAddressOf())));
    return schema->FindOutputIndex(name, index);
}

} // namespace winml_llm_detail

// Loads the three ONNX artifacts, connects their stages, declares the decoder's
// Runtime-managed KV-cache pairs, requests logits, and builds the pipeline.
inline HRESULT BuildLlmPipeline(IWinMLRuntime* runtime, const std::wstring& modelDir,
                                const DeviceArgs& device, LlmPipeline& out)
{
    using namespace winml_llm_detail;

    const std::wstring embPath = JoinPath(modelDir, L"emb.onnx");
    const std::wstring decoderPath = JoinPath(modelDir, L"decoder.onnx");
    const std::wstring headPath = JoinPath(modelDir, L"head.onnx");
    if (!FileExists(embPath) || !FileExists(decoderPath) || !FileExists(headPath))
    {
        wprintf(L"ERROR: Model directory must contain emb.onnx, decoder.onnx, and head.onnx.\n");
        return E_INVALIDARG;
    }

    out.runtime = runtime;

    ComPtr<IWinMLExecutionTarget> cpuTarget;
    CHECK_HR(runtime->CreateCpuExecutionTarget(cpuTarget.GetAddressOf()));

    ComPtr<IWinMLExecutionTarget> decoderTarget;
    ComPtr<IWinMLExecutionTarget> decoderTensorTarget;
    CHECK_HR(CreateExecutionTarget(runtime, device, decoderTarget.GetAddressOf(),
                                   decoderTensorTarget.GetAddressOf()));

    ComPtr<IWinMLModel> embSourceModel;
    ComPtr<IWinMLModel> decoderSourceModel;
    ComPtr<IWinMLModel> headSourceModel;
    CHECK_HR(runtime->LoadModelFromFile(embPath.c_str(), nullptr, embSourceModel.GetAddressOf()));
    CHECK_HR(runtime->LoadModelFromFile(decoderPath.c_str(), nullptr,
                                        decoderSourceModel.GetAddressOf()));
    CHECK_HR(runtime->LoadModelFromFile(headPath.c_str(), nullptr, headSourceModel.GetAddressOf()));
    CHECK_HR(FindModelInputIndex(embSourceModel.Get(), L"input_ids", &out.embInputIdsIndex));
    CHECK_HR(
        FindModelInputIndex(decoderSourceModel.Get(), L"position_id", &out.decoderPositionIdIndex));
    CHECK_HR(FindModelInputIndex(decoderSourceModel.Get(), L"attention_mask",
                                 &out.decoderAttentionMaskIndex));
    CHECK_HR(FindModelOutputIndex(headSourceModel.Get(), L"logits", &out.headLogitsIndex));

    out.embModel = embSourceModel;
    out.decoderModel = decoderSourceModel;
    out.headModel = headSourceModel;

    ComPtr<IWinMLPipelineBuilder> builder;
    CHECK_HR(runtime->CreatePipelineBuilder(builder.GetAddressOf()));

    ComPtr<IWinMLStage> builderEmb;
    ComPtr<IWinMLStage> builderDecoder;
    ComPtr<IWinMLStage> builderHead;
    CHECK_HR(builder->AddModelStage(out.embModel.Get(), cpuTarget.Get(), L"llm-emb",
                                    builderEmb.GetAddressOf()));
    CHECK_HR(builder->AddModelStage(out.decoderModel.Get(), decoderTarget.Get(), L"llm-decoder",
                                    builderDecoder.GetAddressOf()));
    CHECK_HR(builder->AddModelStage(out.headModel.Get(), cpuTarget.Get(), L"llm-head",
                                    builderHead.GetAddressOf()));

    ComPtr<IWinMLModelSchema> decoderDeclaredSchema;
    CHECK_HR(
        decoderSourceModel->QueryInterface(IID_PPV_ARGS(decoderDeclaredSchema.GetAddressOf())));
    UINT32 decoderInputCount = 0;
    UINT32 decoderOutputCount = 0;
    CHECK_HR(decoderDeclaredSchema->GetInputCount(&decoderInputCount));
    CHECK_HR(decoderDeclaredSchema->GetOutputCount(&decoderOutputCount));
    CHECK_HR_IF(decoderInputCount < 3 || decoderOutputCount < 1 ||
                    decoderInputCount - 3 != decoderOutputCount - 1,
                HRESULT_FROM_WIN32(ERROR_INVALID_DATA));

    // State tensor pairs must be declared before Build; otherwise the past and
    // present KV tensors are ordinary model I/O and the sample has nothing to
    // retain across token steps.
    ComPtr<IWinMLStatefulStageOptions> decoderOptions;
    CHECK_HR(builderDecoder->QueryInterface(IID_PPV_ARGS(decoderOptions.GetAddressOf())));
    for (UINT32 stateIndex = 0; stateIndex < decoderInputCount - 3; ++stateIndex)
    {
        CHECK_HR(decoderOptions->AddStateTensorPair(3 + stateIndex, 1 + stateIndex));
    }

    // Pattern 4 composition: connect embedding output to decoder input, then
    // decoder hidden state to the head stage; the head logits are retained.
    CHECK_HR(builder->Connect(builderEmb.Get(), 0, builderDecoder.Get(), 0));
    CHECK_HR(builder->Connect(builderDecoder.Get(), 0, builderHead.Get(), 0));
    CHECK_HR(builderHead->RequestOutput(out.headLogitsIndex));
    CHECK_HR(builder->Build(out.pipeline.GetAddressOf()));

    out.embStage = builderEmb;
    out.decoderStage = builderDecoder;
    out.headStage = builderHead;
    out.cpuTarget = cpuTarget;
    out.decoderTarget = decoderTarget;
    out.decoderTensorTarget = decoderTensorTarget;

    CHECK_HR(out.decoderStage->QueryInterface(IID_PPV_ARGS(out.statefulDecoder.GetAddressOf())));

    ComPtr<IWinMLStageSchema> decoderSchema;
    CHECK_HR(out.decoderStage->QueryInterface(IID_PPV_ARGS(decoderSchema.GetAddressOf())));
    WINML_TENSOR_DESC attentionMaskDesc = {};
    CHECK_HR(decoderSchema->GetInputTensorDesc(out.decoderAttentionMaskIndex, &attentionMaskDesc));
    CHECK_HR_IF(attentionMaskDesc.dataType != WINML_TENSOR_DATA_TYPE_FLOAT16 &&
                    attentionMaskDesc.dataType != WINML_TENSOR_DATA_TYPE_FLOAT32,
                HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED));
    out.attentionMaskDataType = attentionMaskDesc.dataType;

    if (!TryReadContextLengthFromDecoderStage(decoderSourceModel.Get(), out.decoderStage.Get(),
                                              &out.contextLength))
    {
        wprintf(L"ERROR: the decoder stage schema does not expose a fixed sequence capacity.\n");
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    if (!TryReadVocabFromHeadStage(out.headStage.Get(), out.headLogitsIndex, &out.vocabSize))
    {
        wprintf(
            L"ERROR: the head stage schema does not expose a fixed logits vocabulary dimension.\n");
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    // Ask the built stateful stage to use the fixed cache length exported by
    // the model; backends that cannot resize report NOT_SUPPORTED.
    const HRESULT capacityHr = out.statefulDecoder->SetSequenceCapacity(out.contextLength);
    CHECK_HR_IF(FAILED(capacityHr) && capacityHr != HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                capacityHr);

    return S_OK;
}
