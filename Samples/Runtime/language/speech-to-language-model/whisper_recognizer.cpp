// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Speech recognizer implementation for the composed speech-to-language sample.
//
// What this helper shows
//   - LoadModelFromFile for encoder_model.onnx and decoder_model.onnx.
//   - CreatePipelineBuilder -> AddModelStage -> RequestOutput -> Build for each
//     speech stage.
//   - IWinMLOrtStageOptions where used to set documented ORT session options.
//   - Tensor binding/readback around an application-owned greedy speech loop.
//

#include "whisper_recognizer.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <utility>
#include <vector>

#include <wil/resource.h>
#include <wil/result.h>
#include <wrl/client.h>

#include <WinMLRuntime.h>

#include "../../shared/audio_utils.h"
#include "../../shared/execution_target_utils.h"
#include "../../shared/llm_inference.h"
#include "../../speech/whisper/whisper_tokenizer.h"

namespace
{
using Microsoft::WRL::ComPtr;

HRESULT Utf8ToUtf16(const std::string& utf8, std::wstring& utf16) noexcept
try
{
    if (utf8.empty())
    {
        utf16.clear();
        return S_OK;
    }

    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(),
                                          static_cast<int>(utf8.size()), nullptr, 0);
    if (count == 0)
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    utf16.resize(count);
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(),
                            static_cast<int>(utf8.size()), utf16.data(), count) == 0)
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    return S_OK;
}
CATCH_RETURN()

HRESULT BuildSingleStagePipeline(IWinMLRuntime* runtime, IWinMLExecutionTarget* target,
                                 IWinMLModel* model, LPCWSTR name, IWinMLPipeline** pipeline,
                                 IWinMLStage** stage, bool disableLayoutTransform = false) noexcept
{
    ComPtr<IWinMLPipelineBuilder> builder;
    RETURN_IF_FAILED(runtime->CreatePipelineBuilder(builder.GetAddressOf()));
    RETURN_IF_FAILED(builder->AddModelStage(model, target, name, stage));
    RETURN_IF_FAILED((*stage)->RequestOutput(0));

    // ORT-specific stage options are set before Build. This sample disables two
    // graph optimizers for this artifact to keep the exported convolutions in a
    // form accepted by the selected ORT backend.
    // The encoder's two convolutions are 1-D. On Arm64 the CPU provider's
    // channels-last layout transform rewrites them into NhwcFusedConv, whose
    // kernel accepts 2-D kernels only, so execution fails. Some GPU
    // providers' Conv kernels reject the fused activation that
    // ConvActivationFusion attaches to these convolutions. Disable those two
    // optimizers rather than lowering the whole optimization level. This
    // matches the standalone whisper sample.
    if (disableLayoutTransform)
    {
        ComPtr<IWinMLOrtStageOptions> ortStageOptions;
        if (SUCCEEDED((*stage)->QueryInterface(IID_PPV_ARGS(ortStageOptions.GetAddressOf()))))
        {
            RETURN_IF_FAILED(
                ortStageOptions->SetSessionConfigEntry(L"optimization.disable_specified_optimizers",
                                                       L"NhwcTransformer;ConvActivationFusion"));
        }
    }

    return builder->Build(pipeline);
}
} // namespace

struct WhisperRecognizer::Implementation
{
    ComPtr<IWinMLRuntime> runtime;
    ComPtr<IWinMLExecutionTarget> pipelineTarget;
    ComPtr<IWinMLExecutionTarget> target;
    ComPtr<IWinMLModel> encoderModel;
    ComPtr<IWinMLModel> decoderModel;
    ComPtr<IWinMLPipeline> encoderPipeline;
    ComPtr<IWinMLPipeline> decoderPipeline;
    ComPtr<IWinMLStage> encoderStage;
    ComPtr<IWinMLStage> decoderStage;
    std::unordered_map<int64_t, std::string> vocabulary;
};

WhisperRecognizer::WhisperRecognizer() = default;
WhisperRecognizer::~WhisperRecognizer() = default;

IWinMLRuntime* WhisperRecognizer::Runtime() const noexcept
{
    return m_implementation ? m_implementation->runtime.Get() : nullptr;
}

IWinMLExecutionTarget* WhisperRecognizer::Target() const noexcept
{
    return m_implementation ? m_implementation->target.Get() : nullptr;
}

IWinMLPipeline* WhisperRecognizer::EncoderPipeline() const noexcept
{
    return m_implementation ? m_implementation->encoderPipeline.Get() : nullptr;
}

IWinMLPipeline* WhisperRecognizer::DecoderPipeline() const noexcept
{
    return m_implementation ? m_implementation->decoderPipeline.Get() : nullptr;
}

IWinMLStage* WhisperRecognizer::EncoderStage() const noexcept
{
    return m_implementation ? m_implementation->encoderStage.Get() : nullptr;
}

IWinMLStage* WhisperRecognizer::DecoderStage() const noexcept
{
    return m_implementation ? m_implementation->decoderStage.Get() : nullptr;
}

