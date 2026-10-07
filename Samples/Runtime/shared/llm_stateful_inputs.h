// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Stateful decoder input helpers used by the split C++ language sample.
//
// This file wraps the Runtime tensor API for per-step position and attention
// mask inputs, plus schema inspection that discovers context length and logits
// vocabulary for language/llm-chat.

#pragma once

#include <cstdint>
#include <cstring>
#include <limits>
#include <vector>

#include <WinMLRuntime.h>
#include <WinMLRuntimeOrt.h>

#include "common.h"
#include "numeric_utils.h"

// Creates the per-step attention-mask tensor bound to the decoder stage.
// Positions up to and
// including currentPosition are visible (0.0); the rest are masked with a large
// negative value so the decoder ignores unwritten KV-cache slots.
static HRESULT CreateAttentionMaskTensor(IWinMLExecutionTarget* target, UINT32 maxCacheLength,
                                         UINT32 currentPosition, WINML_TENSOR_DATA_TYPE dataType,
                                         IWinMLTensor** tensor)
{
    if (!target || !tensor)
    {
        return E_POINTER;
    }

    if (maxCacheLength == 0 || currentPosition >= maxCacheLength)
    {
        return E_INVALIDARG;
    }

    UINT64 shape[] = {1, 1, 1, maxCacheLength};
    WINML_TENSOR_DESC desc = {};
    desc.dataType = dataType;
    desc.dimensionCount = 4;
    desc.dimensions = shape;

    if (dataType == WINML_TENSOR_DATA_TYPE_FLOAT16)
    {
        std::vector<UINT16> mask(maxCacheLength, FloatToFloat16(-65504.0f));
        for (UINT32 i = 0; i <= currentPosition; ++i)
        {
            mask[i] = FloatToFloat16(0.0f);
        }

        return CreateTensorOnTarget(target, &desc, mask.data(),
                                    static_cast<UINT64>(mask.size() * sizeof(UINT16)), tensor);
    }

    if (dataType == WINML_TENSOR_DATA_TYPE_FLOAT32)
    {
        std::vector<float> mask(maxCacheLength, std::numeric_limits<float>::lowest());
        for (UINT32 i = 0; i <= currentPosition; ++i)
        {
            mask[i] = 0.0f;
        }

        return CreateTensorOnTarget(target, &desc, mask.data(),
                                    static_cast<UINT64>(mask.size() * sizeof(float)), tensor);
    }

    return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
}

// Creates the 1D INT64 position_id tensor bound beside the token state.
static HRESULT CreateSingleInt64Tensor1D(IWinMLExecutionTarget* target, INT64 value,
                                         IWinMLTensor** tensor)
{
    if (!target || !tensor)
    {
        return E_POINTER;
    }

    UINT64 shape[] = {1};
    WINML_TENSOR_DESC desc = {};
    desc.dataType = WINML_TENSOR_DATA_TYPE_INT64;
    desc.dimensionCount = 1;
    desc.dimensions = shape;
    return CreateTensorOnTarget(target, &desc, &value, sizeof(value), tensor);
}

// Reads the context length from the decoder stage's materialized tensor shapes,
// checking the attention mask or KV-cache input dimensions.
//
// Shapes live on the materialized stage's IWinMLStageSchema capability (the
// model schema core is ordinal-only and has no shape information); input
// ordinals are resolved ahead of time from the decoder model's
// IWinMLOrtModelSchema (ONNX name reflection). If the artifact does not carry
// the expected tensor names, this helper returns false and the caller keeps its
// default context length.
static bool TryReadContextLengthFromDecoderStage(IWinMLModel* decoderModel,
                                                 IWinMLStage* decoderStage, UINT32* contextLength)
{
    if (!decoderModel || !decoderStage || !contextLength)
    {
        return false;
    }

    ComPtr<IWinMLOrtModelSchema> ortSchema;
    if (FAILED(decoderModel->QueryInterface(IID_PPV_ARGS(ortSchema.GetAddressOf()))))
    {
        return false;
    }

    ComPtr<IWinMLStageSchema> stageSchema;
    if (FAILED(decoderStage->QueryInterface(IID_PPV_ARGS(stageSchema.GetAddressOf()))))
    {
        return false;
    }

    UINT32 idx = 0;
    WINML_TENSOR_DESC desc = {};
    if (SUCCEEDED(ortSchema->FindInputIndex(L"attention_mask", &idx)) &&
        SUCCEEDED(stageSchema->GetInputTensorDesc(idx, &desc)) && desc.dimensions &&
        desc.dimensionCount >= 4)
    {
        const UINT64 maxSeq = desc.dimensions[3];
        if (maxSeq > 0 && maxSeq != UINT64_MAX && maxSeq <= UINT32_MAX)
        {
            *contextLength = static_cast<UINT32>(maxSeq);
            return true;
        }
    }

    desc = {};
    if (SUCCEEDED(ortSchema->FindInputIndex(L"past_key_values.0.key", &idx)) &&
        SUCCEEDED(stageSchema->GetInputTensorDesc(idx, &desc)) && desc.dimensions &&
        desc.dimensionCount >= 4)
    {
        const UINT64 maxSeq = desc.dimensions[2];
        if (maxSeq > 0 && maxSeq != UINT64_MAX && maxSeq <= UINT32_MAX)
        {
            *contextLength = static_cast<UINT32>(maxSeq);
            return true;
        }
    }

    return false;
}

