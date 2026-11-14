// Copyright (C) Microsoft Corporation. All rights reserved.
//
// ONNX Runtime application code with the Windows ML CG Execution Provider as
// the bridge to the Windows ML Runtime and DirectX Compute Graph Compiler.

#include <algorithm>
#include <array>
#include <chrono>
#include <cerrno>
#include <cctype>
#include <conio.h>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iterator>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include <Windows.h>
#include <bcrypt.h>
#include <wincodec.h>

#include <wil/result.h>
#include <wil/resource.h>

#include <onnxruntime_cxx_api.h>

#include "common.h"
#include "image_utils.h"
#include "tensorization_image.h"

namespace
{
constexpr char kProviderName[] = "WinMLCGExecutionProvider";
constexpr uint32_t kWarpVendorId = 0x1414;
constexpr uint32_t kImageWidth = 224;
constexpr uint32_t kImageHeight = 224;

struct Options
{
    bool recompile = false;
    size_t adapterIndex = 0;
    bool adapterIndexSpecified = false;
    std::string adapterName;
};

struct ProviderLog
{
    mutable std::mutex mutex;
    bool warpFallback = false;
    std::string adapterName;

    void Reset()
    {
        std::lock_guard lock(mutex);
        warpFallback = false;
    }

    bool HasWarpFallback() const
    {
        std::lock_guard lock(mutex);
        return warpFallback;
    }

    std::string AdapterName() const
    {
        std::lock_guard lock(mutex);
        return adapterName;
    }
};

struct CachePaths
{
    std::filesystem::path model;
    std::filesystem::path manifest;
    std::string identity;
};

void ORT_API_CALL LogOrtMessage(void* parameter, OrtLoggingLevel severity, const char*, const char*,
                                const char*, const char* message)
{
    auto& state = *static_cast<ProviderLog*>(parameter);
    const std::string text = message ? message : "";

    std::lock_guard lock(state.mutex);
    if (text.find("WARP") != std::string::npos || text.find("force_warp") != std::string::npos)
    {
        state.warpFallback = true;
    }

    constexpr std::string_view adapterPrefix = "WinMLCG EP: created D3D12 device on adapter: ";
    const size_t adapterPosition = text.find(adapterPrefix);
    if (adapterPosition != std::string::npos)
    {
        state.adapterName = text.substr(adapterPosition + adapterPrefix.size());
    }

    if (severity >= ORT_LOGGING_LEVEL_ERROR)
    {
        std::fprintf(stderr, "ONNX Runtime: %s\n", text.c_str());
    }
}

std::string WideToUtf8(std::wstring_view value)
{
    if (value.empty())
    {
        return {};
    }
    const int byteCount =
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                            static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (byteCount <= 0)
    {
        throw std::runtime_error("Adapter name is not valid Unicode.");
    }
    std::string result(static_cast<size_t>(byteCount), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                            static_cast<int>(value.size()), result.data(), byteCount, nullptr,
                            nullptr) != byteCount)
    {
        throw std::runtime_error("Adapter name conversion failed.");
    }
    return result;
}

