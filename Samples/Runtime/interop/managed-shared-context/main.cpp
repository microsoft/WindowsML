// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Managed shared context: run two provider-pinned stages in one group.
//
// Chain two small generated "add one" models on one provider-pinned target in
// an ORT shared-context group; an input of 3 must come out as 5 everywhere.
//
//   IWinMLOrtCompatibility::CreateExecutionTarget pins the provider and
//   hardware kind, and the input is filled on a separate CPU target. Before
//   Build, each stage gets SetOrtSessionConfigEntry values: quiet logging,
//   profiling when requested, and, for a non-CPU provider,
//   session.disable_cpu_ep_fallback=1, so Build fails rather than moving
//   nodes to the CPU. SetOrtSharedContextGroup("arithmetic") puts both stages
//   in one group (skipped with -DisableSharing), and the Runtime initializes
//   them one after another. Connect links output 0 to input 0, so the app
//   binds only the first stage's input and reads the second stage's output.
//
// Run it
//   .\run_managed_shared_context.ps1 [-Provider <name>]
//                                     [-DeviceKind cpu|gpu|npu]
//                                     [-FirstModel <path>] [-SecondModel <path>]
//                                     [-OutputDirectory <path>] [-DisableSharing]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Runtime/tutorials/05-accelerators.md
//   ../../../../docs/api-reference/IWinMLOrtCompatibility.md
//   ../../../../docs/api-reference/IWinMLOrtStageOptions.md
//   ../../../../docs/api-reference/CommonPatterns.md (pattern 4)

