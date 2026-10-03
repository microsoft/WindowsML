// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared ASR Task helpers for the speech and composition samples. They wrap
// Whisper Runtime pipeline construction, waveform tensor creation, typed ASR
// configuration, streaming, and terminal-result validation.

#pragma once

#include "task_runtime.h"

#include <WinMLTensorFactory.h>
#include <WinMLTokenizer.h>
#include <winml/tasks/WinMLTasks.hpp>
#include <winml/tasks/speech/asr/WhisperAutomaticSpeechRecognition.hpp>

#include <cstdio>
#include <filesystem>
#include <string>

#include "audio_utils.h"

namespace winmlsamples::tasks
{

struct SpeechRecognitionObjects
{
    ComPtr<IWinMLModel> encoderModel;
    ComPtr<IWinMLModel> decoderModel;
    ComPtr<IWinMLExecutionTarget> pipelineTarget;
    ComPtr<IWinMLExecutionTarget> tensorTarget;
    winml::tasks::speech::asr::AutomaticSpeechRecognition task;

    void Reset() noexcept
    {
        task = {};
        tensorTarget.Reset();
        pipelineTarget.Reset();
        decoderModel.Reset();
        encoderModel.Reset();
    }
};

struct SpeechRecognitionResult
{
    std::wstring transcript;
    WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON finishReason =
        WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON_ERROR;
};

// Locate one required Whisper graph before it is loaded through IWinMLRuntime.
inline HRESULT ResolveWhisperModelPath(const std::filesystem::path& modelDirectory, LPCWSTR stem,
                                       std::filesystem::path& modelPath) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, stem);
    const auto onnxPath = modelDirectory / (std::wstring(stem) + L".onnx");
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND),
                 !std::filesystem::is_regular_file(onnxPath));
    modelPath = onnxPath;
    return S_OK;
}
CATCH_RETURN()

// Whisper is represented as independent encoder and decoder pipelines. The
// typed Task owns their orchestration while the caller owns Runtime placement.
inline HRESULT BuildWhisperSpeechRecognition(IWinMLRuntime* runtime, IWinMLExecutionTarget* target,
                                             const std::filesystem::path& modelDirectory,
                                             SpeechRecognitionObjects& objects) noexcept
try
{
    objects.Reset();
    RETURN_HR_IF_NULL(E_POINTER, runtime);
    RETURN_HR_IF_NULL(E_POINTER, target);

    std::filesystem::path encoderPath;
    std::filesystem::path decoderPath;
    const auto tokenizerPath = modelDirectory / L"tokenizer.json";
    RETURN_IF_FAILED(ResolveWhisperModelPath(modelDirectory, L"encoder_model", encoderPath));
    RETURN_IF_FAILED(ResolveWhisperModelPath(modelDirectory, L"decoder_model", decoderPath));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND),
                 !std::filesystem::is_regular_file(tokenizerPath));

    ComPtr<IWinMLPipeline> encoderPipeline;
    ComPtr<IWinMLStage> encoderStage;
    const std::pair<LPCWSTR, INT64> encoderDimensions[] = {
        {L"feature_size", kWhisperMelBins},
        {L"encoder_sequence_length", kWhisperTimeFrames},
    };

    // This prepared encoder needs two ONNX Runtime optimizations disabled.
    // The session configuration keeps that model-specific choice next to the
    // stage that consumes it.
    const std::pair<LPCWSTR, LPCWSTR> encoderSessionConfig[] = {
        {L"optimization.disable_specified_optimizers", L"NhwcTransformer;ConvActivationFusion"},
    };

    RETURN_IF_FAILED(BuildSingleStagePipeline(
        runtime, target, encoderPath.c_str(), L"task-whisper-encoder", encoderDimensions,
        objects.encoderModel, encoderPipeline, encoderStage, encoderSessionConfig));

    ComPtr<IWinMLPipeline> decoderPipeline;
    ComPtr<IWinMLStage> decoderStage;
    RETURN_IF_FAILED(BuildSingleStagePipeline(runtime, target, decoderPath.c_str(),
                                              L"task-whisper-decoder", {}, objects.decoderModel,
                                              decoderPipeline, decoderStage));

    ComPtr<IWinMLTokenizer> tokenizer;
    RETURN_IF_FAILED(WinMLCreateTokenizerFromFile(tokenizerPath.c_str(), tokenizer.GetAddressOf()));

    const winml::tasks::speech::asr::AutomaticSpeechRecognitionArguments arguments{
        runtime,
        tokenizer.Get(),
        {
            encoderPipeline.Get(),
            encoderStage.Get(),
            decoderPipeline.Get(),
            decoderStage.Get(),
            target,
        },
    };

    return winml::tasks::speech::asr::BuildAutomaticSpeechRecognition(arguments, objects.task);
}
CATCH_RETURN()

