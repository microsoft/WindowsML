// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared utilities for Windows ML Runtime samples.
//
// Wraps the Runtime API surface common to most samples: execution-target
// command-line parsing, raw tensor allocation through IWinMLRawTensorFactory,
// HRESULT diagnostics, and COM pointer ownership.

#pragma once

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <string>
#include <vector>

#include <windows.h>
#include <wrl/client.h>

#include <WinMLRuntime.h>
#include <WinMLRuntimeOrt.h>
#include <WinMLTensorFactory.h>
#include "tensor_lock_utils.h"

// HRESULT checking macros.

// Samples return HRESULT from helpers and process exit codes from wmain. These
// macros keep the examples readable while still printing the failing API,
// HRESULT, file, and line.
#define CHECK_HR(expr) \
    do \
    { \
        HRESULT _hr = (expr); \
        if (FAILED(_hr)) \
        { \
            wprintf(L"FAILED: %s\n  HRESULT: 0x%08X\n  File: %hs\n  Line: %d\n", L#expr, _hr, \
                    __FILE__, __LINE__); \
            return _hr; \
        } \
    } while (false)

#define CHECK_HR_MSG(expr, msg) \
    do \
    { \
        HRESULT _hr = (expr); \
        if (FAILED(_hr)) \
        { \
            wprintf(L"FAILED: %s\n  %s\n  HRESULT: 0x%08X\n", L#expr, msg, _hr); \
            return _hr; \
        } \
    } while (false)

// Returns hr with diagnostics when a sample-level check fails.
#define CHECK_HR_IF(failCondition, hr) \
    do \
    { \
        if (failCondition) \
        { \
            wprintf(L"FAILED: %s\n  HRESULT: 0x%08X\n  File: %hs\n  Line: %d\n", L#failCondition, \
                    static_cast<HRESULT>(hr), __FILE__, __LINE__); \
            return (hr); \
        } \
    } while (false)

// COM smart pointer.

// Use the Windows SDK's WRL::ComPtr for COM ownership. It is a header-only part
// of the Windows SDK (not ATL, no extra package), and the shared tensor-lock
// helpers already depend on it -- so the samples standardize on one COM smart
// pointer rather than carrying a bespoke implementation. The unqualified alias
// keeps the sample call sites concise.
using Microsoft::WRL::ComPtr;

// Device selection helpers.

// Forward declaration; defined in the file/string helpers section below. Lets the
// --ep parser convert a wide catalog name to the UTF-8 the Runtime expects.
inline std::string WideToUtf8(const std::wstring& text);

struct DeviceArgs
{
    // deviceType selects which execution target the sample creates. CPU uses
    // IWinMLRuntime::CreateCpuExecutionTarget. GPU and NPU enumerate DXCore
    // adapters and call CreateExecutionTargetFromAdapter.
    // epName is a UTF-8 catalog name from --ep. It selects one installed
    // provider for the requested device class. The sample registers it and
    // creates an IWinMLOrtCompatibility target that pins that provider. Empty
    // uses the Runtime default.
    WINML_EXECUTION_TARGET_KIND deviceType = WINML_EXECUTION_TARGET_KIND_CPU;

    // The samples use --performance/--efficiency only when choosing among multiple
    // DXCore GPU adapters.
    enum class ExecutionPolicy
    {
        Default,
        PreferPerformance,
        PreferEfficiency,
    };

    ExecutionPolicy executionPolicy = ExecutionPolicy::Default;
    std::string epName;
};

inline const wchar_t* DeviceTypeName(WINML_EXECUTION_TARGET_KIND type)
{
    switch (type)
    {
    case WINML_EXECUTION_TARGET_KIND_CPU:
        return L"CPU";
    case WINML_EXECUTION_TARGET_KIND_GPU:
        return L"GPU";
    case WINML_EXECUTION_TARGET_KIND_NPU:
        return L"NPU";
    default:
        return L"Unknown";
    }
}

// Maps a --device token (case-insensitive) to a WINML_EXECUTION_TARGET_KIND.
// Returns false for an unrecognized token so each sample can choose how to
// report it. Keeping the mapping in one place ensures every sample accepts the
// same device names.
inline bool DeviceTypeFromString(const wchar_t* token, WINML_EXECUTION_TARGET_KIND& deviceType)
{
    if (_wcsicmp(token, L"cpu") == 0)
    {
        deviceType = WINML_EXECUTION_TARGET_KIND_CPU;
        return true;
    }

    if (_wcsicmp(token, L"gpu") == 0)
    {
        deviceType = WINML_EXECUTION_TARGET_KIND_GPU;
        return true;
    }

    if (_wcsicmp(token, L"npu") == 0)
    {
        deviceType = WINML_EXECUTION_TARGET_KIND_NPU;
        return true;
    }

    if (_wcsicmp(token, L"default") == 0)
    {
        deviceType = WINML_EXECUTION_TARGET_KIND_CPU;
        return true;
    }

    return false;
}

// Returns true if the command line contains the given flag (case-insensitive).
// Used for simple presence-only switches such as --verbose that every sample
// accepts uniformly.
inline bool HasArg(int argc, wchar_t* argv[], const wchar_t* flag)
{
    for (int i = 1; i < argc; ++i)
    {
        if (_wcsicmp(argv[i], flag) == 0)
        {
            return true;
        }
    }

    return false;
}

inline DeviceArgs ParseDeviceArgs(int argc, wchar_t* argv[])
{
    DeviceArgs args;
    for (int i = 1; i < argc; ++i)
    {
        if (wcscmp(argv[i], L"--device") == 0 && i + 1 < argc)
        {
            ++i;
            if (!DeviceTypeFromString(argv[i], args.deviceType))
            {
                wprintf(L"WARNING: unknown device '%s'; using CPU.\n", argv[i]);
                args.deviceType = WINML_EXECUTION_TARGET_KIND_CPU;
            }
        }
        else if (wcscmp(argv[i], L"--ep") == 0 && i + 1 < argc)
        {
            ++i;
            args.epName = WideToUtf8(argv[i]);
        }
        else if (wcscmp(argv[i], L"--performance") == 0)
        {
            args.executionPolicy = DeviceArgs::ExecutionPolicy::PreferPerformance;
        }
        else if (wcscmp(argv[i], L"--efficiency") == 0)
        {
            args.executionPolicy = DeviceArgs::ExecutionPolicy::PreferEfficiency;
        }
    }

    return args;
}

// Creates an IWinMLTensor from a raw data buffer on the given execution target.
// Tensor creation is a QI capability on the target (IWinMLRawTensorFactory),
// not a method of IWinMLRuntime.
inline HRESULT CreateTensorOnTarget(IWinMLExecutionTarget* target, const WINML_TENSOR_DESC* desc,
                                    const void* data, UINT64 byteCount, IWinMLTensor** tensor)
{
    if (target == nullptr || desc == nullptr || tensor == nullptr)
    {
        return E_POINTER;
    }

    *tensor = nullptr;

    ComPtr<IWinMLRawTensorFactory> factory;
    CHECK_HR(target->QueryInterface(IID_PPV_ARGS(factory.GetAddressOf())));
    return factory->CreateTensor(desc, data, byteCount, tensor);
}

// File and string helpers.

inline bool FileExists(const std::wstring& path)
{
    return !path.empty() && GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

// Returns the path of a file in the same directory as `path`.
inline std::wstring SiblingPath(const std::wstring& path, const wchar_t* filename)
{
    auto separator = path.find_last_of(L"\\/");
    if (separator == std::wstring::npos)
    {
        return filename;
    }

    return path.substr(0, separator + 1) + filename;
}

inline std::string WideToUtf8(const std::wstring& text)
{
    if (text.empty())
    {
        return {};
    }

    int length = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                                     nullptr, 0, nullptr, nullptr);
    if (length <= 0)
    {
        return {};
    }

    std::string utf8(static_cast<size_t>(length), '\0');
    if (WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), utf8.data(),
                            length, nullptr, nullptr) == 0)
    {
        return {};
    }

    return utf8;
}

