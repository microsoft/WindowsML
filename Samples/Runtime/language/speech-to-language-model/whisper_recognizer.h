// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Whisper Runtime recognizer used by language/speech-to-language-model.
//
// The helper wraps WAV preprocessing, two ONNX Runtime stages (encoder and
// decoder), tensor binding/readback, and sample-local token decoding. The
// language model remains in shared/language_model_loader.h.

#pragma once

#include <functional>
#include <memory>
#include <string>

#include <Windows.h>
#include <WinMLRuntime.h>

#include "common.h"

class WhisperRecognizer
{
public:
    // Sample-local, single-threaded recognizer for the Whisper-medium Q4F16
    // encoder/decoder assets used by this sample.
    WhisperRecognizer();
    ~WhisperRecognizer();

    WhisperRecognizer(const WhisperRecognizer&) = delete;
    WhisperRecognizer& operator=(const WhisperRecognizer&) = delete;

    // Creates the Runtime, execution target, encoder/decoder model stages, and
    // retained pipelines for the Whisper assets in modelDirectory.
    HRESULT Load(const std::wstring& modelDirectory, const DeviceArgs& device) noexcept;
    // Runs WAV -> mel tensor -> encoder -> greedy decoder loop and returns the
    // transcript in UTF-8 and UTF-16 for the composed language prompt.
    HRESULT TranscribeWav(const std::wstring& wavPath, std::string& transcriptUtf8,
                          std::wstring& transcriptUtf16,
                          const std::function<void(const std::wstring&)>& onStatus) noexcept;

    IWinMLRuntime* Runtime() const noexcept;
    IWinMLExecutionTarget* Target() const noexcept;
    IWinMLPipeline* EncoderPipeline() const noexcept;
    IWinMLPipeline* DecoderPipeline() const noexcept;
    IWinMLStage* EncoderStage() const noexcept;
    IWinMLStage* DecoderStage() const noexcept;

private:
    struct Implementation;
    std::unique_ptr<Implementation> m_implementation;
};
