// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Language inference tensor helpers used by the C++ language samples.
//
// This file wraps target-bound token tensor creation, Runtime output readback,
// and deterministic logits selection for hello-language-model, llm-chat, and
// speech-to-language-model. It does not own conversation state.

#pragma once

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <vector>

#include <WinMLRuntime.h>

#include "common.h"
#include "numeric_utils.h"
#include "tensor_lock_utils.h"

// Creates the rank-2 INT64 token tensor that the split embedding stage binds as
// its input_ids value for one decode step.
static HRESULT CreateSingleTokenTensor(IWinMLExecutionTarget* target, UINT32 tokenId,
                                       IWinMLTensor** tensor)
{
    INT64 id = static_cast<INT64>(tokenId);
    UINT64 shape[] = {1, 1};
    WINML_TENSOR_DESC desc = {};
    desc.dataType = WINML_TENSOR_DATA_TYPE_INT64;
    desc.dimensionCount = 2;
    desc.dimensions = shape;
    return CreateTensorOnTarget(target, &desc, &id, sizeof(id), tensor);
}

// Gets the requested stage output, reads the final-position logits row, and
// returns host float32 values for application-side token selection.
static HRESULT ReadLastTokenLogits(IWinMLStage* outputStage, UINT32 outputIndex,
                                   std::vector<float>& logits,
                                   IWinMLExecutionTarget* readbackTarget = nullptr)
{
    if (!outputStage)
    {
        return E_POINTER;
    }

    logits.clear();
    ComPtr<IWinMLTensor> logitsTensor;
    CHECK_HR(outputStage->GetOutput(outputIndex, logitsTensor.GetAddressOf()));
    CHECK_HR_IF(!logitsTensor, E_UNEXPECTED);

    WINML_TENSOR_DESC desc = {};
    CHECK_HR(logitsTensor->GetDesc(&desc));
    CHECK_HR_IF(desc.dimensionCount == 0 || !desc.dimensions, E_UNEXPECTED);

    const UINT64 vocabSize64 = desc.dimensions[desc.dimensionCount - 1];
    CHECK_HR_IF(vocabSize64 == 0 || vocabSize64 > UINT32_MAX, E_UNEXPECTED);
    const UINT32 vocabSize = static_cast<UINT32>(vocabSize64);

    if (readbackTarget != nullptr && desc.dimensionCount >= 2)
    {
        const UINT32 positionAxis = desc.dimensionCount - 2;
        CHECK_HR_IF(desc.dimensions[positionAxis] == 0 ||
                        desc.dimensions[positionAxis] == UINT64_MAX,
                    E_UNEXPECTED);

        std::vector<UINT32> starts(desc.dimensionCount, 0);
        std::vector<UINT32> extents(desc.dimensionCount, 1);
        for (UINT32 axis = 0; axis < desc.dimensionCount; ++axis)
        {
            CHECK_HR_IF(desc.dimensions[axis] == 0 || desc.dimensions[axis] > UINT32_MAX,
                        E_UNEXPECTED);
        }

        starts[positionAxis] = static_cast<UINT32>(desc.dimensions[positionAxis] - 1);
        extents[desc.dimensionCount - 1] = vocabSize;

        ComPtr<IWinMLRawTensorFactory> factory;
        CHECK_HR(readbackTarget->QueryInterface(IID_PPV_ARGS(factory.GetAddressOf())));
        ComPtr<IWinMLTensor> rowTensor;
        CHECK_HR(factory->CreateTensorFromRegion(logitsTensor.Get(), starts.data(), extents.data(),
                                                 desc.dimensionCount, rowTensor.GetAddressOf()));
        logitsTensor = std::move(rowTensor);
        CHECK_HR(logitsTensor->GetDesc(&desc));
    }

    UINT64 elementCount = 1;
    for (UINT32 i = 0; i < desc.dimensionCount; ++i)
    {
        CHECK_HR_IF(desc.dimensions[i] == 0 || desc.dimensions[i] == UINT64_MAX, E_UNEXPECTED);
        CHECK_HR_IF(elementCount > UINT64_MAX / desc.dimensions[i], E_UNEXPECTED);
        elementCount *= desc.dimensions[i];
    }

    CHECK_HR_IF(elementCount < vocabSize, E_UNEXPECTED);
    CHECK_HR_IF(elementCount > UINT64_MAX / sizeof(float), E_UNEXPECTED);
    const UINT64 offset = elementCount - vocabSize;

    LockedTensorData locked;
    CHECK_HR(LockTensorForReadAny(logitsTensor.Get(), &locked));
    logits.resize(vocabSize);

    if (desc.dataType == WINML_TENSOR_DATA_TYPE_FLOAT32)
    {
        const float* values = locked.As<float>();
        CHECK_HR_IF(!values || locked.size < elementCount * sizeof(float), E_UNEXPECTED);
        std::copy(values + offset, values + offset + vocabSize, logits.begin());
        return S_OK;
    }

    if (desc.dataType == WINML_TENSOR_DATA_TYPE_FLOAT16)
    {
        const UINT16* values = locked.As<UINT16>();
        CHECK_HR_IF(!values || locked.size < elementCount * sizeof(UINT16), E_UNEXPECTED);
        for (UINT32 i = 0; i < vocabSize; ++i)
        {
            logits[i] = Float16ToFloat(values[offset + i]);
        }

        return S_OK;
    }

    wprintf(L"ERROR: Unsupported logits tensor data type %d.\n", static_cast<int>(desc.dataType));
    return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
}

