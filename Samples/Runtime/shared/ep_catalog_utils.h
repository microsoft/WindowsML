// Copyright (C) Microsoft Corporation. All rights reserved.
//
// ep_catalog_utils.h -- Shared provider setup for Windows ML Runtime samples.
//
// Wraps the Windows ML EP catalog and the public provider registration path used
// by C++ samples. An explicit --ep prepares and registers a catalog provider
// when needed, then execution-target helpers pin that provider through
// IWinMLOrtCompatibility. A run without --ep uses the Runtime default.
//
// Usage:
//   PrepareAndValidateExecutionProviders(
//       WINML_EXECUTION_TARGET_KIND_GPU,
//       providerName);

#pragma once

#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>

#include <Windows.h>
#include <WinMLEpCatalog.h>
#include <WinMLRuntime.h>

// Backend C API registration lets samples make prepared catalog providers visible
// before creating provider-pinned Runtime execution targets.
#include <winml/onnxruntime_c_api.h>

#include "common.h"

struct EpPreparationStats
{
    uint32_t ready = 0;
    uint32_t notReady = 0;
    uint32_t ensured = 0;
    uint32_t registered = 0;
    uint32_t notPresent = 0;
    uint32_t ensureFailed = 0;
    uint32_t skippedWebGpu = 0;
    uint32_t filteredSkipped = 0;
};

struct EpCatalogGuard
{
    WinMLEpCatalogHandle handle = nullptr;

    ~EpCatalogGuard()
    {
        if (handle)
        {
            WinMLEpCatalogRelease(handle);
        }
    }
};

struct EpCallbackContext
{
    EpPreparationStats* stats;
    const char* filterEpName;
};

// Severity used when the sample first creates the process-global OrtEnv. Default
// is WARNING (quiet). EnableVerboseSampleLogging() raises it to VERBOSE before
// the environment is created so node-placement logs show which execution provider
// served each node.
static OrtLoggingLevel s_sampleOrtLogLevel = ORT_LOGGING_LEVEL_WARNING;

// Returns an OrtEnv at the configured severity. Creating it before Runtime
// objects are created also applies the selected logging level to sample runs.
// Returns nullptr on failure.
static OrtEnv* GetOrCreateSampleOrtEnv()
{
    static OrtEnv* s_env = nullptr;
    if (s_env)
    {
        return s_env;
    }

    const OrtApi* api = OrtGetApiBase()->GetApi(ORT_API_VERSION);
    if (!api)
    {
        return nullptr;
    }

    OrtStatus* st = api->CreateEnv(s_sampleOrtLogLevel, "WinMLSample", &s_env);
    if (st || !s_env)
    {
        if (st)
        {
            api->ReleaseStatus(st);
        }

        s_env = nullptr;
    }

    return s_env;
}

// Raise the sample's process-global ORT environment to VERBOSE and create it now,
// before the Runtime initializes. This surfaces node-placement logs that identify
// the execution provider that served the graph. Call once, early in wmain(),
// before WinMLCreateRuntime. Must run before the environment is first created to
// take effect.
static void EnableVerboseSampleLogging()
{
    s_sampleOrtLogLevel = ORT_LOGGING_LEVEL_VERBOSE;
    wprintf(L"  Verbose logging enabled: watch for node-placement lines that identify\n"
            L"  which execution provider served each node.\n");
    GetOrCreateSampleOrtEnv();
}

// Register a catalog provider library with the process-global backend
// environment so Runtime target creation can discover the provider's devices.
static void RegisterEpWithOrt(WinMLEpHandle ep, const char* epName, EpPreparationStats* stats)
{
    // Get the provider library path from the catalog.
    size_t pathSize = 0;
    if (FAILED(WinMLEpGetLibraryPathSize(ep, &pathSize)) || pathSize == 0)
    {
        return;
    }

    std::string libraryPath(pathSize, '\0');
    size_t used = 0;
    if (FAILED(WinMLEpGetLibraryPath(ep, pathSize, libraryPath.data(), &used)))
    {
        return;
    }

    libraryPath.resize(used > 0 ? used - 1 : 0);
    if (libraryPath.empty())
    {
        return;
    }

    // Convert to wide string for the backend registration API.
    int wideLen = MultiByteToWideChar(CP_UTF8, 0, libraryPath.c_str(), -1, nullptr, 0);
    if (wideLen <= 0)
    {
        return;
    }

    std::wstring wideLibPath(static_cast<size_t>(wideLen), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, libraryPath.c_str(), -1, wideLibPath.data(), wideLen);
    wideLibPath.resize(static_cast<size_t>(wideLen - 1));

    // Register so GetEpDevices can report the provider's devices.
    const OrtApi* api = OrtGetApiBase()->GetApi(ORT_API_VERSION);
    OrtEnv* env = GetOrCreateSampleOrtEnv();
    if (!api || !env)
    {
        return;
    }

    OrtStatus* st = api->RegisterExecutionProviderLibrary(env, epName, wideLibPath.c_str());
    if (st)
    {
        // Registration can fail if the EP is already registered (e.g.,
        // from a prior call or the Runtime's own registration). That's
        // expected and not an error.
        api->ReleaseStatus(st);
    }
    else if (stats)
    {
        ++stats->registered;
    }
}