Options ParseOptions(int argc, wchar_t* argv[])
{
    Options options;
    for (int index = 1; index < argc; ++index)
    {
        if (_wcsicmp(argv[index], L"--recompile") == 0)
        {
            options.recompile = true;
        }
        else if (_wcsicmp(argv[index], L"--adapter-index") == 0)
        {
            if (++index >= argc)
            {
                throw std::runtime_error("--adapter-index requires a value.");
            }

            wchar_t* end = nullptr;
            errno = 0;
            const unsigned long value = std::wcstoul(argv[index], &end, 10);
            if (errno == ERANGE || !end || *end != L'\0')
            {
                throw std::runtime_error("--adapter-index must be a non-negative integer.");
            }
            options.adapterIndex = static_cast<size_t>(value);
            options.adapterIndexSpecified = true;
        }
        else if (_wcsicmp(argv[index], L"--adapter-name") == 0)
        {
            if (++index >= argc)
            {
                throw std::runtime_error("--adapter-name requires a value.");
            }

            const std::wstring wideName = argv[index];
            if (wideName.empty())
            {
                throw std::runtime_error("--adapter-name must not be empty.");
            }
            options.adapterName = WideToUtf8(wideName);
        }
        else if (_wcsicmp(argv[index], L"--help") == 0 || _wcsicmp(argv[index], L"-h") == 0)
        {
            std::wprintf(
                L"Usage: onnx-runtime-winmlcgep.exe [--adapter-index N | --adapter-name TEXT] [--recompile]\n"
                L"  --adapter-index N  Select a physical GPU listed by the sample.\n"
                L"  --adapter-name TEXT Select one physical GPU by description substring.\n"
                L"  --recompile        Replace the cache for the selected model and GPU.\n");
            std::exit(0);
        }
        else
        {
            throw std::runtime_error("Unknown command-line argument.");
        }
    }
    if (options.adapterIndexSpecified && !options.adapterName.empty())
    {
        throw std::runtime_error("--adapter-index and --adapter-name are mutually exclusive.");
    }
    return options;
}

std::string ReadTextFile(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary);
    if (!stream)
    {
        return {};
    }
    return {std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
}

void WriteTextFile(const std::filesystem::path& path, const std::string& text)
{
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    THROW_HR_IF_MSG(E_FAIL, !stream, "Unable to write the cache manifest.");
    stream << text;
    THROW_HR_IF_MSG(E_FAIL, !stream, "Unable to finish writing the cache manifest.");
}

std::filesystem::path TemporaryCachePath(const std::filesystem::path& finalPath,
                                         const wchar_t* suffix)
{
    std::wostringstream name;
    name << finalPath.stem().wstring() << L"." << GetCurrentProcessId() << L"." << GetTickCount64()
         << suffix << finalPath.extension().wstring();
    return finalPath.parent_path() / name.str();
}

void PublishFile(const std::filesystem::path& temporaryPath, const std::filesystem::path& finalPath)
{
    THROW_IF_WIN32_BOOL_FALSE_MSG(MoveFileExW(temporaryPath.c_str(), finalPath.c_str(),
                                              MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH),
                                  "Unable to publish the cache file.");
}

void RemoveCacheFiles(const CachePaths& cache)
{
    std::error_code error;
    std::filesystem::remove(cache.model, error);
    error.clear();
    std::filesystem::remove(cache.manifest, error);

    const std::wstring prefix = std::filesystem::path(cache.identity).filename().wstring() + L".";
    for (const auto& entry : std::filesystem::directory_iterator(cache.model.parent_path(), error))
    {
        if (!entry.is_regular_file())
        {
            continue;
        }

        const std::wstring name = entry.path().filename().wstring();
        if (name.starts_with(prefix) &&
            (name.ends_with(L".epctx.onnx") || name.ends_with(L".tmp.onnx") ||
             name.ends_with(L".manifest")))
        {
            std::filesystem::remove(entry.path(), error);
            error.clear();
        }
    }
}

std::string Sha256File(const std::filesystem::path& path)
{
    wil::unique_bcrypt_algorithm algorithm;
    THROW_IF_NTSTATUS_FAILED_MSG(
        BCryptOpenAlgorithmProvider(algorithm.put(), BCRYPT_SHA256_ALGORITHM, nullptr, 0),
        "Unable to initialize SHA-256.");
    wil::unique_bcrypt_hash hash;
    THROW_IF_NTSTATUS_FAILED_MSG(
        BCryptCreateHash(algorithm.get(), hash.put(), nullptr, 0, nullptr, 0, 0),
        "Unable to create SHA-256 state.");

    std::ifstream stream(path, std::ios::binary);
    if (!stream)
    {
        throw std::runtime_error("Unable to open the source model.");
    }
    stream.exceptions(std::ios::badbit);

    std::array<unsigned char, 64 * 1024> buffer{};
    while (stream)
    {
        stream.read(reinterpret_cast<char*>(buffer.data()),
                    static_cast<std::streamsize>(buffer.size()));
        const std::streamsize bytesRead = stream.gcount();
        if (bytesRead > 0)
        {
            THROW_IF_NTSTATUS_FAILED_MSG(
                BCryptHashData(hash.get(), buffer.data(), static_cast<ULONG>(bytesRead), 0),
                "Unable to hash the source model.");
        }
    }

    std::array<unsigned char, 32> digest{};
    THROW_IF_NTSTATUS_FAILED_MSG(
        BCryptFinishHash(hash.get(), digest.data(), static_cast<ULONG>(digest.size()), 0),
        "Unable to finish the source-model hash.");

    std::ostringstream result;
    result << std::hex << std::setfill('0');
    for (unsigned char byte : digest)
    {
        result << std::setw(2) << static_cast<unsigned>(byte);
    }
    return result.str();
}