inline const char* ResolveExecutionProviderName(const char* name)
{
    if (name == nullptr)
    {
        return nullptr;
    }

    static const struct
    {
        const char* alias;
        const char* fullName;
    } aliases[] = {
        {"webgpu", "WebGpuExecutionProvider"},
        {"nvtensorrtrtx", "NvTensorRTRTXExecutionProvider"},
        {"trtrtx", "NvTensorRTRTXExecutionProvider"},
        {"openvino", "OpenVINOExecutionProvider"},
        {"qnn", "QNNExecutionProvider"},
        {"vitisai", "VitisAIExecutionProvider"},
        {"migraphx", "MIGraphXExecutionProvider"},
    };

    for (const auto& entry : aliases)
    {
        if (_stricmp(name, entry.alias) == 0)
        {
            return entry.fullName;
        }
    }

    return name;
}

inline std::wstring Utf8ToWide(const std::string& utf8)
{
    if (utf8.empty())
    {
        return {};
    }

    int length =
        MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), nullptr, 0);
    if (length <= 0)
    {
        return {};
    }

    std::wstring wide(static_cast<size_t>(length), L'\0');
    if (MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), wide.data(),
                            length) == 0)
    {
        return {};
    }

    return wide;
}

// Model I/O metadata printing.

// Metadata printing is useful for first-time API users because bindings happen
// by ordinal stage input/output index, while model authoring usually names
// tensors. The model schema core (IWinMLModelSchema) is ordinal-only - input
// and output names are only available for ONNX-backed models via the optional
// IWinMLOrtModelSchema capability (WinMLRuntimeOrt.h). Tensor shapes belong to
// the materialized stage, not the model.
inline void PrintModelInfo(IWinMLModel* model)
{
    ComPtr<IWinMLModelSchema> schema;
    if (FAILED(model->QueryInterface(IID_PPV_ARGS(schema.GetAddressOf()))))
    {
        return;
    }

    UINT32 inCount = 0, outCount = 0;
    schema->GetInputCount(&inCount);
    schema->GetOutputCount(&outCount);
    wprintf(L"       %u input(s), %u output(s)\n", inCount, outCount);

    ComPtr<IWinMLOrtModelSchema> ortSchema;
    model->QueryInterface(IID_PPV_ARGS(ortSchema.GetAddressOf()));

    for (UINT32 i = 0; i < inCount; ++i)
    {
        LPCWSTR name = nullptr;
        if (ortSchema)
        {
            ortSchema->GetInputName(i, &name);
        }

        wprintf(L"       input[%u]: '%s'\n", i, name ? name : L"?");
    }

    for (UINT32 i = 0; i < outCount; ++i)
    {
        LPCWSTR name = nullptr;
        if (ortSchema)
        {
            ortSchema->GetOutputName(i, &name);
        }

        wprintf(L"       output[%u]: '%s'\n", i, name ? name : L"?");
    }
}