// Providers supplied directly by the Runtime for a GPU request do not need
// sample-side catalog registration.
static bool IsWebGpuEpName(const char* name)
{
    return name && _stricmp(name, "WebGpuExecutionProvider") == 0;
}

static BOOL CALLBACK PrepareEpCallback(WinMLEpHandle ep, const WinMLEpInfo* info, void* context)
{
    auto* ctx = static_cast<EpCallbackContext*>(context);
    if (!ep || !info || !ctx || !ctx->stats)
    {
        return TRUE;
    }

    auto* stats = ctx->stats;
    const char* epName = info->name ? info->name : "";

    if (IsWebGpuEpName(epName))
    {
        // If the user explicitly asked for this provider, explain that it is
        // Runtime-owned; otherwise count it and continue.
        if (ctx->filterEpName &&
            (_stricmp(epName, ctx->filterEpName) == 0 ||
             _stricmp(epName, ResolveExecutionProviderName(ctx->filterEpName)) == 0))
        {
            wprintf(
                L"  WebGPU is Runtime-owned and needs no app registration. Request the GPU device without a provider name.\n");
        }

        ++stats->skippedWebGpu;
        return TRUE;
    }

    // Skip everything that does not match the requested name or short alias.
    if (_stricmp(epName, ctx->filterEpName) != 0 &&
        _stricmp(epName, ResolveExecutionProviderName(ctx->filterEpName)) != 0)
    {
        ++stats->filteredSkipped;
        return TRUE;
    }

    // Certification is not a selection gate for a provider the app explicitly
    // registers. Report it so the user can make an informed choice.
    const wchar_t* certText = (info->certification == WinMLEpCertification_Certified)
                                  ? L"certified"
                                  : L"uncertified (app opt-in)";
    wprintf(L"  EP %hs: %s, ", epName, certText);

    if (info->readyState == WinMLEpReadyState_NotPresent)
    {
        ++stats->notPresent;
        wprintf(L"not installed -- skipped.\n");
        return TRUE;
    }

    if (info->readyState == WinMLEpReadyState_Ready)
    {
        ++stats->ready;
        wprintf(L"ready");
    }
    else if (info->readyState == WinMLEpReadyState_NotReady)
    {
        ++stats->notReady;
        HRESULT hr = WinMLEpEnsureReady(ep);
        if (FAILED(hr))
        {
            ++stats->ensureFailed;
            wprintf(L"EnsureReady failed (0x%08X) -- skipped.\n", static_cast<unsigned int>(hr));
            return TRUE;
        }

        ++stats->ensured;
        wprintf(L"ensured");
    }
    else
    {
        wprintf(L"unknown ready state -- skipped.\n");
        return TRUE;
    }

    RegisterEpWithOrt(ep, epName, stats);
    wprintf(L", registered.\n");
    return TRUE;
}

static const wchar_t* DeviceTypeNameForEp(WINML_EXECUTION_TARGET_KIND deviceType)
{
    switch (deviceType)
    {
    case WINML_EXECUTION_TARGET_KIND_GPU:
        return L"GPU";
    case WINML_EXECUTION_TARGET_KIND_CPU:
        return L"CPU";
    case WINML_EXECUTION_TARGET_KIND_NPU:
        return L"NPU";
    default:
        return L"UNKNOWN";
    }
}