std::string PlatformName()
{
#if defined(_M_ARM64)
    return "arm64";
#elif defined(_M_X64)
    return "x64";
#else
    return "unknown";
#endif
}

std::string Sanitize(std::string value)
{
    std::replace_if(
        value.begin(), value.end(),
        [](unsigned char character) {
            return !std::isalnum(character);
        },
        '-');
    return value;
}

std::string RuntimePackageVersion()
{
    const std::filesystem::path versionPath =
        std::filesystem::path(GetExecutableDirectory()) / L"runtime-package-version.txt";
    std::string version = ReadTextFile(versionPath);
    if (version.size() >= 3 && static_cast<unsigned char>(version[0]) == 0xEF &&
        static_cast<unsigned char>(version[1]) == 0xBB &&
        static_cast<unsigned char>(version[2]) == 0xBF)
    {
        version.erase(0, 3);
    }
    while (!version.empty() && (version.back() == '\r' || version.back() == '\n'))
    {
        version.pop_back();
    }
    if (version.empty())
    {
        throw std::runtime_error("Runtime package version metadata is missing.");
    }
    return version;
}

std::string DeviceLuid(const Ort::ConstEpDevice& device);

std::vector<Ort::ConstEpDevice> FindHardwareWinMLCGEpDevices(
    const std::vector<Ort::ConstEpDevice>& devices)
{
    std::vector<Ort::ConstEpDevice> matches;
    for (const Ort::ConstEpDevice& device : devices)
    {
        const Ort::ConstHardwareDevice hardware = device.Device();
        if (std::string(device.EpName()) == kProviderName &&
            hardware.Type() == OrtHardwareDeviceType_GPU && hardware.VendorId() != kWarpVendorId)
        {
            matches.push_back(device);
        }
    }
    return matches;
}