// Console output helpers.

inline void PrintSeparator(const wchar_t* title = nullptr)
{
    wprintf(L"\n");
    if (title)
    {
        wprintf(L"--- %s ---\n", title);
    }
    else
    {
        wprintf(L"---\n");
    }
}

// Top-K extraction from logits/probabilities.

struct TopKResult
{
    int index;
    float value;
};

inline std::vector<TopKResult> GetTopK(const float* data, size_t count, int k)
{
    std::vector<TopKResult> results(count);
    for (size_t i = 0; i < count; ++i)
    {
        results[i] = {static_cast<int>(i), data[i]};
    }

    std::partial_sort(results.begin(), results.begin() + std::min(static_cast<size_t>(k), count),
                      results.end(), [](const TopKResult& a, const TopKResult& b) {
                          return a.value > b.value;
                      });

    results.resize(std::min(static_cast<size_t>(k), count));
    return results;
}

// Softmax (in-place).

inline void Softmax(float* data, size_t count)
{
    if (!data || count == 0)
    {
        return;
    }

    float maxVal = *std::max_element(data, data + count);
    float sum = 0.0f;
    for (size_t i = 0; i < count; ++i)
    {
        data[i] = std::exp(data[i] - maxVal);
        sum += data[i];
    }

    for (size_t i = 0; i < count; ++i)
    {
        data[i] /= sum;
    }
}