// filterEpName uses a narrow string because provider identifiers are UTF-8.
static HRESULT PrepareExecutionProviders(WINML_EXECUTION_TARGET_KIND deviceType,
                                         _In_z_ const char* filterEpName)
{
    wprintf(L"  Preparing EP '%hs' for %s execution...\n", filterEpName,
            DeviceTypeNameForEp(deviceType));

    EpCatalogGuard catalog;
    HRESULT hr = WinMLEpCatalogCreate(&catalog.handle);
    if (FAILED(hr))
    {
        wprintf(L"  ERROR: EP catalog unavailable (0x%08X).\n", hr);
        return hr;
    }

    EpPreparationStats stats;
    EpCallbackContext ctx = {&stats, filterEpName};
    hr = WinMLEpCatalogEnumProviders(catalog.handle, PrepareEpCallback, &ctx);
    if (FAILED(hr))
    {
        wprintf(L"  ERROR: EP catalog enumeration failed (0x%08X).\n", hr);
        return hr;
    }

    wprintf(L"  EP summary: registered=%u, ready=%u, ensured=%u, notPresent=%u, ensureFailed=%u",
            stats.registered, stats.ready, stats.ensured, stats.notPresent, stats.ensureFailed);
    if (stats.filteredSkipped > 0)
    {
        wprintf(L", filtered=%u", stats.filteredSkipped);
    }

    if (stats.skippedWebGpu > 0)
    {
        wprintf(L", webgpuRuntimeOwned=%u", stats.skippedWebGpu);
    }

    wprintf(L"\n");

    if (stats.ready == 0 && stats.ensured == 0)
    {
        wprintf(L"  ERROR: requested provider '%hs' is not installed and ready.\n", filterEpName);
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }

    return S_OK;
}

static const char* HardwareDeviceTypeName(OrtHardwareDeviceType type)
{
    switch (type)
    {
    case OrtHardwareDeviceType_CPU:
        return "CPU";
    case OrtHardwareDeviceType_GPU:
        return "GPU";
    case OrtHardwareDeviceType_NPU:
        return "NPU";
    default:
        return "unknown";
    }
}

// Enumerate the provider devices visible to the backend and print each provider
// with its hardware class. Returns true when at least one provider exposes a
// device of requiredType.
static bool ReportDiscoveredEpDevices(OrtHardwareDeviceType requiredType,
                                      _In_opt_ const char* requiredProviderName)
{
    const OrtApi* api = OrtGetApiBase()->GetApi(ORT_API_VERSION);
    OrtEnv* env = GetOrCreateSampleOrtEnv();
    if (!api || !env)
    {
        wprintf(L"  WARNING: could not access the ORT environment to enumerate EP devices; "
                L"device-class check is unavailable.\n");
        return false;
    }

    const OrtEpDevice* const* devices = nullptr;
    size_t count = 0;
    OrtStatus* st = api->GetEpDevices(env, &devices, &count);
    if (st)
    {
        const char* msg = api->GetErrorMessage(st);
        wprintf(L"  WARNING: GetEpDevices failed (%hs); device-class check is unavailable.\n",
                msg ? msg : "no detail");
        api->ReleaseStatus(st);
        return false;
    }

    bool requiredFound = false;
    wprintf(L"  EP devices visible to the Runtime (provider -> hardware class):\n");
    for (size_t i = 0; i < count; ++i)
    {
        const char* epName = api->EpDevice_EpName(devices[i]);
        const OrtHardwareDevice* hw = api->EpDevice_Device(devices[i]);
        OrtHardwareDeviceType type = hw ? api->HardwareDevice_Type(hw) : OrtHardwareDeviceType_CPU;
        if (type == requiredType &&
            (!requiredProviderName || (epName && _stricmp(epName, requiredProviderName) == 0)))
        {
            requiredFound = true;
        }

        wprintf(L"    %hs -> %hs\n", epName ? epName : "(unnamed)", HardwareDeviceTypeName(type));
    }

    return requiredFound;
}

static OrtHardwareDeviceType HardwareTypeForDeviceClass(WINML_EXECUTION_TARGET_KIND deviceType)
{
    switch (deviceType)
    {
    case WINML_EXECUTION_TARGET_KIND_GPU:
        return OrtHardwareDeviceType_GPU;
    case WINML_EXECUTION_TARGET_KIND_NPU:
        return OrtHardwareDeviceType_NPU;
    default:
        return OrtHardwareDeviceType_CPU;
    }
}