Ort::ConstEpDevice RegisterAndSelectDevice(Ort::Env& environment, const Options& options)
{
    std::vector<Ort::ConstEpDevice> devices = environment.GetEpDevices();
    std::vector<Ort::ConstEpDevice> candidates = FindHardwareWinMLCGEpDevices(devices);
    if (candidates.empty())
    {
        const std::filesystem::path providerPath =
            std::filesystem::path(GetExecutableDirectory()) / L"WinMLCGEp.dll";
        if (!std::filesystem::is_regular_file(providerPath))
        {
            throw std::runtime_error("WinMLCGEp.dll is missing. Restore and build with the "
                                     "Microsoft.Windows.AI.MachineLearning NuGet package.");
        }

        try
        {
            environment.RegisterExecutionProviderLibrary(kProviderName, providerPath.c_str());
        }
        catch (const Ort::Exception&)
        {
            // Runtime initialization may have registered the same provider.
            // Re-enumeration distinguishes that expected case from a real load failure.
            devices = environment.GetEpDevices();
            candidates = FindHardwareWinMLCGEpDevices(devices);
            if (candidates.empty())
            {
                throw;
            }
        }

        if (candidates.empty())
        {
            devices = environment.GetEpDevices();
            candidates = FindHardwareWinMLCGEpDevices(devices);
        }
    }

    if (candidates.empty())
    {
        throw std::runtime_error(
            "The Windows ML CG Execution Provider did not expose a physical GPU. "
            "Verify the package and GPU driver.");
    }

    std::printf("Physical GPU candidates:\n");
    for (size_t index = 0; index < candidates.size(); ++index)
    {
        const Ort::ConstHardwareDevice hardware = candidates[index].Device();
        const char* description = hardware.Metadata().GetValue("Description");
        std::printf("  [%zu] %s (vendor=0x%04X device=0x%04X LUID=%s)\n", index,
                    description ? description : "D3D12 GPU", hardware.VendorId(),
                    hardware.DeviceId(), DeviceLuid(candidates[index]).c_str());
    }
    size_t adapterIndex = options.adapterIndex;
    if (!options.adapterName.empty())
    {
        std::vector<size_t> matches;
        for (size_t index = 0; index < candidates.size(); ++index)
        {
            const char* description = candidates[index].Device().Metadata().GetValue("Description");
            const std::string_view candidate = description ? description : "";
            if (std::search(candidate.begin(), candidate.end(), options.adapterName.begin(),
                            options.adapterName.end(),
                            [](unsigned char first, unsigned char second) {
                                return std::tolower(first) == std::tolower(second);
                            }) != candidate.end())
            {
                matches.push_back(index);
            }
        }
        if (matches.empty())
        {
            throw std::runtime_error("--adapter-name did not match a listed physical GPU.");
        }
        if (matches.size() != 1)
        {
            throw std::runtime_error("--adapter-name matched more than one physical GPU.");
        }
        adapterIndex = matches[0];
    }
    if (adapterIndex >= candidates.size())
    {
        throw std::runtime_error("--adapter-index is outside the listed physical GPU range.");
    }

    std::printf("Selected adapter index: %zu\n", adapterIndex);
    return candidates[adapterIndex];
}

void ConfigureSession(Ort::Env& environment, const Ort::ConstEpDevice& device,
                      Ort::SessionOptions& options)
{
    options.SetGraphOptimizationLevel(ORT_DISABLE_ALL);
    options.AddConfigEntry("ep.winmlcgexecutionprovider.agility_sdk_strict", "1");
    options.AppendExecutionProvider_V2(environment, {device}, {});
}

std::string DeviceLuid(const Ort::ConstEpDevice& device)
{
    const char* value = device.Device().Metadata().GetValue("LUID");
    return value ? value : "unknown";
}

std::string CacheManifest(const std::string& modelHash, const std::string& packageVersion,
                          const Ort::ConstEpDevice& device)
{
    std::ostringstream manifest;
    manifest << "schema=1\n"
             << "model_sha256=" << modelHash << "\n"
             << "runtime_package=" << packageVersion << "\n"
             << "platform=" << PlatformName() << "\n"
             << "provider=" << kProviderName << "\n"
             << "vendor_id=" << device.Device().VendorId() << "\n"
             << "device_id=" << device.Device().DeviceId() << "\n"
             << "graph_optimization=disabled\n"
             << "embed_mode=1\n";
    return manifest.str();
}

CachePaths ResolveCache(const std::string& modelHash, const std::string& packageVersion,
                        const Ort::ConstEpDevice& device)
{
    std::ostringstream identity;
    identity << "squeezenet-" << modelHash.substr(0, 16) << "-" << Sanitize(packageVersion) << "-"
             << PlatformName() << "-v" << std::hex << std::setw(4) << std::setfill('0')
             << device.Device().VendorId() << "-d" << std::setw(4) << device.Device().DeviceId();

    const std::filesystem::path cacheDirectory =
        std::filesystem::path(GetExecutableDirectory()) / L"cache";
    const std::filesystem::path base = cacheDirectory / identity.str();
    return {
        base.wstring() + L".epctx.onnx",
        base.wstring() + L".manifest",
        identity.str(),
    };
}

