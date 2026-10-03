// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Super-resolution: turn a photo frame into an upscaled image tensor.
//
// Upscale a photo 2x with SESR, starting from a Media Foundation NV12 frame
// as a video app would, and save the 512x512 result as a PNG.
//
//   mf::CreateTensorFromMFSample turns the NV12 IMFSample into a
//   [1,3,256,256] float RGB tensor (0-255) on a CPU target, converting
//   color, size, and layout in one call; the sample checks it against a WIC
//   reference decode. The model stage runs on the -Device/-Ep target, and
//   BindInput(0) takes the CPU tensor directly. After Run, GetOutput(0) is
//   packed back into NV12 with mf::CreateMFSampleFromTensor, and
//   wic::EncodeTensorToFile writes the output tensor as a PNG.
//
// Run it
//   .\run_super_resolution.ps1 [-Device cpu|gpu|npu] [-Ep <name>]
//                               [-Output <path>] [-Show]
//                               [-Performance|-Efficiency] [-Diagnostics]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Runtime/tutorials/02-tensors-and-media.md
//   ../../../../docs/Runtime/tutorials/05-accelerators.md
//   ../../../../docs/api-reference/CommonPatterns.md (patterns 1, 5, and 8)

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include <Windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfobjects.h>
#include <shellapi.h>
#include <wincodec.h>

#include <WinMLRuntime.h>
#include <WinMLTensor.h>

#include <winml/tensors/WinMLTensorMediaFoundation.h>
#include <winml/tensors/WinMLTensorWIC.h>

#include "common.h"
#include "tensorization_common.h"
#include "tensorization_image.h"
#include "tensorization_inference.h"
#include "tensorization_video.h"

namespace ws = winmlsamples::tensors;
using namespace winml::tensors;

namespace
{

constexpr UINT32 kSrcW = 256;
constexpr UINT32 kSrcH = 256;
constexpr UINT32 kModelW = 256;
constexpr UINT32 kModelH = 256;

class ScopedMF
{
public:
    ScopedMF() noexcept : m_hr(MFStartup(MF_VERSION, MFSTARTUP_LITE))
    {
    }

    ~ScopedMF()
    {
        if (SUCCEEDED(m_hr))
        {
            MFShutdown();
        }
    }

    HRESULT hr() const noexcept
    {
        return m_hr;
    }

private:
    HRESULT m_hr;
};

WINML_VIDEO_FRAME_METADATA MakeNv12Metadata(UINT32 width, UINT32 height) noexcept
{
    WINML_VIDEO_FRAME_METADATA metadata = WinMLTensorsEmptyVideoFrameMetadata();
    metadata.sourceWidth = width;
    metadata.sourceHeight = height;
    metadata.sourceStride = width;
    return metadata;
}

// Builds the RGB NCHW float layout SESR consumes. The model uses the original
// [0,255] pixel range for both input and output.
WINML_IMAGE_TENSOR_DESC MakeRgbLayout(UINT32 width, UINT32 height) noexcept
{
    WINML_IMAGE_TENSOR_DESC layout = WinMLTensorsDefaultImageLayout();
    layout.layout = WINML_TENSOR_LAYOUT_FORMAT_NCHW;
    layout.dataType = WINML_TENSOR_DATA_TYPE_FLOAT32;
    layout.channelOrder = WINML_TENSOR_CHANNEL_ORDER_RGB;
    layout.channels = 3;
    layout.width = width;
    layout.height = height;
    layout.batchSize = 1;
    layout.normalization = WinMLTensorsIdentityNormalization();
    return layout;
}

} // namespace