HRESULT WhisperRecognizer::Load(const std::wstring& modelDirectory,
                                const DeviceArgs& device) noexcept
try
{
    const std::filesystem::path directory(modelDirectory);
    const auto encoderPath = directory / L"encoder_model.onnx";
    const auto decoderPath = directory / L"decoder_model.onnx";
    const auto vocabularyPath = directory / L"vocab.json";
    for (const auto& path : {encoderPath, decoderPath, vocabularyPath})
    {
        if (!std::filesystem::is_regular_file(path))
        {
            fwprintf(stderr, L"Whisper model file not found: %ls\n", path.c_str());
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        }
    }

    auto implementation = std::make_unique<Implementation>();
    RETURN_IF_FAILED(WinMLCreateRuntime(
        __uuidof(IWinMLRuntime), reinterpret_cast<void**>(implementation->runtime.GetAddressOf())));
    implementation->vocabulary = LoadVocab(vocabularyPath.c_str());
    if (implementation->vocabulary.empty())
    {
        fwprintf(stderr, L"Whisper vocabulary is invalid or unsupported: %ls\n",
                 vocabularyPath.c_str());
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    RETURN_IF_FAILED(CreateExecutionTarget(implementation->runtime.Get(), device,
                                           implementation->pipelineTarget.GetAddressOf(),
                                           implementation->target.GetAddressOf()));
    // Load both models, then build the two retained speech pipelines. Each
    // helper call performs CreatePipelineBuilder -> AddModelStage ->
    // RequestOutput -> Build.
    RETURN_IF_FAILED(implementation->runtime->LoadModelFromFile(
        encoderPath.c_str(), nullptr, implementation->encoderModel.GetAddressOf()));
    RETURN_IF_FAILED(implementation->runtime->LoadModelFromFile(
        decoderPath.c_str(), nullptr, implementation->decoderModel.GetAddressOf()));
    RETURN_IF_FAILED(BuildSingleStagePipeline(
        implementation->runtime.Get(), implementation->pipelineTarget.Get(),
        implementation->encoderModel.Get(), L"whisper-encoder",
        implementation->encoderPipeline.GetAddressOf(), implementation->encoderStage.GetAddressOf(),
        /*disableLayoutTransform*/ true));
    RETURN_IF_FAILED(BuildSingleStagePipeline(
        implementation->runtime.Get(), implementation->pipelineTarget.Get(),
        implementation->decoderModel.Get(), L"whisper-decoder",
        implementation->decoderPipeline.GetAddressOf(),
        implementation->decoderStage.GetAddressOf()));

    ComPtr<IWinMLStageSchema> decoderSchema;
    RETURN_IF_FAILED(
        implementation->decoderStage->QueryInterface(IID_PPV_ARGS(decoderSchema.GetAddressOf())));
    WINML_TENSOR_DESC tokenDescription = {};
    RETURN_IF_FAILED(decoderSchema->GetInputTensorDesc(0, &tokenDescription));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 tokenDescription.dimensionCount != 2 || tokenDescription.dimensions == nullptr ||
                     tokenDescription.dimensions[0] != 1 ||
                     tokenDescription.dimensions[1] != kMaxDecodeTokens);

    m_implementation = std::move(implementation);
    return S_OK;
}
CATCH_RETURN()

HRESULT WhisperRecognizer::TranscribeWav(
    const std::wstring& wavPath, std::string& transcriptUtf8, std::wstring& transcriptUtf16,
    const std::function<void(const std::wstring&)>& onStatus) noexcept