void CompileCache(Ort::Env& environment, const Ort::ConstEpDevice& device,
                  const std::filesystem::path& sourceModel, const CachePaths& cache,
                  ProviderLog& log)
{
    std::filesystem::create_directories(cache.model.parent_path());
    log.Reset();

    Ort::SessionOptions sessionOptions;
    ConfigureSession(environment, device, sessionOptions);
    Ort::ModelCompilationOptions compilationOptions(environment, sessionOptions);
    compilationOptions.SetInputModelPath(sourceModel.c_str());
    compilationOptions.SetOutputModelPath(cache.model.c_str());
    compilationOptions.SetEpContextEmbedMode(true);
    compilationOptions.SetGraphOptimizationLevel(ORT_DISABLE_ALL);
    compilationOptions.SetFlags(OrtCompileApiFlags_ERROR_IF_NO_NODES_COMPILED);

    const auto started = std::chrono::steady_clock::now();
    const Ort::Status status = Ort::CompileModel(environment, compilationOptions);
    const auto elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started);
    if (!status.IsOK())
    {
        throw std::runtime_error(std::string("ONNX JIT failed: ") + status.GetErrorMessage());
    }

    if (log.HasWarpFallback())
    {
        std::filesystem::remove(cache.model);
        throw std::runtime_error("The Windows ML CG Execution Provider fell back to WARP. "
                                 "A physical GPU run is required.");
    }

    if (!std::filesystem::is_regular_file(cache.model) ||
        std::filesystem::file_size(cache.model) == 0)
    {
        throw std::runtime_error("ONNX JIT did not produce a valid cache.");
    }

    std::printf("Compile time: %.2f seconds\n", elapsed.count());
}

void CompileAndPublishCache(Ort::Env& environment, const Ort::ConstEpDevice& device,
                            const std::filesystem::path& sourceModel, const CachePaths& cache,
                            const std::string& manifest, ProviderLog& log)
{
    CachePaths temporaryCache = cache;
    temporaryCache.model = TemporaryCachePath(cache.model, L".tmp");
    temporaryCache.manifest = TemporaryCachePath(cache.manifest, L".tmp");

    auto cleanup = wil::scope_exit([&]() noexcept {
        std::error_code error;
        std::filesystem::remove(temporaryCache.model, error);
        error.clear();
        std::filesystem::remove(temporaryCache.manifest, error);
    });

    CompileCache(environment, device, sourceModel, temporaryCache, log);
    WriteTextFile(temporaryCache.manifest, manifest);

    // The manifest is the readiness marker. Publish the complete model
    // first, then atomically publish the matching manifest.
    PublishFile(temporaryCache.model, cache.model);
    PublishFile(temporaryCache.manifest, cache.manifest);
    cleanup.release();
}

std::vector<float> LoadImage(const std::filesystem::path& path)
{
    namespace image = winmlsamples::tensors::image;
    winmlsamples::tensors::ComApartment apartment;
    THROW_IF_FAILED_MSG(apartment.hr, "CoInitializeEx");

    ComPtr<IWICImagingFactory> factory;
    THROW_IF_FAILED_MSG(image::CreateWicFactory(factory.GetAddressOf()), "CreateWicFactory");
    std::vector<BYTE> bgra;
    THROW_IF_FAILED_MSG(
        image::DecodeImageToBgra(factory.Get(), path.c_str(), kImageWidth, kImageHeight, bgra),
        "DecodeImageToBgra");

    const size_t plane = static_cast<size_t>(kImageWidth) * kImageHeight;
    std::vector<float> nchw(plane * 3);
    for (size_t pixel = 0; pixel < plane; ++pixel)
    {
        for (size_t channel = 0; channel < 3; ++channel)
        {
            const float normalized = static_cast<float>(bgra[pixel * 4 + (2 - channel)]) / 255.0f;
            nchw[channel * plane + pixel] =
                (normalized - kImageNetNorm.mean[channel]) / kImageNetNorm.std[channel];
        }
    }
    return nchw;
}