// Reads the vocabulary size from the resolved head logits output (last dim).
//
// Like the context length, this comes from the materialized stage's
// IWinMLStageSchema capability.
static bool TryReadVocabFromHeadStage(IWinMLStage* headStage, UINT32 logitsOutputIndex,
                                      UINT32* vocabSize)
{
    if (!headStage || !vocabSize)
    {
        return false;
    }

    ComPtr<IWinMLStageSchema> stageSchema;
    if (FAILED(headStage->QueryInterface(IID_PPV_ARGS(stageSchema.GetAddressOf()))))
    {
        return false;
    }

    WINML_TENSOR_DESC desc = {};
    if (FAILED(stageSchema->GetOutputTensorDesc(logitsOutputIndex, &desc)) ||
        desc.dimensionCount == 0 || !desc.dimensions)
    {
        return false;
    }

    const UINT64 lastDim = desc.dimensions[desc.dimensionCount - 1];
    if (lastDim > 0 && lastDim != UINT64_MAX && lastDim <= UINT32_MAX)
    {
        *vocabSize = static_cast<UINT32>(lastDim);
        return true;
    }

    return false;
}

// Creates and binds the per-step position_id and attention_mask tensors before
// each pipeline Run in the manual decode loop.
static HRESULT BindDecoderStepInputs(IWinMLExecutionTarget* target, IWinMLStage* decoderStage,
                                     UINT32 positionIdIndex, UINT32 attentionMaskIndex,
                                     UINT32 contextLength,
                                     WINML_TENSOR_DATA_TYPE attentionMaskDataType, UINT64 position)
{
    ComPtr<IWinMLTensor> positionTensor;
    ComPtr<IWinMLTensor> attentionMaskTensor;
    CHECK_HR(CreateSingleInt64Tensor1D(target, static_cast<INT64>(position),
                                       positionTensor.GetAddressOf()));
    CHECK_HR(CreateAttentionMaskTensor(target, contextLength, static_cast<UINT32>(position),
                                       attentionMaskDataType, attentionMaskTensor.GetAddressOf()));
    CHECK_HR(decoderStage->BindInput(positionIdIndex, positionTensor.Get()));
    CHECK_HR(decoderStage->BindInput(attentionMaskIndex, attentionMaskTensor.Get()));
    return S_OK;
}

static UINT64 CountTensorElements(const std::vector<UINT64>& shape)
{
    if (shape.empty())
    {
        return 0;
    }

    UINT64 elementCount = 1;
    for (UINT64 dimension : shape)
    {
        if (dimension == 0 || dimension == UINT64_MAX)
        {
            return 0;
        }

        if (elementCount > UINT64_MAX / dimension)
        {
            return 0;
        }

        elementCount *= dimension;
    }

    return elementCount;
}

static UINT32 TensorElementSizeInBytes(WINML_TENSOR_DATA_TYPE dataType)
{
    switch (dataType)
    {
    case WINML_TENSOR_DATA_TYPE_INT32:
    case WINML_TENSOR_DATA_TYPE_FLOAT32:
        return 4;
    case WINML_TENSOR_DATA_TYPE_INT64:
        return 8;
    case WINML_TENSOR_DATA_TYPE_FLOAT16:
        return 2;
    default:
        return 0;
    }
}

