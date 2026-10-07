// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Image classification with a one-stage Windows ML Runtime pipeline.
//
// Classify one image with SqueezeNet and print the top five ImageNet labels.
//
//   wic::CreateTensorFromBitmapFile decodes, resizes, and normalizes the image
//   into a 224x224 planar RGB tensor on a CPU target in one call. The model
//   stage gets its own target from -Device and -Ep, so input and model
//   placement are chosen separately. LoadModelFromFile, AddModelStage,
//   RequestOutput(0), and Build set up the stage, and Build fails if the
//   model cannot run on that target. BindInput(0) binds by position, Run
//   executes, and GetOutput(0) is read through a CPU lock (synchronized
//   access if it stays on the device) before softmax picks the top five.
//
// Run it
//   .\run_image_classification.ps1 [-Device cpu|gpu|npu] [-Ep <name>]
//                                  [-Performance|-Efficiency] [-Diagnostics]
//
// Learn more (paths relative to this file)
//   ../../../../docs/Runtime/tutorials/01-first-inference.md
//   ../../../../docs/Runtime/tutorials/02-tensors-and-media.md
//   ../../../../docs/api-reference/CommonPatterns.md (patterns 1, 5, and 8)
//   ../../../../docs/api-reference/IWinMLPipelineBuilder.md
//
// COM note: WIC is a COM API, so this sample initializes a COM apartment for the
// WIC adapter. The Windows ML Runtime does not require COM initialization.

#include <cstdio>
#include <string>
#include <vector>

#include <Windows.h>
#include <wincodec.h>

#include <WinMLRuntime.h>
#include <WinMLTensor.h>

#include <winml/tensors/WinMLTensorWIC.h>

#include "common.h"
#include "image_utils.h"
#include "tensorization_common.h"
#include "tensorization_inference.h"
#include "tensorization_image.h"

namespace ws = winmlsamples::tensors;
using namespace winml::tensors;

namespace
{
constexpr UINT32 kImageW = 224;
constexpr UINT32 kImageH = 224;

// An ImageNet classifier wants NCHW float32, RGB, with standard per-channel
// normalization. The layout describes that tensor shape and normalization.
WINML_IMAGE_TENSOR_DESC MakeImageNetLayout() noexcept
{
    WINML_IMAGE_TENSOR_DESC layout = WinMLTensorsDefaultImageLayout();
    layout.normalization = WinMLTensorsImageNetNormalization();
    return layout;
}
} // namespace

