// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Model compilation: compile an ONNX model, reload it, and run it.
//
// Compile a model ahead of time for one target, reload the result from files
// or from application memory, and run it once.
//
//   The target supplies IWinMLModelCompiler (-Device gpu needs a non-WebGPU
//   -Ep). The source loads with LoadModelFromFile; then, per -Mode:
//   - file: CompileToFile writes artifact.onnx and weights.bin, which
//     LoadModelFromFile reloads (-OutputDirectory keeps them).
//   - sink, and zerocopy for assets the app already holds: CompileToSink
//     passes the graph and weights to an IWinMLCompileOutputSink the app
//     implements. LoadModelFromBuffer reloads the graph with an
//     IWinMLResourceMapReader that points into the captured weights, so
//     they must outlive the model.
//   Each reloaded model runs once as one stage on float32 inputs shaped from
//   its schema, and a few output values are printed without being checked.
//
// Run it
//   .\run_model_compilation.ps1 [-Device cpu|gpu|npu] [-Ep <name>]
//                                [-Mode file|sink|zerocopy|all]
//                                [-ModelPath <path>] [-OutputDirectory <path>]
//                                [-Diagnostics]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Runtime/tutorials/06-compile-and-deploy.md
//   ../../../../docs/api-reference/IWinMLModelCompiler.md
//   ../../../../docs/api-reference/IWinMLCompileOutputSink.md
//   ../../../../docs/api-reference/IWinMLResourceMapReader.md
//   ../../../../docs/api-reference/CommonPatterns.md (pattern 7)

#include <cstdio>
#include <filesystem>
#include <map>
#include <string>
#include <vector>

#include <Windows.h>
#include <wil/result.h>
#include <wrl/implements.h>

#include <WinMLRuntime.h>

#include "common.h"
#include "execution_target_utils.h"
#include "ep_catalog_utils.h"
#include "model_compilation_utils.h"
#include "sample_args.h"

using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Make;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;

// ---------------------------------------------------------------------------
// In-memory capture of a compile: the caller-implemented sink that receives
// graph artifact bytes and any named resource bytes during CompileToSink.
// The resource key opened by BeginResource is the key LoadModelFromBuffer later
// asks the reader to resolve.
// ---------------------------------------------------------------------------
class CapturedCompile final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IWinMLCompileOutputSink>
{
public:
    struct Resource
    {
        std::vector<BYTE> bytes;
        UINT64 alignment = 0;
    };

    using ResourceMap = std::map<std::string, Resource, std::less<>>;

    IFACEMETHODIMP WriteArtifactBytes(const BYTE* data, UINT64 byteCount) noexcept override
    try
    {
        RETURN_HR_IF(E_POINTER, data == nullptr && byteCount != 0);
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW), byteCount > SIZE_MAX);
        if (byteCount == 0)
        {
            return S_OK;
        }

        m_artifact.insert(m_artifact.end(), data, data + byteCount);
        return S_OK;
    }
    catch (...)
    {
        RETURN_CAUGHT_EXCEPTION();
    }

    IFACEMETHODIMP BeginResource(const CHAR* key,
                                 const WINML_RESOURCE_DESCRIPTOR* descriptor) noexcept override
    try
    {
        RETURN_HR_IF(E_POINTER, key == nullptr || descriptor == nullptr);
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW),
                     descriptor->byteSize > SIZE_MAX);

        auto [iterator, inserted] = m_resources.try_emplace(key);
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS), !inserted);
        auto& resource = iterator->second;
        resource.alignment = descriptor->alignment;
        resource.bytes.reserve(static_cast<size_t>(descriptor->byteSize));
        return S_OK;
    }
    catch (...)
    {
        RETURN_CAUGHT_EXCEPTION();
    }

    IFACEMETHODIMP WriteResourceBytes(const CHAR* key, const BYTE* data,
                                      UINT64 byteCount) noexcept override
    try
    {
        RETURN_HR_IF(E_POINTER, key == nullptr || (data == nullptr && byteCount != 0));
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW), byteCount > SIZE_MAX);
        auto it = m_resources.find(key);
        if (it == m_resources.end())
        {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }

        if (byteCount == 0)
        {
            return S_OK;
        }

        it->second.bytes.insert(it->second.bytes.end(), data, data + byteCount);
        return S_OK;
    }
    catch (...)
    {
        RETURN_CAUGHT_EXCEPTION();
    }

    IFACEMETHODIMP EndResource(const CHAR* key) noexcept override
    {
        RETURN_HR_IF_NULL(E_POINTER, key);
        return m_resources.contains(key) ? S_OK : HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }

    const std::vector<BYTE>& Artifact() const noexcept
    {
        return m_artifact;
    }

    const ResourceMap& Resources() const noexcept
    {
        return m_resources;
    }

