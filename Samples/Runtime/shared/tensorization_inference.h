// Copyright (C) Microsoft Corporation. All rights reserved.
//
// tensorization_inference.h - Optional inference helpers for Runtime samples.
//
// Wraps model loading, one-stage pipeline construction, raw tensor creation,
// named bindings, and backend diagnostics for samples that retarget inference
// independently from tensorization.

#pragma once

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <initializer_list>
#include <limits>
#include <string>
#include <vector>

#include <Windows.h>

#include <WinMLRuntime.h>
#include <WinMLRuntimeOrt.h>
#include <WinMLTensor.h>
#include <winml/onnxruntime_c_api.h>

#include "common.h"           // DeviceArgs
#include "ep_catalog_utils.h" // PrepareAndValidateExecutionProviders

namespace winmlsamples
{
namespace tensors
{

inline size_t ElementCountFromDims(const std::vector<UINT64>& dims) noexcept
{
    if (dims.empty())
    {
        return 0;
    }

    size_t count = 1;
    for (UINT64 dim : dims)
    {
        if (dim == 0 || dim > static_cast<UINT64>(std::numeric_limits<size_t>::max() / count))
        {
            return 0;
        }

        count *= static_cast<size_t>(dim);
    }

    return count;
}

inline HRESULT GetTensorShape(IWinMLTensor* tensor, std::vector<UINT64>& dims,
                              WINML_TENSOR_DATA_TYPE* dataType = nullptr) noexcept
{
    if (tensor == nullptr)
    {
        return E_POINTER;
    }

    WINML_TENSOR_DESC desc{};
    CHECK_HR(tensor->GetDesc(&desc));
    if (desc.dimensionCount == 0 || desc.dimensions == nullptr)
    {
        return E_FAIL;
    }

    dims.assign(desc.dimensions, desc.dimensions + desc.dimensionCount);
    if (dataType != nullptr)
    {
        *dataType = desc.dataType;
    }

    return S_OK;
}

// Creates a raw tensor on the supplied target through IWinMLRawTensorFactory.
// The descriptor dimensions must already be concrete and match byteCount.
inline HRESULT CreateTensorFromBuffer(IWinMLExecutionTarget* target,
                                      WINML_TENSOR_DATA_TYPE dataType,
                                      const std::vector<UINT64>& dims, const void* data,
                                      UINT64 byteCount, IWinMLTensor** tensor) noexcept
{
    if (target == nullptr || tensor == nullptr)
    {
        return E_POINTER;
    }

    *tensor = nullptr;
    if (dims.empty() || data == nullptr || byteCount == 0)
    {
        return E_INVALIDARG;
    }

    WINML_TENSOR_DESC desc{};
    desc.dataType = dataType;
    desc.dimensionCount = static_cast<UINT32>(dims.size());
    desc.dimensions = const_cast<UINT64*>(dims.data());
    return CreateTensorOnTarget(target, &desc, data, byteCount, tensor);
}

// Creates a raw tensor from typed values on the supplied target.
template <typename T>
inline HRESULT CreateTensorFromValues(IWinMLExecutionTarget* target,
                                      WINML_TENSOR_DATA_TYPE dataType,
                                      const std::vector<UINT64>& dims, const std::vector<T>& values,
                                      IWinMLTensor** tensor) noexcept
{
    return CreateTensorFromBuffer(target, dataType, dims, values.data(),
                                  static_cast<UINT64>(values.size() * sizeof(T)), tensor);
}

// Convenience overload: creates a CPU target from the Runtime, then allocates the
// raw tensor on that target.
inline HRESULT CreateTensorFromBuffer(IWinMLRuntime* runtime, WINML_TENSOR_DATA_TYPE dataType,
                                      const std::vector<UINT64>& dims, const void* data,
                                      UINT64 byteCount, IWinMLTensor** tensor) noexcept
{
    if (runtime == nullptr)
    {
        return E_POINTER;
    }

    ComPtr<IWinMLExecutionTarget> cpuTarget;
    CHECK_HR(runtime->CreateCpuExecutionTarget(cpuTarget.GetAddressOf()));
    return CreateTensorFromBuffer(cpuTarget.Get(), dataType, dims, data, byteCount, tensor);
}

// Convenience overload for typed values allocated on a new CPU target.
template <typename T>
inline HRESULT CreateTensorFromValues(IWinMLRuntime* runtime, WINML_TENSOR_DATA_TYPE dataType,
                                      const std::vector<UINT64>& dims, const std::vector<T>& values,
                                      IWinMLTensor** tensor) noexcept
{
    if (runtime == nullptr)
    {
        return E_POINTER;
    }

    ComPtr<IWinMLExecutionTarget> cpuTarget;
    CHECK_HR(runtime->CreateCpuExecutionTarget(cpuTarget.GetAddressOf()));
    return CreateTensorFromValues(cpuTarget.Get(), dataType, dims, values, tensor);
}

// Uses the optional named-binding interface when a sample must bind by ONNX name
// instead of the default Runtime ordinal binding.
inline HRESULT BindInputByName(IWinMLStage* stage, const wchar_t* name,
                               IWinMLTensor* tensor) noexcept
{
    if (stage == nullptr || name == nullptr || tensor == nullptr)
    {
        return E_POINTER;
    }

    ComPtr<IWinMLOrtNamedBindings> namedBindings;
    CHECK_HR(stage->QueryInterface(IID_PPV_ARGS(namedBindings.GetAddressOf())));
    return namedBindings->BindInputByName(name, tensor);
}

// LoadModelFromFile -> CreatePipelineBuilder -> AddModelStage ->
// RequestOutput(0) -> Build for the common one-stage inference path.
inline HRESULT LoadModelPipeline(IWinMLRuntime* runtime, const std::wstring& path,
                                 IWinMLExecutionTarget* target, IWinMLModel** model,
                                 IWinMLPipeline** pipeline, IWinMLStage** stage = nullptr) noexcept
{
    if (runtime == nullptr || target == nullptr || model == nullptr || pipeline == nullptr)
    {
        return E_POINTER;
    }

    *model = nullptr;
    *pipeline = nullptr;
    if (stage != nullptr)
    {
        *stage = nullptr;
    }

    ComPtr<IWinMLModel> localModel;
    CHECK_HR(runtime->LoadModelFromFile(path.c_str(), nullptr, localModel.GetAddressOf()));

    ComPtr<IWinMLPipelineBuilder> builder;
    CHECK_HR(runtime->CreatePipelineBuilder(builder.GetAddressOf()));

    ComPtr<IWinMLStage> localStage;
    CHECK_HR(
        builder->AddModelStage(localModel.Get(), target, path.c_str(), localStage.GetAddressOf()));
    CHECK_HR(localStage->RequestOutput(0));

    ComPtr<IWinMLPipeline> localPipeline;
    CHECK_HR(builder->Build(localPipeline.GetAddressOf()));

    *model = localModel.Detach();
    *pipeline = localPipeline.Detach();
    if (stage != nullptr)
    {
        *stage = localStage.Detach();
    }

    return S_OK;
}

inline UINT32 Argmax(const float* values, size_t count) noexcept
{
    if (values == nullptr || count == 0)
    {
        return 0;
    }

    UINT32 best = 0;
    float bestValue = values[0];
    for (size_t i = 1; i < count; ++i)
    {
        if (values[i] > bestValue)
        {
            bestValue = values[i];
            best = static_cast<UINT32>(i);
        }
    }

    return best;
}

inline UINT32 Argmax(const std::vector<float>& values) noexcept
{
    return Argmax(values.data(), values.size());
}

inline bool TensorShapeMatches(IWinMLTensor* tensor,
                               std::initializer_list<UINT64> expectedDims) noexcept
{
    if (tensor == nullptr || expectedDims.size() == 0)
    {
        return false;
    }

    WINML_TENSOR_DESC desc{};
    if (FAILED(tensor->GetDesc(&desc)) || desc.dimensionCount != expectedDims.size() ||
        desc.dimensions == nullptr)
    {
        return false;
    }

    size_t index = 0;
    for (UINT64 expected : expectedDims)
    {
        if (desc.dimensions[index++] != expected)
        {
            return false;
        }
    }

    return true;
}

inline HRESULT GetTensorElementCount(IWinMLTensor* tensor, UINT64* elementCount) noexcept
{
    if (tensor == nullptr || elementCount == nullptr)
    {
        return E_POINTER;
    }

    WINML_TENSOR_DESC desc{};
    CHECK_HR(tensor->GetDesc(&desc));
    if (desc.dimensionCount == 0 || desc.dimensions == nullptr)
    {
        return E_FAIL;
    }

    UINT64 count = 1;
    for (UINT32 i = 0; i < desc.dimensionCount; ++i)
    {
        if (desc.dimensions[i] == 0 ||
            count > std::numeric_limits<UINT64>::max() / desc.dimensions[i])
        {
            return E_FAIL;
        }

        count *= desc.dimensions[i];
    }

    *elementCount = count;
    return S_OK;
}

// Enable verbose node-placement logging before the Runtime initializes. Call once
// before creating Runtime objects; if the environment already exists, the logging
// level may not change.
inline void EnableVerboseInferenceLogging() noexcept
{
    const OrtApi* api = OrtGetApiBase()->GetApi(ORT_API_VERSION);
    if (api == nullptr)
    {
        return;
    }

    static OrtEnv* s_env = nullptr;
    if (s_env != nullptr)
    {
        return;
    }

    OrtStatus* status = api->CreateEnv(ORT_LOGGING_LEVEL_VERBOSE, "WinMLTensorsSample", &s_env);
    if (status != nullptr)
    {
        api->ReleaseStatus(status);
        s_env = nullptr;
        return;
    }

    std::wprintf(L"  Verbose logging enabled for inference node placement.\n");
}

// Prepare a requested provider before creating its pinned execution target.
// A CPU or GPU run without --ep uses the Runtime default. NPU requires --ep.
inline HRESULT PrepareInferenceExecutionProviders(const DeviceArgs& device) noexcept
{
    if (device.epName.empty() && device.deviceType != WINML_EXECUTION_TARGET_KIND_NPU)
    {
        return S_OK;
    }

    return ::PrepareAndValidateExecutionProviders(
        device.deviceType, device.epName.empty() ? nullptr : device.epName.c_str());
}

inline HRESULT PrepareAndCreateInferenceTarget(
    IWinMLRuntime* runtime, const DeviceArgs& device, _COM_Outptr_ IWinMLExecutionTarget** target,
    _COM_Outptr_opt_ IWinMLExecutionTarget** tensorTarget = nullptr) noexcept
{
    CHECK_HR(PrepareInferenceExecutionProviders(device));
    return winmlsamples::tensors::CreateExecutionTarget(runtime, device, target, tensorTarget);
}

} // namespace tensors
} // namespace winmlsamples