// The Task consumes a Runtime tensor plus waveform metadata. This adapter keeps
// file parsing outside the Task API and checks the expected PCM format.
inline HRESULT CreateWaveformTensor(
    IWinMLExecutionTarget* target, LPCWSTR wavPath, ComPtr<IWinMLTensor>& waveform,
    WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA& metadata) noexcept
try
{
    waveform.Reset();
    metadata = {};
    RETURN_HR_IF_NULL(E_POINTER, target);
    RETURN_HR_IF_NULL(E_POINTER, wavPath);

    WavData audio;
    RETURN_IF_FAILED(LoadWavFile(wavPath, audio));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 audio.samples.empty() || audio.sampleRate != kWhisperSampleRate);

    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
    format.nChannels = 1;
    format.nSamplesPerSec = kWhisperSampleRate;
    format.wBitsPerSample = sizeof(float) * 8;
    format.nBlockAlign = sizeof(float);
    format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

    ComPtr<IWinMLAudioTensorFactory> factory;
    RETURN_IF_FAILED(target->QueryInterface(IID_PPV_ARGS(factory.GetAddressOf())));
    return winml::tasks::CreateWhisperWaveformTensorFromPcm(
        factory.Get(), reinterpret_cast<const BYTE*>(audio.samples.data()),
        audio.samples.size() * sizeof(float), &format, waveform.GetAddressOf(), &metadata);
}
CATCH_RETURN()

// Like text generation, ASR exposes incremental updates followed by a terminal
// result. Reading both demonstrates streaming without giving up final status.
inline HRESULT Transcribe(IWinMLAutomaticSpeechRecognitionSession* session, IWinMLTensor* waveform,
                          const WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA& metadata,
                          IWinMLCancellationSource* cancellation, bool printFragments,
                          SpeechRecognitionResult& transcription) noexcept
try
{
    transcription = {};
    RETURN_HR_IF_NULL(E_POINTER, session);
    RETURN_HR_IF_NULL(E_POINTER, waveform);

    ComPtr<IWinMLAutomaticSpeechRecognitionPullStream> stream;
    RETURN_IF_FAILED(
        session->TranscribeWaveform(waveform, &metadata, cancellation, stream.GetAddressOf()));
    StreamCloser streamCloser(stream.Get());

    std::wstring streamedTranscript;
    for (;;)
    {
        WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS status{};
        LPCWSTR fragment = nullptr;
        RETURN_IF_FAILED(stream->ReadNext(&status, &fragment));
        if (status == WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_UPDATE)
        {
            // Only an update carries text. A completed read reports no fragment
            // and must reach the final result below.
            RETURN_HR_IF_NULL(E_UNEXPECTED, fragment);
            streamedTranscript += fragment;
            if (printFragments)
            {
                std::wprintf(L"%ls", fragment);
                std::fflush(stdout);
            }

            continue;
        }

        RETURN_HR_IF(E_UNEXPECTED,
                     status != WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_COMPLETED);
        break;
    }

    ComPtr<IWinMLAutomaticSpeechRecognitionResult> result;
    RETURN_IF_FAILED(stream->GetResult(result.GetAddressOf()));
    LPCWSTR transcript = nullptr;
    HRESULT error = E_UNEXPECTED;
    RETURN_IF_FAILED(result->GetTranscript(&transcript));
    RETURN_HR_IF_NULL(E_UNEXPECTED, transcript);
    RETURN_IF_FAILED(result->GetFinishReason(&transcription.finishReason));
    RETURN_IF_FAILED(result->GetErrorCode(&error));
    RETURN_IF_FAILED(error);
    RETURN_HR_IF(E_FAIL, transcription.finishReason ==
                             WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON_ERROR);

    transcription.transcript = transcript;
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 transcription.transcript != streamedTranscript);
    return S_OK;
}
CATCH_RETURN()

} // namespace winmlsamples::tasks
