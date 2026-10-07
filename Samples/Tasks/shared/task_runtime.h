// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared Runtime helpers for Task samples. These wrap IWinMLRuntime pipeline
// construction, Runtime identity checks, and pull-stream closure used by
// the language, speech, and composition samples.

#pragma once

#include <WinMLRuntime.h>
#include <WinMLRuntimeOrt.h>
#include <WinMLTasks.h>

#include <span>
#include <utility>

#include <wil/result.h>
#include <wrl/client.h>

namespace winmlsamples::tasks
{

using Microsoft::WRL::ComPtr;

// Pull streams hold execution resources until Close is called. Keeping that
// lifetime rule in one small type makes the scenario code easier to follow.
template <typename T>
class StreamCloser
{
public:
    explicit StreamCloser(T* stream) noexcept : stream_(stream)
    {
    }

    ~StreamCloser()
    {
        if (stream_ != nullptr)
        {
            LOG_IF_FAILED(stream_->Close());
        }
    }

    StreamCloser(const StreamCloser&) = delete;
    StreamCloser& operator=(const StreamCloser&) = delete;

private:
    T* stream_ = nullptr;
};

// Compare COM identity pointers to prove two interfaces refer to the same
// underlying Runtime object.
inline HRESULT VerifySameIdentity(IUnknown* left, IUnknown* right) noexcept
{
    RETURN_HR_IF_NULL(E_POINTER, left);
    RETURN_HR_IF_NULL(E_POINTER, right);

    ComPtr<IUnknown> leftIdentity;
    ComPtr<IUnknown> rightIdentity;
    RETURN_IF_FAILED(left->QueryInterface(IID_PPV_ARGS(leftIdentity.GetAddressOf())));
    RETURN_IF_FAILED(right->QueryInterface(IID_PPV_ARGS(rightIdentity.GetAddressOf())));
    RETURN_HR_IF(E_UNEXPECTED, leftIdentity.Get() != rightIdentity.Get());
    return S_OK;
}

// Task sessions disclose the Runtime instance that owns them. Samples verify
// that callers retain control of the Runtime used to construct the Task.
inline HRESULT VerifyExactRuntime(IUnknown* taskObject, IWinMLRuntime* expectedRuntime) noexcept
{
    RETURN_HR_IF_NULL(E_POINTER, taskObject);
    RETURN_HR_IF_NULL(E_POINTER, expectedRuntime);

    ComPtr<IWinMLTaskRuntimeIdentity> disclosure;
    RETURN_IF_FAILED(taskObject->QueryInterface(IID_PPV_ARGS(disclosure.GetAddressOf())));
    ComPtr<IWinMLRuntime> actualRuntime;
    RETURN_IF_FAILED(disclosure->GetRuntime(actualRuntime.GetAddressOf()));
    return VerifySameIdentity(expectedRuntime, actualRuntime.Get());
}

// This is the smallest useful Runtime pipeline: load one model, place it on a
// target, optionally resolve symbolic dimensions or pass ONNX Runtime session
// settings, and build the pipeline.
inline HRESULT BuildSingleStagePipeline(
    IWinMLRuntime* runtime, IWinMLExecutionTarget* target, LPCWSTR modelPath, LPCWSTR debugName,
    std::span<const std::pair<LPCWSTR, INT64>> symbolicDimensions, ComPtr<IWinMLModel>& model,
    ComPtr<IWinMLPipeline>& pipeline, ComPtr<IWinMLStage>& stage,
    std::span<const std::pair<LPCWSTR, LPCWSTR>> ortSessionConfig = {}) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, runtime);
    RETURN_HR_IF_NULL(E_POINTER, target);
    RETURN_HR_IF_NULL(E_POINTER, modelPath);
    RETURN_HR_IF_NULL(E_POINTER, debugName);

    model.Reset();
    pipeline.Reset();
    stage.Reset();
    RETURN_IF_FAILED(runtime->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));

    ComPtr<IWinMLPipelineBuilder> builder;
    RETURN_IF_FAILED(runtime->CreatePipelineBuilder(builder.GetAddressOf()));
    RETURN_IF_FAILED(builder->AddModelStage(model.Get(), target, debugName, stage.GetAddressOf()));

    if (!symbolicDimensions.empty())
    {
        ComPtr<IWinMLOnnxSymbolicDimensionOverrides> overrides;
        RETURN_IF_FAILED(stage.As(&overrides));
        for (const auto& [name, extent] : symbolicDimensions)
        {
            RETURN_IF_FAILED(overrides->SetOverride(name, extent));
        }
    }

    if (!ortSessionConfig.empty())
    {
        ComPtr<IWinMLOrtStageOptions> ortOptions;
        RETURN_IF_FAILED(stage.As(&ortOptions));
        for (const auto& [key, value] : ortSessionConfig)
        {
            RETURN_IF_FAILED(ortOptions->SetSessionConfigEntry(key, value));
        }
    }

    return builder->Build(pipeline.GetAddressOf());
}
CATCH_RETURN()

// Build a one-stage Runtime pipeline from a model the caller has already loaded
// (for example, after reading its schema).
inline HRESULT BuildSingleStagePipelineFromModel(IWinMLRuntime* runtime,
                                                 IWinMLExecutionTarget* target, IWinMLModel* model,
                                                 LPCWSTR debugName,
                                                 ComPtr<IWinMLPipeline>& pipeline,
                                                 ComPtr<IWinMLStage>& stage) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, runtime);
    RETURN_HR_IF_NULL(E_POINTER, target);
    RETURN_HR_IF_NULL(E_POINTER, model);
    RETURN_HR_IF_NULL(E_POINTER, debugName);

    pipeline.Reset();
    stage.Reset();
    ComPtr<IWinMLPipelineBuilder> builder;
    RETURN_IF_FAILED(runtime->CreatePipelineBuilder(builder.GetAddressOf()));
    RETURN_IF_FAILED(builder->AddModelStage(model, target, debugName, stage.GetAddressOf()));
    return builder->Build(pipeline.GetAddressOf());
}
CATCH_RETURN()

} // namespace winmlsamples::tasks