// PCI identity of one device a provider exposes.
struct EpHardwareId
{
    uint32_t vendorId;
    uint32_t deviceId;
};

// Collects the hardware ids the named provider exposes for one device class.
//
// Adapter selection is otherwise provider-neutral. Returning the provider's
// device ids keeps selection within the set that provider reports.
//
// Returns false when the provider exposes no device of that class, which
// includes the case where it was never registered; the caller then keeps its
// unconstrained selection rather than inventing a constraint.
static bool TryGetEpHardwareIds(_In_z_ const char* providerName, OrtHardwareDeviceType requiredType,
                                std::vector<EpHardwareId>& hardwareIds)
{
    hardwareIds.clear();
    if (!providerName || providerName[0] == '\0')
    {
        return false;
    }

    const OrtApi* api = OrtGetApiBase()->GetApi(ORT_API_VERSION);
    OrtEnv* env = GetOrCreateSampleOrtEnv();
    if (!api || !env)
    {
        return false;
    }

    const OrtEpDevice* const* devices = nullptr;
    size_t count = 0;
    OrtStatus* st = api->GetEpDevices(env, &devices, &count);
    if (st)
    {
        api->ReleaseStatus(st);
        return false;
    }

    for (size_t i = 0; i < count; ++i)
    {
        const char* epName = api->EpDevice_EpName(devices[i]);
        if (!epName || _stricmp(epName, providerName) != 0)
        {
            continue;
        }

        const OrtHardwareDevice* hw = api->EpDevice_Device(devices[i]);
        if (!hw || api->HardwareDevice_Type(hw) != requiredType)
        {
            continue;
        }

        const EpHardwareId id = {api->HardwareDevice_VendorId(hw),
                                 api->HardwareDevice_DeviceId(hw)};
        bool alreadyPresent = false;
        for (const EpHardwareId& existing : hardwareIds)
        {
            if (existing.vendorId == id.vendorId && existing.deviceId == id.deviceId)
            {
                alreadyPresent = true;
                break;
            }
        }

        if (!alreadyPresent)
        {
            hardwareIds.push_back(id);
        }
    }

    return !hardwareIds.empty();
}

// Prepare a requested provider and check that it reports the requested hardware
// class. NPU requires --ep because there is no provider-neutral fallback.
static HRESULT PrepareAndValidateExecutionProviders(WINML_EXECUTION_TARGET_KIND deviceType,
                                                    _In_opt_ const char* filterEpName)
{
    const bool isNpu = (deviceType == WINML_EXECUTION_TARGET_KIND_NPU);
    const bool hasExplicitProvider = filterEpName && filterEpName[0] != '\0';

    if (isNpu && !hasExplicitProvider)
    {
        wprintf(L"  ERROR: --device npu requires --ep <provider>.\n");
        return E_INVALIDARG;
    }

    if (!hasExplicitProvider)
    {
        return S_OK;
    }

    const char* providerName = ResolveExecutionProviderName(filterEpName);
    if (_stricmp(providerName, "CPUExecutionProvider") == 0)
    {
        if (deviceType != WINML_EXECUTION_TARGET_KIND_CPU)
        {
            wprintf(L"  ERROR: CPUExecutionProvider supports CPU execution only.\n");
            return E_INVALIDARG;
        }

        wprintf(L"  CPUExecutionProvider is built in; no catalog registration is required.\n");
        return S_OK;
    }

    if (IsWebGpuEpName(providerName))
    {
        if (deviceType != WINML_EXECUTION_TARGET_KIND_GPU)
        {
            wprintf(L"  ERROR: WebGPU supports GPU execution only.\n");
            return E_INVALIDARG;
        }

        wprintf(L"  WebGPU is Runtime-owned; using the default GPU target.\n");
        return S_OK;
    }

    const HRESULT prepHr = PrepareExecutionProviders(deviceType, filterEpName);
    if (FAILED(prepHr))
    {
        wprintf(L"  ERROR: preparing execution provider failed (0x%08X).\n", prepHr);
        return prepHr;
    }

    const bool requiredFound =
        ReportDiscoveredEpDevices(HardwareTypeForDeviceClass(deviceType), providerName);
    if (!requiredFound)
    {
        wprintf(L"  ERROR: provider '%hs' does not expose the requested %s device.\n", providerName,
                DeviceTypeNameForEp(deviceType));
        return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
    }

    return S_OK;
}