private:
    std::vector<BYTE> m_artifact;
    ResourceMap m_resources;
};

// ---------------------------------------------------------------------------
// Read-only reader over captured or app-held resources. GetData returns a
// pointer into the backing store, so the buffers must outlive the loaded model.
// ---------------------------------------------------------------------------
class CapturedWeights final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IWinMLResourceMapReader>
{
public:
    HRESULT Initialize(const CapturedCompile::ResourceMap& source) noexcept
    try
    {
        m_keys.reserve(source.size());
        for (const auto& entry : source)
        {
            m_keys.push_back(entry.first);
            m_index.emplace(m_keys.back(), &entry.second);
        }

        return S_OK;
    }
    catch (...)
    {
        RETURN_CAUGHT_EXCEPTION();
    }

    IFACEMETHODIMP GetCount(UINT32* count) noexcept override
    {
        RETURN_HR_IF_NULL(E_POINTER, count);
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW), m_keys.size() > UINT32_MAX);
        *count = static_cast<UINT32>(m_keys.size());
        return S_OK;
    }

    IFACEMETHODIMP GetKey(UINT32 index, const CHAR** key, UINT32* keyLength) noexcept override
    {
        RETURN_HR_IF(E_POINTER, key == nullptr || keyLength == nullptr);
        if (index >= m_keys.size())
        {
            return E_BOUNDS;
        }

        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW),
                     m_keys[index].size() > UINT32_MAX);
        *key = m_keys[index].c_str();
        *keyLength = static_cast<UINT32>(m_keys[index].size());
        return S_OK;
    }

    IFACEMETHODIMP GetDescriptor(const CHAR* key,
                                 WINML_RESOURCE_DESCRIPTOR* descriptor) noexcept override
    {
        RETURN_HR_IF(E_POINTER, key == nullptr || descriptor == nullptr);
        auto it = m_index.find(key);
        if (it == m_index.end())
        {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }

        descriptor->byteSize = it->second->bytes.size();
        descriptor->alignment = it->second->alignment;
        descriptor->offset = 0;
        return S_OK;
    }

    IFACEMETHODIMP GetData(const CHAR* key, const BYTE** data, UINT64* byteSize) noexcept override
    {
        RETURN_HR_IF(E_POINTER, key == nullptr || data == nullptr || byteSize == nullptr);
        auto it = m_index.find(key);
        if (it == m_index.end())
        {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }

        *data = it->second->bytes.data(); // pointer into the captured buffer - no copy
        *byteSize = it->second->bytes.size();
        return S_OK;
    }

private:
    std::vector<std::string> m_keys;
    std::map<std::string, const CapturedCompile::Resource*, std::less<>> m_index;
};

static HRESULT CompileToCapturedSink(IWinMLRuntime* runtime, IWinMLModelCompiler* compiler,
                                     const wchar_t* modelPath,
                                     ComPtr<CapturedCompile>& captured) noexcept
{
    ComPtr<IWinMLModel> model;
    CHECK_HR(runtime->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));

    captured = Make<CapturedCompile>();
    CHECK_HR_IF(captured == nullptr, E_OUTOFMEMORY);
    return compiler->CompileToSink(model.Get(), captured.Get());
}

static HRESULT LoadCapturedModel(IWinMLRuntime* runtime, CapturedCompile* captured,
                                 ComPtr<CapturedWeights>& weights,
                                 ComPtr<IWinMLModel>& compiled) noexcept
{
    weights = Make<CapturedWeights>();
    CHECK_HR_IF(weights == nullptr, E_OUTOFMEMORY);
    CHECK_HR(weights->Initialize(captured->Resources()));

    // LoadModelFromBuffer accepts the artifact bytes plus a resource reader for
    // any externalized resources named by the artifact.
    return runtime->LoadModelFromBuffer(captured->Artifact().data(),
                                        static_cast<UINT64>(captured->Artifact().size()),
                                        weights.Get(), compiled.GetAddressOf());
}