int wmain(int argc, wchar_t* argv[])
{
    if (ws::ShowInferenceUsageIfRequested(
            argc, argv, L"webcam-super-resolution.exe", L"",
            L"  --output <path>         Saved PNG path (default: super-resolution-output.png)\n"
            L"  --model <path>          Override the SESR ONNX model\n"
            L"  --show                  Open the saved image with the default viewer\n"))
    {
        return 0;
    }

    std::wstring outputPath = L"super-resolution-output.png";
    std::wstring modelPathOverride;
    const bool showOutput = HasArg(argc, argv, L"--show");
    for (int i = 1; i < argc; ++i)
    {
        if (_wcsicmp(argv[i], L"--output") == 0)
        {
            if (i + 1 >= argc)
            {
                std::wprintf(L"ERROR: --output requires a path.\n");
                return 1;
            }

            outputPath = argv[++i];
        }
        else if (_wcsicmp(argv[i], L"--model") == 0)
        {
            if (i + 1 >= argc)
            {
                std::wprintf(L"ERROR: --model requires a path.\n");
                return 1;
            }

            modelPathOverride = argv[++i];
        }
    }

    std::wprintf(L"=== Windows ML Runtime: Super-resolution ===\n\n");
    std::wprintf(L"CreateTensorFromMFSample turns an NV12 video frame into the RGB tensor\n");
    std::wprintf(L"SESR expects; CreateMFSampleFromTensor turns the upscaled output back into\n");
    std::wprintf(L"a video frame.\n");
    std::wprintf(L"Real photo: %ux%u -> model input [1,3,%u,%u] -> upscaled 2x.\n\n", kSrcW, kSrcH,
                 kModelH, kModelW);

    const ws::InferenceOptions opts = ws::ParseInferenceOptions(argc, argv);
    if (opts.verbose)
    {
        ws::EnableVerboseInferenceLogging();
    }

    ws::ComApartment com;
    CHECK_HR_MSG(com.hr, L"CoInitializeEx (Media Foundation and WIC need a COM apartment)");

    ScopedMF mf;
    CHECK_HR_MSG(mf.hr(), L"MFStartup (video tensorization needs Media Foundation)");
    std::wprintf(L"[1/5] COM apartment + Media Foundation initialized.\n");

    // CreateCpuRuntimeAndDevice (shared/tensorization_common.h) calls
    // WinMLCreateRuntime -> CreateCpuExecutionTarget for CPU tensorization.
    ComPtr<IWinMLRuntime> runtime;
    ComPtr<IWinMLExecutionTarget> tensorTarget;
    CHECK_HR(ws::CreateCpuRuntimeAndDevice(runtime.GetAddressOf(), tensorTarget.GetAddressOf()));
    std::wprintf(L"[2/5] WinML runtime + CPU tensorization execution target created.\n");

    const WINML_VIDEO_FRAME_METADATA srcMetadata = MakeNv12Metadata(kSrcW, kSrcH);
    const WINML_IMAGE_TENSOR_DESC rgbLayout = MakeRgbLayout(kModelW, kModelH);
    const size_t modelPlane = static_cast<size_t>(kModelW) * kModelH;

    // The sample uses one JPEG asset twice: a BGRA reference for validation and
    // an NV12 IMFSample that exercises the Media Foundation tensor adapter.
    const std::wstring imagePath = FindModelPath(L"sesr_x2\\sample-image.jpg");
    CHECK_HR_MSG(
        imagePath.empty() ? E_FAIL : S_OK,
        L"sample-image.jpg not found. Run .\\check_artifacts.ps1 -Sample super-resolution.");
    ComPtr<IWICImagingFactory> wicFactory;
    CHECK_HR(ws::image::CreateWicFactory(wicFactory.GetAddressOf()));

    std::vector<BYTE> referenceBgra;
    CHECK_HR(ws::image::DecodeImageToBgra(wicFactory.Get(), imagePath.c_str(), kModelW, kModelH,
                                          referenceBgra));
    double refR = 0.0, refG = 0.0, refB = 0.0;
    for (size_t i = 0; i < modelPlane; ++i)
    {
        refB += referenceBgra[i * 4 + 0];
        refG += referenceBgra[i * 4 + 1];
        refR += referenceBgra[i * 4 + 2];
    }

    refR /= modelPlane;
    refG /= modelPlane;
    refB /= modelPlane;

    ComPtr<IMFSample> sample;
    CHECK_HR(ws::video::CreateNv12SampleFromImage(wicFactory.Get(), imagePath.c_str(), kSrcW, kSrcH,
                                                  sample.GetAddressOf()));
    std::wprintf(L"[3/5] Real-photo NV12 source frame ready.\n");

    // CreateTensorFromMFSample performs color conversion, resize, layout, and
    // range handling in one Runtime tensorization call.
    ComPtr<IWinMLTensor> modelInput;
    CHECK_HR(mf::CreateTensorFromMFSample(tensorTarget.Get(), sample.Get(), &rgbLayout,
                                          &srcMetadata, modelInput.GetAddressOf()));

    // Verify the adapter wrote RGB values whose per-channel mean tracks
    // the decoded photo (after the NV12 4:2:0 / BT.601 round trip).
    std::vector<float> rgb;
    const bool readOk = ws::ReadTensorFloats(modelInput.Get(), 3 * modelPlane, rgb);
    bool meanOk = false;
    double rMean = 0.0;
    double gMean = 0.0;
    double bMean = 0.0;
    if (readOk)
    {
        for (size_t i = 0; i < modelPlane; ++i)
        {
            rMean += rgb[i];
            gMean += rgb[modelPlane + i];
            bMean += rgb[2 * modelPlane + i];
        }

        rMean /= modelPlane;
        gMean /= modelPlane;
        bMean /= modelPlane;
        constexpr double tol = 8.0;
        meanOk = std::abs(rMean - refR) < tol && std::abs(gMean - refG) < tol &&
                 std::abs(bMean - refB) < tol;
    }

    std::wprintf(L"[4/5] NV12 frame tensorized into RGB input.\n");
    std::wprintf(L"      RESULT: shape [1,3,%u,%u], channel means R=%.3f G=%.3f B=%.3f.\n", kModelH,
                 kModelW, rMean, gMean, bMean);
    CHECK_HR_IF(!ws::TensorShapeMatches(modelInput.Get(), {1, 3, kModelH, kModelW}) || !readOk ||
                    !meanOk,
                E_FAIL);

    // CreateMFSampleFromTensor is the reverse adapter: it packs the RGB tensor
    // into an NV12 frame for display or handoff.
    const WINML_VIDEO_FRAME_METADATA egressMetadata = MakeNv12Metadata(kModelW, kModelH);
    ComPtr<IMFSample> recovered;
    CHECK_HR(mf::CreateMFSampleFromTensor(tensorTarget.Get(), modelInput.Get(), &rgbLayout,
                                          &egressMetadata, nullptr, recovered.GetAddressOf()));
    std::wprintf(
        L"      RESULT: CreateMFSampleFromTensor produced an NV12 frame from the tensor.\n");
    CHECK_HR_IF(recovered == nullptr, E_FAIL);

    const std::wstring modelPath =
        modelPathOverride.empty() ? FindModelPath(L"sesr_x2.onnx") : modelPathOverride;
    CHECK_HR_MSG(modelPath.empty() ? E_FAIL : S_OK,
                 L"sesr_x2.onnx not found. Run .\\check_artifacts.ps1 -Sample super-resolution.");

    std::wprintf(L"[5/5] Running SESR 2x super-resolution inference.\n");
    std::wprintf(L"      inference device: %s (policy: %s)\n",
                 DeviceTypeName(opts.device.deviceType),
                 ws::ExecutionPolicyName(opts.device.executionPolicy));

    // PrepareAndCreateInferenceTarget (shared/tensorization_inference.h) registers
    // any requested provider, then creates the target that owns the model stage.
    ComPtr<IWinMLExecutionTarget> inferenceTarget;
    ComPtr<IWinMLExecutionTarget> inferenceTensorTarget;
    CHECK_HR(ws::PrepareAndCreateInferenceTarget(runtime.Get(), opts.device,
                                                 inferenceTarget.GetAddressOf(),
                                                 inferenceTensorTarget.GetAddressOf()));

    // LoadModelPipeline (shared/tensorization_inference.h) performs the core
    // sequence: LoadModelFromFile -> CreatePipelineBuilder -> AddModelStage ->
    // RequestOutput(0) -> Build. Build validates the selected stage target.
    ComPtr<IWinMLModel> model;
    ComPtr<IWinMLPipeline> pipeline;
    ComPtr<IWinMLStage> stage;
    CHECK_HR(ws::LoadModelPipeline(runtime.Get(), modelPath, inferenceTarget.Get(),
                                   model.GetAddressOf(), pipeline.GetAddressOf(),
                                   stage.GetAddressOf()));

    // Bindings are positional. The CPU input tensor can feed the stage placed on
    // the requested target; Runtime handles any required transfer.
    CHECK_HR(stage->BindInput(0, modelInput.Get()));
    CHECK_HR(pipeline->Run());

    ComPtr<IWinMLTensor> upscaled;
    CHECK_HR(stage->GetOutput(0, upscaled.GetAddressOf()));

    WINML_TENSOR_DESC outDesc{};
    CHECK_HR(upscaled->GetDesc(&outDesc));
    CHECK_HR_IF(outDesc.dimensionCount != 4 || outDesc.dimensions == nullptr, E_FAIL);

    const UINT32 outH = static_cast<UINT32>(outDesc.dimensions[2]);
    const UINT32 outW = static_cast<UINT32>(outDesc.dimensions[3]);
    CHECK_HR_IF(outW == 0 || outH == 0, E_FAIL);

    // The model output is still an RGB tensor. The adapters below demonstrate
    // both media-sample packing and direct image-file encoding from that tensor.
    const WINML_IMAGE_TENSOR_DESC outLayout = MakeRgbLayout(outW, outH);
    const WINML_VIDEO_FRAME_METADATA outMetadata = MakeNv12Metadata(outW, outH);
    ComPtr<IMFSample> outSample;
    CHECK_HR(mf::CreateMFSampleFromTensor(tensorTarget.Get(), upscaled.Get(), &outLayout,
                                          &outMetadata, nullptr, outSample.GetAddressOf()));
    CHECK_HR_IF(outSample == nullptr, E_FAIL);

    std::wprintf(L"      RESULT: upscaled %u x %u -> %u x %u (%.1fx) and packed to NV12.\n",
                 kModelW, kModelH, outW, outH, static_cast<double>(outW) / kModelW);

    if (FileExists(outputPath) && !DeleteFileW(outputPath.c_str()))
    {
        CHECK_HR(HRESULT_FROM_WIN32(GetLastError()));
    }

    CHECK_HR(wic::EncodeTensorToFile(inferenceTensorTarget.Get(), upscaled.Get(),
                                     outputPath.c_str(), GUID_ContainerFormatPng, &outLayout));

    std::vector<wchar_t> fullPathBuffer;
    DWORD fullPathLength = GetFullPathNameW(outputPath.c_str(), 0, nullptr, nullptr);
    if (fullPathLength > 0)
    {
        fullPathBuffer.resize(fullPathLength);
        if (GetFullPathNameW(outputPath.c_str(), static_cast<DWORD>(fullPathBuffer.size()),
                             fullPathBuffer.data(), nullptr) == 0)
        {
            fullPathBuffer.clear();
        }
    }

    const wchar_t* savedPath = fullPathBuffer.empty() ? outputPath.c_str() : fullPathBuffer.data();
    std::wprintf(L"      RESULT: saved %s\n", savedPath);

    if (showOutput)
    {
        const INT_PTR shellResult = reinterpret_cast<INT_PTR>(
            ShellExecuteW(nullptr, L"open", savedPath, nullptr, nullptr, SW_SHOWNORMAL));
        if (shellResult <= 32)
        {
            std::wprintf(L"      WARNING: the default image viewer could not be opened (%lld).\n",
                         static_cast<long long>(shellResult));
        }
    }

    std::wprintf(L"\nSuper-resolution tensorization fed SESR end to end.\n");
    return 0;
}