try
{
    if (m_implementation == nullptr)
    {
        return E_UNEXPECTED;
    }

    WavData wav;
    RETURN_IF_FAILED(LoadWavFile(wavPath.c_str(), wav));
    if (wav.truncated && onStatus)
    {
        onStatus(L"Input exceeds 30 seconds; only the first 30 seconds will be transcribed.");
    }

    if (onStatus)
    {
        onStatus(L"Computing Whisper mel spectrogram...");
    }

    const MelSpectrogramData mel =
        ::ComputeMelSpectrogram(wav.samples.data(), wav.samples.size(), wav.sampleRate);

    UINT64 melDimensions[] = {1, kWhisperMelBins, kWhisperTimeFrames};
    WINML_TENSOR_DESC melDescription = {};
    melDescription.dataType = WINML_TENSOR_DATA_TYPE_FLOAT32;
    melDescription.dimensionCount = ARRAYSIZE(melDimensions);
    melDescription.dimensions = melDimensions;
    ComPtr<IWinMLTensor> melTensor;
    RETURN_IF_FAILED(CreateTensorOnTarget(
        m_implementation->target.Get(), &melDescription, mel.data.data(),
        static_cast<UINT64>(mel.data.size() * sizeof(float)), melTensor.GetAddressOf()));

    if (onStatus)
    {
        onStatus(L"Running Whisper encoder...");
    }

    // Speech encoder step: bind the mel tensor by ordinal, run, and read the
    // retained encoder output for the decoder input.
    RETURN_IF_FAILED(m_implementation->encoderStage->BindInput(0, melTensor.Get()));
    RETURN_IF_FAILED(m_implementation->encoderPipeline->Run());
    ComPtr<IWinMLTensor> encoderOutput;
    RETURN_IF_FAILED(m_implementation->encoderStage->GetOutput(0, encoderOutput.GetAddressOf()));

    LockedTensorData encoderData;
    // The encoder output is synchronized to CPU here, then uploaded to the
    // decoder target. Keeping this handoff explicit makes the two independently
    // built pipelines easy to inspect; a production pipeline can connect them
    // device-to-device.
    RETURN_IF_FAILED(LockTensorForReadAny(encoderOutput.Get(), &encoderData));
    if (encoderData.data == nullptr || encoderData.size == 0)
    {
        return E_FAIL;
    }

    WINML_TENSOR_DESC encoderDescription = {};
    RETURN_IF_FAILED(encoderOutput->GetDesc(&encoderDescription));
    if (encoderDescription.dataType != WINML_TENSOR_DATA_TYPE_FLOAT32 ||
        encoderDescription.dimensionCount == 0)
    {
        fwprintf(stderr, L"Whisper encoder output must be a FLOAT32 tensor.\n");
        return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
    }

    size_t expectedBytes = sizeof(float);
    for (UINT32 dimension = 0; dimension < encoderDescription.dimensionCount; ++dimension)
    {
        const UINT64 elementCount = encoderDescription.dimensions[dimension];
        if (elementCount == 0)
        {
            return E_INVALIDARG;
        }

        if (elementCount > std::numeric_limits<size_t>::max() / expectedBytes)
        {
            return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
        }

        expectedBytes *= static_cast<size_t>(elementCount);
    }

    if (encoderData.size < expectedBytes)
    {
        return E_FAIL;
    }

    ComPtr<IWinMLTensor> encoderState;
    RETURN_IF_FAILED(CreateTensorOnTarget(m_implementation->target.Get(), &encoderDescription,
                                          encoderData.data, expectedBytes,
                                          encoderState.GetAddressOf()));

    if (onStatus)
    {
        onStatus(L"Greedy-decoding Whisper tokens...");
    }

    std::vector<int64_t> tokens = {kSOT, kEnglish, kTranscribe, kNoTimestamps};
    std::vector<int64_t> tokenBuffer(kMaxDecodeTokens, kEOT);
    const UINT64 tokenDimensions[] = {1, kMaxDecodeTokens};
    WhisperTokenDecoder textDecoder(m_implementation->vocabulary);
    bool reachedEndOfTranscript = false;
    transcriptUtf8.clear();
    while (tokens.size() < kMaxDecodeTokens)
    {
        std::copy(tokens.begin(), tokens.end(), tokenBuffer.begin());
        WINML_TENSOR_DESC tokenDescription = {};
        tokenDescription.dataType = WINML_TENSOR_DATA_TYPE_INT64;
        tokenDescription.dimensionCount = ARRAYSIZE(tokenDimensions);
        tokenDescription.dimensions = tokenDimensions;
        ComPtr<IWinMLTensor> tokenTensor;
        RETURN_IF_FAILED(CreateTensorOnTarget(
            m_implementation->target.Get(), &tokenDescription, tokenBuffer.data(),
            static_cast<UINT64>(tokenBuffer.size() * sizeof(int64_t)), tokenTensor.GetAddressOf()));

        // Speech decoder step: bind token history plus encoder state, run, read
        // logits for the current position, and append the selected token.
        RETURN_IF_FAILED(m_implementation->decoderStage->BindInput(0, tokenTensor.Get()));
        RETURN_IF_FAILED(m_implementation->decoderStage->BindInput(1, encoderState.Get()));
        RETURN_IF_FAILED(m_implementation->decoderPipeline->Run());
        ComPtr<IWinMLTensor> decoderOutput;
        RETURN_IF_FAILED(
            m_implementation->decoderStage->GetOutput(0, decoderOutput.GetAddressOf()));

        const size_t currentPosition = tokens.size() - 1;
        std::vector<float> positionLogits;
        RETURN_IF_FAILED(ReadFloat32LogitsAtPosition(
            m_implementation->target.Get(), decoderOutput.Get(),
            static_cast<UINT32>(currentPosition), kVocabSize, positionLogits));
        const int64_t token = ArgmaxToken(positionLogits);
        if (token == kEOT)
        {
            reachedEndOfTranscript = true;
            break;
        }

        transcriptUtf8 += textDecoder.DecodeToken(token);
        tokens.push_back(token);
    }

    transcriptUtf8 += textDecoder.Finish();
    if (onStatus)
    {
        onStatus(reachedEndOfTranscript ? L"Whisper decoding reached EOT."
                                        : L"Whisper decoding stopped at decoder capacity.");
    }

    if (!transcriptUtf8.empty() && transcriptUtf8.front() == ' ')
    {
        transcriptUtf8.erase(transcriptUtf8.begin());
    }

    RETURN_IF_FAILED(Utf8ToUtf16(transcriptUtf8, transcriptUtf16));
    return S_OK;
}
CATCH_RETURN()