void VerifyProviderAssignment(Ort::Session& session)
{
    const std::vector<Ort::ConstEpDevice> assigned = session.GetEpDeviceForInputs();
    const bool usesWinMLCGEp =
        std::any_of(assigned.begin(), assigned.end(), [](const Ort::ConstEpDevice& device) {
            return static_cast<const OrtEpDevice*>(device) &&
                   std::string(device.EpName()) == kProviderName;
        });
    if (!usesWinMLCGEp)
    {
        throw std::runtime_error("The session did not assign model inputs to WinMLCGEP.");
    }
}

std::wstring RunClassification(Ort::Session& session, const std::filesystem::path& imagePath,
                               const std::filesystem::path& labelsPath)
{
    if (session.GetInputCount() != 1 || session.GetOutputCount() != 1)
    {
        throw std::runtime_error("The sample expects one model input and one output.");
    }

    const Ort::TypeInfo inputTypeInfo = session.GetInputTypeInfo(0);
    const Ort::ConstTensorTypeAndShapeInfo inputInfo = inputTypeInfo.GetTensorTypeAndShapeInfo();
    const std::vector<int64_t> expectedShape = {1, 3, kImageHeight, kImageWidth};
    if (inputInfo.GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT ||
        inputInfo.GetShape() != expectedShape)
    {
        throw std::runtime_error("The pinned SqueezeNet input contract changed.");
    }

    std::vector<float> inputData = LoadImage(imagePath);
    Ort::MemoryInfo memory = Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault);
    Ort::Value input = Ort::Value::CreateTensor<float>(memory, inputData.data(), inputData.size(),
                                                       expectedShape.data(), expectedShape.size());

    Ort::AllocatorWithDefaultOptions allocator;
    Ort::AllocatedStringPtr inputName = session.GetInputNameAllocated(0, allocator);
    Ort::AllocatedStringPtr outputName = session.GetOutputNameAllocated(0, allocator);
    const char* inputNames[] = {inputName.get()};
    const char* outputNames[] = {outputName.get()};

    std::vector<Ort::Value> outputs =
        session.Run(Ort::RunOptions{}, inputNames, &input, 1, outputNames, 1);
    if (outputs.size() != 1 || !outputs[0].IsTensor())
    {
        throw std::runtime_error("ONNX Runtime returned an unexpected output.");
    }

    const Ort::TensorTypeAndShapeInfo outputInfo = outputs[0].GetTensorTypeAndShapeInfo();
    if (outputInfo.GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT ||
        outputInfo.GetElementCount() != 1000)
    {
        throw std::runtime_error("The pinned SqueezeNet output contract changed.");
    }

    float* logits = outputs[0].GetTensorMutableData<float>();
    std::vector<float> probabilities(logits, logits + 1000);
    Softmax(probabilities.data(), probabilities.size());
    const auto best = std::max_element(probabilities.begin(), probabilities.end());
    const size_t index = static_cast<size_t>(best - probabilities.begin());
    const std::vector<std::wstring> labels = LoadClassLabels(labelsPath.wstring());
    if (labels.size() != probabilities.size())
    {
        throw std::runtime_error("The pinned SqueezeNet class labels changed.");
    }

    const std::wstring prediction = labels[index];
    std::wprintf(L"Prediction: %s (%.2f%%)\n", prediction.c_str(), *best * 100.0f);
    return prediction;
}

} // namespace