// File path helpers.

inline std::wstring GetExecutableDirectory()
{
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring dir(path);
    auto pos = dir.find_last_of(L"\\/");
    return (pos != std::wstring::npos) ? dir.substr(0, pos) : dir;
}

inline std::wstring FindModelPath(const wchar_t* filename, const wchar_t* envVar = nullptr)
{
    // 1. Check environment variable
    if (envVar)
    {
        wchar_t envPath[MAX_PATH] = {};
        if (GetEnvironmentVariableW(envVar, envPath, MAX_PATH) > 0)
        {
            std::wstring path(envPath);
            const DWORD attributes = GetFileAttributesW(path.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES &&
                (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                return path;
            }

            // Try as directory + filename
            path = std::wstring(envPath) + L"\\" + filename;
            if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
            {
                return path;
            }
        }
    }

    // 2. Check next to executable
    std::wstring exeDir = GetExecutableDirectory();
    std::wstring path = exeDir + L"\\" + filename;
    if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        return path;
    }

    // 3. Walk up from exe directory looking for models/ folder
    //    Models are stored as models/<filename> or models/<subfolder>/<filename>
    std::wstring searchDir = exeDir;
    for (int i = 0; i < 8; ++i)
    {
        // Direct: models/<filename>
        path = searchDir + L"\\models\\" + filename;
        if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            return path;
        }

        // Nested: search models/*/<filename> (one level of subdirectories)
        std::wstring modelsRoot = searchDir + L"\\models";
        WIN32_FIND_DATAW findData = {};
        HANDLE hFind = FindFirstFileW((modelsRoot + L"\\*").c_str(), &findData);
        if (hFind != INVALID_HANDLE_VALUE)
        {
            do
            {
                if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
                    wcscmp(findData.cFileName, L".") != 0 && wcscmp(findData.cFileName, L"..") != 0)
                {
                    path = modelsRoot + L"\\" + findData.cFileName + L"\\" + filename;
                    if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
                    {
                        FindClose(hFind);
                        return path;
                    }

                    // Also check one more level deep (e.g., models/whisper-medium-q4f16/onnx/)
                    WIN32_FIND_DATAW findData2 = {};
                    std::wstring subDir = modelsRoot + L"\\" + findData.cFileName;
                    HANDLE hFind2 = FindFirstFileW((subDir + L"\\*").c_str(), &findData2);
                    if (hFind2 != INVALID_HANDLE_VALUE)
                    {
                        do
                        {
                            if ((findData2.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
                                wcscmp(findData2.cFileName, L".") != 0 &&
                                wcscmp(findData2.cFileName, L"..") != 0)
                            {
                                path = subDir + L"\\" + findData2.cFileName + L"\\" + filename;
                                if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
                                {
                                    FindClose(hFind2);
                                    FindClose(hFind);
                                    return path;
                                }
                            }
                        } while (FindNextFileW(hFind2, &findData2));
                        FindClose(hFind2);
                    }
                }
            } while (FindNextFileW(hFind, &findData));
            FindClose(hFind);
        }

        auto sep = searchDir.find_last_of(L"\\/");
        if (sep == std::wstring::npos)
        {
            break;
        }

        searchDir = searchDir.substr(0, sep);
    }

    // 4. Check current working directory
    if (GetFileAttributesW(filename) != INVALID_FILE_ATTRIBUTES)
    {
        return filename;
    }

    return {};
}