int wmain(int argc, wchar_t* argv[])
{
    if (ws::ShowInferenceUsageIfRequested(argc, argv, L"image-classification.exe", L""))
    {
        return 0;
    }

    std::wprintf(L"=== Windows ML Runtime: Image classification ===\n\n");

    const ws::InferenceOptions opts = ws::ParseInferenceOptions(argc, argv);
    if (opts.verbose)
    {
        ws::EnableVerboseInferenceLogging();
    }

    // The photograph and the model are listed by check_artifacts.ps1.
    const std::wstring imagePath = FindModelPath(L"squeezenet\\sample-image.jpg");
    const std::wstring modelPath = FindModelPath(L"squeezenet\\SqueezeNet.onnx");
    if (imagePath.empty() || modelPath.empty())
    {
        std::wprintf(L"ERROR: sample image and/or SqueezeNet.onnx not found.\n");
        std::wprintf(L"Run .\\check_artifacts.ps1 to see how to obtain them, then re-run.\n");
        return 1;
    }

    // COM apartment for the WIC adapter only.
    ws::ComApartment com;
    CHECK_HR_MSG(com.hr, L"CoInitializeEx (required by WIC)");

    // The Runtime is the factory for everything else. The image tensor is
    // created on a CPU execution target.
    ComPtr<IWinMLRuntime> runtime;
    CHECK_HR(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    ComPtr<IWinMLExecutionTarget> imageTarget;
    CHECK_HR(runtime->CreateCpuExecutionTarget(imageTarget.GetAddressOf()));
    std::wprintf(L"[1/5] Runtime and CPU image-tensor target created.\n");

    // One call decodes the file, resizes it to 224x224, converts it to planar
    // RGB, and applies the ImageNet normalization.
    const WINML_IMAGE_TENSOR_DESC layout = MakeImageNetLayout();
    ComPtr<IWinMLTensor> inputTensor;
    CHECK_HR(wic::CreateTensorFromBitmapFile(imageTarget.Get(), imagePath.c_str(), &layout,
                                             inputTensor.GetAddressOf()));
    CHECK_HR_IF(!ws::TensorShapeMatches(inputTensor.Get(), {1, 3, kImageH, kImageW}), E_FAIL);
    std::wprintf(L"[2/5] Image decoded, resized, and normalized to [1,3,%u,%u].\n", kImageH,
                 kImageW);

    // The model stage gets its own target from --device and --ep.
    // PrepareAndCreateInferenceTarget (shared/tensorization_inference.h) registers
    // a requested provider, then creates the target.
    ComPtr<IWinMLExecutionTarget> stageTarget;
    CHECK_HR(ws::PrepareAndCreateInferenceTarget(runtime.Get(), opts.device,
                                                 stageTarget.GetAddressOf()));
    ComPtr<IWinMLModel> model;
    CHECK_HR(runtime->LoadModelFromFile(modelPath.c_str(), nullptr, model.GetAddressOf()));
    std::wprintf(L"[3/5] Model ready for %s.\n", DeviceTypeName(opts.device.deviceType));

    // A stage places one model on one execution target. The Runtime publishes
    // only outputs requested before Build, and Build validates that the model
    // can run on the target.
    ComPtr<IWinMLPipelineBuilder> builder;
    CHECK_HR(runtime->CreatePipelineBuilder(builder.GetAddressOf()));
    ComPtr<IWinMLStage> stage;
    CHECK_HR(builder->AddModelStage(model.Get(), stageTarget.Get(), L"squeezenet",
                                    stage.GetAddressOf()));
    CHECK_HR(stage->RequestOutput(0));
    ComPtr<IWinMLPipeline> pipeline;
    CHECK_HR(builder->Build(pipeline.GetAddressOf()));

    ComPtr<IWinMLExecutionTarget> resolvedTarget;
    WINML_EXECUTION_TARGET_KIND resolvedKind{};
    CHECK_HR(stage->GetExecutionTarget(resolvedTarget.GetAddressOf()));
    CHECK_HR(resolvedTarget->GetKind(&resolvedKind));
    std::wprintf(L"[4/5] One-stage pipeline built. Resolved target: %s\n",
                 DeviceTypeName(resolvedKind));

    // Bindings are positional: input ordinal 0, not an ONNX tensor name. The CPU
    // input tensor can feed a stage on any target.
    CHECK_HR(stage->BindInput(0, inputTensor.Get()));
    CHECK_HR(pipeline->Run());
    ComPtr<IWinMLTensor> output;
    CHECK_HR(stage->GetOutput(0, output.GetAddressOf()));

    // ReadTensorFloats takes a CPU lock and falls back to synchronized access
    // when the output is device-resident.
    UINT64 numClasses = 1;
    CHECK_HR(ws::GetTensorElementCount(output.Get(), &numClasses));
    std::vector<float> probs;
    CHECK_HR_IF(!ws::ReadTensorFloats(output.Get(), numClasses, probs), E_FAIL);
    Softmax(probs.data(), probs.size());
    const auto top = GetTopK(probs.data(), probs.size(), 5);
    CHECK_HR_IF(top.empty(), E_FAIL);
    std::wprintf(L"[5/5] Inference complete.\n\n");

    const std::vector<std::wstring> labels =
        LoadClassLabels(SiblingPath(modelPath, L"imagenet_classes.txt"));
    std::wprintf(L"Top-5 predictions:\n");
    for (size_t i = 0; i < top.size(); ++i)
    {
        std::wprintf(L"  #%zu  %6.2f%%  %s\n", i + 1, top[i].value * 100.0f,
                     ClassLabel(labels, top[i].index));
    }

    return 0;
}