#include <windows.h>
#include <wil/com.h>
#include <wil/result.h>
#include <WinMLRuntimeRAII.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace
{

struct Arguments
{
    std::wstring firstModelPath;
    std::wstring secondModelPath;
    std::wstring providerName;
    std::wstring profilePrefix;
    WINML_EXECUTION_TARGET_KIND targetKind = WINML_EXECUTION_TARGET_KIND_NPU;
    bool hasTargetKind = false;
    bool disableSharing = false;
};

struct PipelineResult
{
    float firstValue = 0.0f;
    size_t elementCount = 0;
    bool allValuesMatch = false;
};

void PrintUsage()
{
    std::fwprintf(stderr, L"Usage: managed-shared-context.exe "
                          L"--first-model <path> --second-model <path> "
                          L"--provider <name> --device-kind <cpu|gpu|npu> "
                          L"[--profile-prefix <path-prefix>] [--disable-sharing]\n");
}

WINML_EXECUTION_TARGET_KIND ParseTargetKind(PCWSTR value)
{
    if (_wcsicmp(value, L"cpu") == 0)
    {
        return WINML_EXECUTION_TARGET_KIND_CPU;
    }

    if (_wcsicmp(value, L"gpu") == 0)
    {
        return WINML_EXECUTION_TARGET_KIND_GPU;
    }

    if (_wcsicmp(value, L"npu") == 0)
    {
        return WINML_EXECUTION_TARGET_KIND_NPU;
    }

    THROW_HR_MSG(E_INVALIDARG, "Device kind must be cpu, gpu, or npu.");
}

Arguments ParseArguments(int argumentCount, wchar_t** arguments)
{
    Arguments parsed;
    for (int index = 1; index < argumentCount; ++index)
    {
        const std::wstring_view argument(arguments[index]);
        if (argument == L"--disable-sharing")
        {
            parsed.disableSharing = true;
            continue;
        }

        THROW_HR_IF_MSG(E_INVALIDARG, index + 1 >= argumentCount,
                        "Missing value for a command-line argument.");

        PCWSTR value = arguments[++index];
        if (argument == L"--first-model")
        {
            parsed.firstModelPath = value;
        }
        else if (argument == L"--second-model")
        {
            parsed.secondModelPath = value;
        }
        else if (argument == L"--provider")
        {
            parsed.providerName = value;
        }
        else if (argument == L"--device-kind")
        {
            parsed.targetKind = ParseTargetKind(value);
            parsed.hasTargetKind = true;
        }
        else if (argument == L"--profile-prefix")
        {
            parsed.profilePrefix = value;
        }
        else
        {
            THROW_HR_MSG(E_INVALIDARG, "Unknown command-line argument.");
        }
    }

    THROW_HR_IF_MSG(E_INVALIDARG,
                    parsed.firstModelPath.empty() || parsed.secondModelPath.empty() ||
                        parsed.providerName.empty() || !parsed.hasTargetKind,
                    "All required command-line arguments must be supplied.");

    return parsed;
}

PCWSTR TargetKindName(WINML_EXECUTION_TARGET_KIND kind)
{
    switch (kind)
    {
    case WINML_EXECUTION_TARGET_KIND_CPU:
        return L"cpu";
    case WINML_EXECUTION_TARGET_KIND_GPU:
        return L"gpu";
    case WINML_EXECUTION_TARGET_KIND_NPU:
        return L"npu";
    default:
        return L"unknown";
    }
}

WinML::ExecutionTarget CreateOrtTarget(const WinML::Runtime& runtime, PCWSTR providerName,
                                       WINML_EXECUTION_TARGET_KIND kind)
{
    // IWinMLOrtCompatibility creates a provider-pinned execution target from
    // the runtime, provider name, and requested hardware class.
    wil::com_ptr<IWinMLOrtCompatibility> compatibility;
    THROW_IF_FAILED(runtime.get()->QueryInterface(IID_PPV_ARGS(compatibility.put())));

    wil::com_ptr<IWinMLExecutionTarget> target;
    THROW_IF_FAILED(
        compatibility->CreateExecutionTarget(providerName, kind, nullptr, target.put()));

    return WinML::ExecutionTarget(std::move(target));
}

PipelineResult RunPipeline(const Arguments& arguments)
{
    // WinMLCreateRuntime returns the entry point for targets, model loading,
    // and pipeline builders.
    wil::com_ptr<IWinMLRuntime> runtimePtr;
    THROW_IF_FAILED(
        WinMLCreateRuntime(__uuidof(IWinMLRuntime), reinterpret_cast<void**>(runtimePtr.put())));
    WinML::Runtime runtime(std::move(runtimePtr));
    auto target = CreateOrtTarget(runtime, arguments.providerName.c_str(), arguments.targetKind);
    auto cpuTarget = runtime.CreateCpuExecutionTarget();
    // Load both generated models as artifact handles; Build later prepares each
    // stage for the selected target.
    auto firstModel = runtime.LoadModelFromFile(arguments.firstModelPath.c_str());
    auto secondModel = runtime.LoadModelFromFile(arguments.secondModelPath.c_str());

    // The builder records two model stages and a same-iteration edge from the
    // first stage's output 0 to the second stage's input 0.
    auto builder = runtime.CreatePipelineBuilder();
    auto firstStage = builder.AddModelStage(firstModel, target, L"add-one");
    auto secondStage = builder.AddModelStage(secondModel, target, L"add-one-again");
    secondStage.RequestOutput(0);

    firstStage.SetOrtSessionConfigEntry(L"session.log_severity_level", L"4");
    secondStage.SetOrtSessionConfigEntry(L"session.log_severity_level", L"4");

    if (_wcsicmp(arguments.providerName.c_str(), L"CPUExecutionProvider") != 0)
    {
        firstStage.SetOrtSessionConfigEntry(L"session.disable_cpu_ep_fallback", L"1");
        secondStage.SetOrtSessionConfigEntry(L"session.disable_cpu_ep_fallback", L"1");
    }

    // Session config entries and shared-context membership must be set before
    // Build creates the provider-backed sessions.
    if (!arguments.profilePrefix.empty())
    {
        firstStage.SetOrtSessionConfigEntry(L"session.enable_profiling",
                                            arguments.profilePrefix.c_str());
        secondStage.SetOrtSessionConfigEntry(L"session.enable_profiling",
                                             arguments.profilePrefix.c_str());
    }

    if (!arguments.disableSharing)
    {
        firstStage.SetOrtSharedContextGroup(L"arithmetic");
        secondStage.SetOrtSharedContextGroup(L"arithmetic");
    }

    // Build validates the graph, materializes both stages, and consumes the
    // builder so no more stages or edges can be added.
    builder.Connect(firstStage, 0, secondStage, 0);
    auto pipeline = builder.Build();

    const UINT64 dimensions[] = {1, 16, 32, 32};
    const WINML_TENSOR_DESC descriptor{
        WINML_TENSOR_DATA_TYPE_FLOAT32,
        ARRAYSIZE(dimensions),
        dimensions,
    };

    constexpr size_t elementCount = 1 * 16 * 32 * 32;
    std::vector<float> inputValues(elementCount, 3.0f);
    // CreateTensor allocates descriptor-compatible Runtime tensor storage on the
    // CPU target and copies the app-provided float buffer into it.
    auto input =
        cpuTarget.CreateTensor(descriptor, inputValues.data(), inputValues.size() * sizeof(float));

    // Bind the pipeline's external input on the first stage; the connected edge
    // feeds the second stage without an application-side copy.
    firstStage.BindInput(0, input);
    pipeline.Run();

    // RequestOutput above publishes the second stage's output. Lock provides CPU
    // access so the sample can validate every element.
    auto output = secondStage.GetOutput(0);
    auto dataLock = output.Lock(WINML_TENSOR_LOCK_MODE_READ);
    const auto [data, byteCount] = dataLock.GetData();
    THROW_HR_IF(E_UNEXPECTED, byteCount != elementCount * sizeof(float));

    std::vector<float> outputValues(elementCount);
    std::memcpy(outputValues.data(), data, outputValues.size() * sizeof(float));
    const bool allValuesMatch =
        std::all_of(outputValues.begin(), outputValues.end(), [](float value) {
            return std::fabs(value - 5.0f) <= 0.00001f;
        });

    return {
        outputValues.front(),
        outputValues.size(),
        allValuesMatch,
    };
}

} // namespace

