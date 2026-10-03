// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Composition helper for language/speech-to-language-model.
//
// This class owns the sample's two Runtime flows: WhisperRecognizer loads
// and runs the speech ONNX stages, while shared/language_model_loader.h loads a
// unified ONNX/ORT or GGUF language model for transcript-grounded generation.

#pragma once

#include <functional>
#include <memory>
#include <string>

#include <Windows.h>
#include "language_model_loader.h"
#include "whisper_recognizer.h"

struct SpeechToLanguageModelResult
{
    std::string transcriptUtf8;
    std::wstring transcript;
    winmlsamples::language::GenerationResult generation;
};

class SpeechToLanguageModelTemplate
{
public:
    SpeechToLanguageModelTemplate();
    ~SpeechToLanguageModelTemplate();

    // Loads both task components so Run can compose their outputs without
    // rebuilding Runtime pipelines for every WAV.
    HRESULT Load(const std::wstring& speechModelDirectory, const std::wstring& languageModelSource,
                 const DeviceArgs& device) noexcept;

    // Transcribes the WAV, formats instruction + transcript as one user
    // message, and streams the language-model response.
    HRESULT Run(const std::wstring& wavPath, const std::wstring& instruction, UINT32 maxNewTokens,
                const std::function<void(const std::wstring&)>& onStatus,
                const std::function<void(const std::wstring&)>& onTranscript,
                const std::function<bool(const wchar_t*)>& onFragment,
                SpeechToLanguageModelResult& result) noexcept;

    WhisperRecognizer* SpeechRecognizer() const noexcept;

private:
    std::unique_ptr<WhisperRecognizer> m_speechRecognizer;
    std::unique_ptr<winmlsamples::language::LanguageModel> m_languageModel;
};