// Path 1: AOT compile to files, then load the files back and run. The compiler
// writes the artifact + external weights to the caller's paths.
//
// Outputs live in a unique temporary directory. This avoids deleting files
// from the caller's working directory and keeps the externalized weights alive
// until the compiled model finishes running.
static HRESULT RunFilePath(IWinMLRuntime* runtime, IWinMLExecutionTarget* pipelineTarget,
                           IWinMLExecutionTarget* tensorTarget, IWinMLModelCompiler* compiler,
                           const wchar_t* modelPath, const std::wstring& requestedOutputDirectory)
{
    winmlsamples::compilation::TemporaryOutputDirectory outputDirectory;
    CHECK_HR(outputDirectory.Create(requestedOutputDirectory));
    const std::filesystem::path artifactPath = outputDirectory.path / L"artifact.onnx";
    const wchar_t* externalWeightsPath = L"weights.bin";

    wprintf(L"[file] compile -> %s + weights.bin, then load + run\n", artifactPath.c_str());
    if (!requestedOutputDirectory.empty())
    {
        wprintf(L"      retained output directory: %s\n", outputDirectory.path.c_str());
    }

    // CompileToFile writes a deployable artifact path plus optional external
    // resources. The compiled artifact reloads through the normal file API.
    ComPtr<IWinMLModel> model;
    CHECK_HR(runtime->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));

    CHECK_HR(compiler->CompileToFile(model.Get(), artifactPath.c_str(), externalWeightsPath));
    model.Reset();

    ComPtr<IWinMLModel> compiled;
    CHECK_HR(runtime->LoadModelFromFile(artifactPath.c_str(), nullptr, compiled.GetAddressOf()));
    return winmlsamples::compilation::RunCompiledModel(runtime, pipelineTarget, tensorTarget,
                                                       compiled.Get());
}

// Path 2: no-disk. Compile into an in-memory sink, then load the captured bytes
// back through a reader and run - nothing touches disk.
static HRESULT RunSinkPath(IWinMLRuntime* runtime, IWinMLExecutionTarget* pipelineTarget,
                           IWinMLExecutionTarget* tensorTarget, IWinMLModelCompiler* compiler,
                           const wchar_t* modelPath)
{
    wprintf(L"[sink] compile to memory, load from buffer + reader, run (no disk)\n");

    // CompileToSink streams the compiled artifact and resources to the sink
    // callbacks above instead of naming output files.
    ComPtr<CapturedCompile> captured;
    CHECK_HR(CompileToCapturedSink(runtime, compiler, modelPath, captured));
    wprintf(L"      captured graph: %zu bytes, %zu externalized resource(s)\n",
            captured->Artifact().size(), captured->Resources().size());

    ComPtr<CapturedWeights> weights;
    ComPtr<IWinMLModel> compiled;
    CHECK_HR(LoadCapturedModel(runtime, captured.Get(), weights, compiled));
    return winmlsamples::compilation::RunCompiledModel(runtime, pipelineTarget, tensorTarget,
                                                       compiled.Get());
}

// Path 3: zero-copy escape hatch. The app already holds the compiled artifact +
// weights in its own memory (here reused from a sink compile); it serves them
// through a reader and the runtime binds them in place, copying nothing.
static HRESULT RunZeroCopyPath(IWinMLRuntime* runtime, IWinMLExecutionTarget* pipelineTarget,
                               IWinMLExecutionTarget* tensorTarget, IWinMLModelCompiler* compiler,
                               const wchar_t* modelPath)
{
    wprintf(L"[zerocopy] bind app-held weights via a reader, run (no copy)\n");

    ComPtr<CapturedCompile> captured;
    CHECK_HR(CompileToCapturedSink(runtime, compiler, modelPath, captured));

    // The app keeps the artifact + weights in its own asset memory. A reader over
    // that memory is the only thing the runtime needs; no file, no copy.
    ComPtr<CapturedWeights> appHeldWeights;
    ComPtr<IWinMLModel> compiled;
    CHECK_HR(LoadCapturedModel(runtime, captured.Get(), appHeldWeights, compiled));
    return winmlsamples::compilation::RunCompiledModel(runtime, pipelineTarget, tensorTarget,
                                                       compiled.Get());
}

// Checks whether the selected target can compile the model.
//
// The compiler is a capability of the execution target: a CPU target, an NPU
// target, or a GPU target pinned to an execution provider compiles with ONNX
// Runtime. An unpinned GPU target, including a Runtime-owned provider reached
// through the default GPU target, has no provider to compile with, so the
// sample reports that combination before opening the model.
static HRESULT ValidateCompilerBackend(const DeviceArgs& device)
{
    const bool pinned = !device.epName.empty() &&
                        !IsWebGpuEpName(ResolveExecutionProviderName(device.epName.c_str()));
    if (device.deviceType != WINML_EXECUTION_TARGET_KIND_GPU || pinned)
    {
        return S_OK;
    }

    wprintf(L"ERROR: --device gpu needs an app-registered execution provider to compile with.\n");
    wprintf(L"       Add --ep <name> to compile with ONNX Runtime on that provider.\n");
    return E_INVALIDARG;
}