// Greedy selection: pick the highest-logit token. The samples use argmax
// instead of sampling to keep replies repeatable.
static UINT32 ArgmaxToken(const std::vector<float>& logits)
{
    return static_cast<UINT32>(std::max_element(logits.begin(), logits.end()) - logits.begin());
}

// Uses IWinMLRawTensorFactory::CreateTensorFromRegion when available so the
// sample reads one logits row instead of the whole sequence tensor.
static HRESULT ReadFloat32LogitsAtPosition(IWinMLExecutionTarget* target,
                                           IWinMLTensor* logitsTensor, UINT32 position,
                                           UINT32 expectedVocabSize, std::vector<float>& logits)
{
    CHECK_HR_IF(!target || !logitsTensor, E_POINTER);

    WINML_TENSOR_DESC description = {};
    CHECK_HR(logitsTensor->GetDesc(&description));
    CHECK_HR_IF(description.dataType != WINML_TENSOR_DATA_TYPE_FLOAT32 ||
                    description.dimensionCount < 2 || description.dimensions == nullptr,
                HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED));

    const UINT32 positionAxis = description.dimensionCount - 2;
    const UINT32 vocabularyAxis = description.dimensionCount - 1;
    CHECK_HR_IF(description.dimensions[positionAxis] <= position ||
                    description.dimensions[vocabularyAxis] != expectedVocabSize,
                HRESULT_FROM_WIN32(ERROR_INVALID_DATA));

    std::vector<UINT32> starts(description.dimensionCount, 0);
    std::vector<UINT32> extents(description.dimensionCount, 1);
    for (UINT32 axis = 0; axis < description.dimensionCount; ++axis)
    {
        CHECK_HR_IF(description.dimensions[axis] == 0 || description.dimensions[axis] > UINT32_MAX,
                    HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    }

    starts[positionAxis] = position;
    extents[vocabularyAxis] = expectedVocabSize;

    ComPtr<IWinMLRawTensorFactory> factory;
    CHECK_HR(target->QueryInterface(IID_PPV_ARGS(factory.GetAddressOf())));
    ComPtr<IWinMLTensor> rowTensor;
    const HRESULT regionHr =
        factory->CreateTensorFromRegion(logitsTensor, starts.data(), extents.data(),
                                        description.dimensionCount, rowTensor.GetAddressOf());

    LockedTensorData rowData;
    UINT64 valueOffset = 0;
    if (regionHr == HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED))
    {
        // Some compiled providers cannot materialize a tensor region. Preserve
        // correctness with one synchronized read of the original tensor; all
        // other region failures remain errors.
        CHECK_HR(LockTensorForReadAny(logitsTensor, &rowData));
        valueOffset = static_cast<UINT64>(position) * static_cast<UINT64>(expectedVocabSize);
    }
    else
    {
        CHECK_HR(regionHr);
        CHECK_HR(LockTensorForReadAny(rowTensor.Get(), &rowData));
    }

    const float* values = rowData.As<const float>();
    CHECK_HR_IF(
        values == nullptr || valueOffset > UINT64_MAX - static_cast<UINT64>(expectedVocabSize) ||
            rowData.size < (valueOffset + static_cast<UINT64>(expectedVocabSize)) * sizeof(float),
        E_UNEXPECTED);
    logits.assign(values + valueOffset, values + valueOffset + expectedVocabSize);
    return S_OK;
}