int wmain(int argumentCount, wchar_t** arguments) noexcept
try
{
    if (argumentCount == 2 && std::wstring_view(arguments[1]) == L"--help")
    {
        PrintUsage();
        return 0;
    }

    const Arguments parsed = ParseArguments(argumentCount, arguments);
    const PipelineResult result = RunPipeline(parsed);
    constexpr float expected = 5.0f;

    std::wprintf(
        L"Provider: %ls\n"
        L"Device kind: %ls\n"
        L"Managed sharing: %ls\n"
        L"CPU fallback: %ls\n"
        L"Input: 3\n"
        L"First output: %.6g\n"
        L"Output elements: %zu\n"
        L"Expected: %.6g\n",
        parsed.providerName.c_str(), TargetKindName(parsed.targetKind),
        parsed.disableSharing ? L"disabled" : L"enabled",
        _wcsicmp(parsed.providerName.c_str(), L"CPUExecutionProvider") == 0 ? L"not applicable"
                                                                            : L"disabled",
        static_cast<double>(result.firstValue), result.elementCount, static_cast<double>(expected));

    if (!result.allValuesMatch)
    {
        std::fwprintf(stderr, L"Result: FAIL\n");
        return 1;
    }

    if (!parsed.profilePrefix.empty())
    {
        std::wprintf(L"ORT profile prefix: %ls\n", parsed.profilePrefix.c_str());
    }

    std::wprintf(L"Result: PASS\n");
    return 0;
}
catch (...)
{
    const HRESULT result = wil::ResultFromCaughtException();
    std::fwprintf(stderr, L"Sample failed: 0x%08X\n", static_cast<unsigned int>(result));
    PrintUsage();
    return 1;
}