int wmain(int argc, wchar_t* argv[])
{
    wprintf(L"=== Windows ML Runtime: Model compilation ===\n\n");

    SampleArgs args(argc, argv);
    const DeviceArgs deviceArgs = args.Device();
    if (FAILED(ValidateCompilerBackend(deviceArgs)))
    {
        return 1;
    }

    const std::string epFilter = args.Ep();
    // Default to the SqueezeNet model prepared by check_artifacts.ps1; pass another
    // ONNX model to compile it instead.
    const std::wstring modelPathStr =
        args.Text(L"--model", 0, L"Source ONNX model path", FindModelPath(L"SqueezeNet.onnx"));
    const std::wstring mode = args.Text(L"--mode", -1, L"Mode (file|sink|zerocopy|all)", L"all");
    const std::wstring outputDirectory = args.Text(
        L"--output-dir", -1, L"Optional empty directory for retained file-mode outputs", L"");
    const bool runAll = (mode == L"all");
    const bool runFile = (mode == L"file");
    const bool runSink = (mode == L"sink");
    const bool runZeroCopy = (mode == L"zerocopy");
    if (!(runAll || runFile || runSink || runZeroCopy))
    {
        wprintf(L"ERROR: unrecognized --mode value '%s'. Expected file|sink|zerocopy|all.\n",
                mode.c_str());
        return 1;
    }

    if (!outputDirectory.empty() && !(runAll || runFile))
    {
        wprintf(L"ERROR: --output-dir applies only to file or all mode.\n");
        return 1;
    }

    if (modelPathStr.empty() || !FileExists(modelPathStr))
    {
        wprintf(L"ERROR: source model not found. Pass a model path, or build the\n");
        wprintf(L"artifact check first so its model is present under models\\squeezenet.\n");
        return 1;
    }

    const std::wstring extension = std::filesystem::path(modelPathStr).extension().wstring();
    if (_wcsicmp(extension.c_str(), L".onnx") != 0)
    {
        wprintf(L"ERROR: compilation requires an ONNX source model.\n");
        return 1;
    }

    const wchar_t* modelPath = modelPathStr.c_str();

    if (!epFilter.empty() || deviceArgs.deviceType == WINML_EXECUTION_TARGET_KIND_NPU)
    {
        if (FAILED(PrepareAndValidateExecutionProviders(
                deviceArgs.deviceType, epFilter.empty() ? nullptr : epFilter.c_str())))
        {
            return 1;
        }
    }

    wprintf(L"Model:   %s\nDevice:  %s\nMode:    %s\n\n", modelPath,
            DeviceTypeName(deviceArgs.deviceType), mode.c_str());

    // IWinMLRuntime is the process-local entry point for loading models,
    // creating execution targets, and creating pipeline builders.
    ComPtr<IWinMLRuntime> runtime;
    if (FAILED(WinMLCreateRuntime(__uuidof(IWinMLRuntime),
                                  reinterpret_cast<void**>(runtime.GetAddressOf()))))
    {
        wprintf(L"ERROR: failed to create the Runtime.\n");
        return 1;
    }

    DeviceArgs compilerDeviceArgs = deviceArgs;
    if (compilerDeviceArgs.deviceType == WINML_EXECUTION_TARGET_KIND_CPU &&
        compilerDeviceArgs.epName.empty())
    {
        // The ORT compatibility target selects the CPU execution provider for the
        // compile and reload path.
        compilerDeviceArgs.epName = "CPUExecutionProvider";
    }

    ComPtr<IWinMLExecutionTarget> target;
    ComPtr<IWinMLExecutionTarget> tensorTarget;
    if (FAILED(CreateExecutionTarget(runtime.Get(), compilerDeviceArgs, target.GetAddressOf(),
                                     tensorTarget.GetAddressOf())))
    {
        wprintf(L"ERROR: failed to create the execution target.\n");
        return 1;
    }

    // Model compilation is a target capability: query it from the selected
    // execution target and fail early if that target cannot compile.
    ComPtr<IWinMLModelCompiler> compiler;
    if (FAILED(target->QueryInterface(IID_PPV_ARGS(compiler.GetAddressOf()))))
    {
        wprintf(L"ERROR: selected execution target does not expose the model compiler.\n");
        return 1;
    }

    HRESULT hr = S_OK;
    if (runAll || runFile)
    {
        hr = RunFilePath(runtime.Get(), target.Get(), tensorTarget.Get(), compiler.Get(), modelPath,
                         outputDirectory);
    }

    if (SUCCEEDED(hr) && (runAll || runSink))
    {
        hr =
            RunSinkPath(runtime.Get(), target.Get(), tensorTarget.Get(), compiler.Get(), modelPath);
    }

    if (SUCCEEDED(hr) && (runAll || runZeroCopy))
    {
        hr = RunZeroCopyPath(runtime.Get(), target.Get(), tensorTarget.Get(), compiler.Get(),
                             modelPath);
    }

    if (FAILED(hr))
    {
        wprintf(L"\nFAILED: 0x%08X\n", hr);
        return 1;
    }

    wprintf(L"\nDone.\n");
    return 0;
}