static HRESULT CreateTensorFromShape(IWinMLExecutionTarget* target, WINML_TENSOR_DATA_TYPE dataType,
                                     const std::vector<UINT64>& shape, const void* data,
                                     UINT64 byteCount, IWinMLTensor** tensor)
{
    if (!target || !tensor)
    {
        return E_POINTER;
    }

    const UINT64 elementCount = CountTensorElements(shape);
    CHECK_HR_IF(elementCount == 0, E_INVALIDARG);

    const UINT32 elementSize = TensorElementSizeInBytes(dataType);
    CHECK_HR_IF(elementSize == 0, HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED));
    CHECK_HR_IF(data == nullptr && byteCount != 0, E_POINTER);
    CHECK_HR_IF(data != nullptr && byteCount != elementCount * static_cast<UINT64>(elementSize),
                E_INVALIDARG);

    WINML_TENSOR_DESC desc = {};
    desc.dataType = dataType;
    desc.dimensionCount = static_cast<UINT32>(shape.size());
    desc.dimensions = shape.data();
    return CreateTensorOnTarget(target, &desc, data, byteCount, tensor);
}

static HRESULT CopyTensorContents(IWinMLTensor* destination, IWinMLTensor* source)
{
    if (!destination || !source)
    {
        return E_POINTER;
    }

    ComPtr<IWinMLMutableTensor> mutableTensor;
    CHECK_HR(destination->QueryInterface(IID_PPV_ARGS(mutableTensor.GetAddressOf())));
    return mutableTensor->CopyFrom(source);
}

static HRESULT FillTensorElements(IWinMLTensor* tensor, const void* value, UINT32 valueSizeInBytes)
{
    if (!tensor || !value)
    {
        return E_POINTER;
    }

    ComPtr<IWinMLMutableTensor> mutableTensor;
    CHECK_HR(tensor->QueryInterface(IID_PPV_ARGS(mutableTensor.GetAddressOf())));
    return mutableTensor->Fill(value, valueSizeInBytes);
}

static HRESULT LockTensorForWriteAny(IWinMLTensor* tensor, LockedTensorData* lockedData)
{
    HRESULT hr = LockTensorData(tensor, WINML_TENSOR_LOCK_MODE_WRITE, WINML_TENSOR_LOCK_FLAG_NONE,
                                lockedData);
    if (hr != HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED))
    {
        return hr;
    }

    return LockTensorData(tensor, WINML_TENSOR_LOCK_MODE_WRITE,
                          WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS, lockedData);
}

static HRESULT CommitSynchronizedTensorWrite(const LockedTensorData& lockedData)
{
    if (!lockedData.lock)
    {
        return E_POINTER;
    }

    ComPtr<IWinMLTensorSynchronizedDataLock> synchronizedLock;
    const HRESULT queryHr =
        lockedData.lock->QueryInterface(IID_PPV_ARGS(synchronizedLock.GetAddressOf()));
    if (FAILED(queryHr))
    {
        return queryHr == E_NOINTERFACE ? S_OK : queryHr;
    }

    return synchronizedLock->Commit();
}

static HRESULT WriteTensorBytes(IWinMLTensor* tensor, const void* data, UINT64 byteCount)
{
    if (!tensor || !data)
    {
        return E_POINTER;
    }

    LockedTensorData locked;
    CHECK_HR(LockTensorForWriteAny(tensor, &locked));
    CHECK_HR_IF(locked.data == nullptr || locked.size != byteCount, E_INVALIDARG);
    memcpy(locked.data, data, static_cast<size_t>(byteCount));
    return CommitSynchronizedTensorWrite(locked);
}

static HRESULT ReadInt32ScalarTensor(IWinMLTensor* tensor, INT32* value)
{
    if (!tensor || !value)
    {
        return E_POINTER;
    }

    LockedTensorData locked;
    CHECK_HR(LockTensorForReadAny(tensor, &locked));
    CHECK_HR_IF(locked.data == nullptr || locked.size < sizeof(INT32), E_INVALIDARG);
    *value = locked.As<INT32>()[0];
    return S_OK;
}
