// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Utility helpers for the model-compilation Runtime sample.
//
// The sample-specific IWinMLCompileOutputSink and IWinMLResourceMapReader
// implementations stay in model-compilation/main.cpp. This header holds the
// supporting plumbing: temporary output-directory lifetime and a short run
// that confirms a compiled IWinMLModel can build, bind, run, and read output.

#pragma once

#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

#include <Windows.h>
#include <wil/result.h>

#include <WinMLRuntime.h>

#include "common.h"

namespace winmlsamples::compilation
{
struct TemporaryOutputDirectory
{
    std::filesystem::path path;
    bool removeOnDestroy = true;

    TemporaryOutputDirectory() = default;
    TemporaryOutputDirectory(const TemporaryOutputDirectory&) = delete;
    TemporaryOutputDirectory& operator=(const TemporaryOutputDirectory&) = delete;

    ~TemporaryOutputDirectory()
    {
        if (removeOnDestroy && !path.empty())
        {
            std::error_code error;
            std::filesystem::remove_all(path, error);
        }
    }

    HRESULT Create(const std::wstring& requestedPath) noexcept
    {
        if (!requestedPath.empty())
        {
            removeOnDestroy = false;
            std::error_code error;
            path = std::filesystem::absolute(requestedPath, error);
            RETURN_HR_IF(E_INVALIDARG, error);
            if (std::filesystem::exists(path, error))
            {
                const bool isDirectory = std::filesystem::is_directory(path, error);
                RETURN_HR_IF(E_INVALIDARG, error || !isDirectory);
                error.clear();
                const bool isEmpty = std::filesystem::is_empty(path, error);
                RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY), error || !isEmpty);
            }
            else
            {
                RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_CANNOT_MAKE),
                             !std::filesystem::create_directories(path, error) || error);
            }

            return S_OK;
        }

        wchar_t tempRoot[MAX_PATH + 1] = {};
        const DWORD tempRootLength = GetTempPathW(ARRAYSIZE(tempRoot), tempRoot);
        RETURN_LAST_ERROR_IF(tempRootLength == 0);
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER),
                     tempRootLength >= ARRAYSIZE(tempRoot));

        wchar_t temporaryName[MAX_PATH + 1] = {};
        RETURN_LAST_ERROR_IF(GetTempFileNameW(tempRoot, L"WML", 0, temporaryName) == 0);
        RETURN_LAST_ERROR_IF(DeleteFileW(temporaryName) == FALSE);
        RETURN_LAST_ERROR_IF(CreateDirectoryW(temporaryName, nullptr) == FALSE);
        path = temporaryName;
        return S_OK;
    }
};

inline HRESULT RunCompiledModel(IWinMLRuntime* runtime, IWinMLExecutionTarget* pipelineTarget,
                                IWinMLExecutionTarget* tensorTarget,
                                IWinMLModel* compiledModel) noexcept
{
    ComPtr<IWinMLPipelineBuilder> builder;
    CHECK_HR(runtime->CreatePipelineBuilder(builder.GetAddressOf()));

    ComPtr<IWinMLPipeline> pipeline;
    ComPtr<IWinMLStage> stage;
    CHECK_HR(builder->AddModelStage(compiledModel, pipelineTarget, L"compiled-model",
                                    stage.GetAddressOf()));
    CHECK_HR(stage->RequestOutput(0));
    CHECK_HR(builder->Build(pipeline.GetAddressOf()));

    ComPtr<IWinMLStageSchema> stageSchema;
    CHECK_HR(stage->QueryInterface(IID_PPV_ARGS(stageSchema.GetAddressOf())));

    UINT32 inputCount = 0;
    CHECK_HR(stageSchema->GetInputCount(&inputCount));

    std::vector<ComPtr<IWinMLTensor>> inputs(inputCount);
    std::vector<std::vector<float>> hostData(inputCount);
    std::vector<std::vector<UINT64>> hostDims(inputCount);
    for (UINT32 i = 0; i < inputCount; ++i)
    {
        WINML_TENSOR_DESC desc = {};
        CHECK_HR(stageSchema->GetInputTensorDesc(i, &desc));

        hostDims[i].resize(desc.dimensionCount);
        UINT64 elements = 1;
        for (UINT32 d = 0; d < desc.dimensionCount; ++d)
        {
            const UINT64 dim = (desc.dimensions[d] == UINT64_MAX) ? 1 : desc.dimensions[d];
            hostDims[i][d] = dim;
            elements *= dim;
        }

        hostData[i].assign(static_cast<size_t>(elements), 0.0f);
        for (size_t e = 0; e < hostData[i].size(); ++e)
        {
            hostData[i][e] = static_cast<float>(e % 7);
        }

        WINML_TENSOR_DESC concrete = {};
        concrete.dataType = WINML_TENSOR_DATA_TYPE_FLOAT32;
        concrete.dimensionCount = desc.dimensionCount;
        concrete.dimensions = hostDims[i].data();
        CHECK_HR(CreateTensorOnTarget(tensorTarget, &concrete, hostData[i].data(),
                                      static_cast<UINT64>(hostData[i].size() * sizeof(float)),
                                      inputs[i].GetAddressOf()));
        CHECK_HR(stage->BindInput(i, inputs[i].Get()));
    }

    CHECK_HR(pipeline->Run());

    ComPtr<IWinMLTensor> output;
    CHECK_HR(stage->GetOutput(0, output.GetAddressOf()));
    ComPtr<IWinMLTensorDataLock> lock;
    CHECK_HR(output->Lock(WINML_TENSOR_LOCK_MODE_READ, WINML_TENSOR_LOCK_FLAG_NONE,
                          lock.GetAddressOf()));

    BYTE* bytes = nullptr;
    UINT64 byteSize = 0;
    CHECK_HR(lock->GetData(&bytes, &byteSize));

    const float* values = reinterpret_cast<const float*>(bytes);
    const UINT64 count = byteSize / sizeof(float);
    wprintf(L"      output[0..%llu] =", (count < 4 ? count : 4));
    for (UINT64 i = 0; i < count && i < 4; ++i)
    {
        wprintf(L" %.4f", values[i]);
    }

    wprintf(L"\n");
    return S_OK;
}
} // namespace winmlsamples::compilation