int wmain(int argc, wchar_t* argv[])
try
{
    const Options options = ParseOptions(argc, argv);
    const std::filesystem::path modelPath = FindModelPath(L"squeezenet\\SqueezeNet.onnx");
    const std::filesystem::path imagePath = FindModelPath(L"squeezenet\\sample-image.jpg");
    const std::filesystem::path labelsPath = FindModelPath(L"squeezenet\\imagenet_classes.txt");
    if (modelPath.empty() || imagePath.empty() || labelsPath.empty())
    {
        std::fprintf(stderr, "SqueezeNet assets are missing. Run check_artifacts.ps1 "
                             "-Sample onnx-runtime-winmlcgep from Samples\\Runtime.\n");
        return 1;
    }

    ProviderLog providerLog;
    Ort::Env environment(ORT_LOGGING_LEVEL_INFO, "onnx-runtime-winmlcgep", LogOrtMessage,
                         &providerLog);
    const Ort::ConstEpDevice device = RegisterAndSelectDevice(environment, options);
    const Ort::ConstHardwareDevice hardware = device.Device();
    const std::string packageVersion = RuntimePackageVersion();
    const std::string modelHash = Sha256File(modelPath);
    const std::string manifest = CacheManifest(modelHash, packageVersion, device);
    const CachePaths cache = ResolveCache(modelHash, packageVersion, device);

    std::printf("Provider: %s\n"
                "GPU: vendor=0x%04X device=0x%04X LUID=%s\n"
                "Runtime package: %s\n",
                kProviderName, hardware.VendorId(), hardware.DeviceId(), DeviceLuid(device).c_str(),
                packageVersion.c_str());

    std::unique_ptr<Ort::Session> session;
    {
        const std::wstring mutexName = L"Local\\WindowsMLSample.WinMLCGEP." +
                                       std::wstring(cache.identity.begin(), cache.identity.end());
        wil::unique_mutex mutex(mutexName.c_str());
        const DWORD waitResult = WaitForSingleObject(mutex.get(), 5 * 60 * 1000);
        THROW_HR_IF_MSG(HRESULT_FROM_WIN32(ERROR_TIMEOUT), waitResult == WAIT_TIMEOUT,
                        "WaitForSingleObject");
        THROW_LAST_ERROR_IF_MSG(waitResult != WAIT_OBJECT_0 && waitResult != WAIT_ABANDONED,
                                "WaitForSingleObject");
        auto cacheLock = mutex.ReleaseMutex_scope_exit();
        if (options.recompile)
        {
            RemoveCacheFiles(cache);
        }

        const bool modelExists = std::filesystem::is_regular_file(cache.model);
        const bool manifestMatches = std::filesystem::is_regular_file(cache.manifest) &&
                                     ReadTextFile(cache.manifest) == manifest;
        if (modelExists && manifestMatches)
        {
            std::printf("Cache: warm\n");
        }
        else
        {
            if (modelExists || std::filesystem::exists(cache.manifest))
            {
                std::printf("Cache: recovering incomplete publication\n");
            }
            RemoveCacheFiles(cache);
            std::printf("Cache: cold compile\n");
            CompileAndPublishCache(environment, device, modelPath, cache, manifest, providerLog);
        }

        providerLog.Reset();
        Ort::SessionOptions sessionOptions;
        ConfigureSession(environment, device, sessionOptions);
        session = std::make_unique<Ort::Session>(environment, cache.model.c_str(), sessionOptions);
        if (providerLog.HasWarpFallback())
        {
            throw std::runtime_error("The Windows ML CG Execution Provider fell back to WARP. "
                                     "A physical GPU run is required.");
        }
        VerifyProviderAssignment(*session);
    }

    const std::string adapterName = providerLog.AdapterName();
    std::printf("Adapter: %s\n", adapterName.empty() ? "physical D3D12 GPU" : adapterName.c_str());
    std::printf("Provider assignment: WinMLCGEP\n");

    const std::wstring prediction = RunClassification(*session, imagePath, labelsPath);
    if (prediction != L"Samoyed")
    {
        std::fprintf(stderr, "Expected the pinned sample image to classify as Samoyed.\n");
        return 1;
    }

    DWORD consoleMode = 0;
    if (GetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), &consoleMode) &&
        GetConsoleMode(GetStdHandle(STD_OUTPUT_HANDLE), &consoleMode))
    {
        std::printf("\nPress any key to exit...\n");
        std::fflush(stdout);
        _getch();
    }

    return 0;
}
catch (const Ort::Exception& error)
{
    std::fprintf(stderr, "ONNX Runtime error: %s\n", error.what());
    return 1;
}
catch (const std::exception& error)
{
    std::fprintf(stderr, "Error: %s\n", error.what());
    return 1;
}
