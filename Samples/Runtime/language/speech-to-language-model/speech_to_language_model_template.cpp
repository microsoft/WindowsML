// Copyright (C) Microsoft Corporation. All rights reserved.

#include "speech_to_language_model_template.h"

#include <filesystem>
#include <utility>

#include <wil/result.h>
#include <wrl/client.h>

#include "language_model_loader.h"

SpeechToLanguageModelTemplate::SpeechToLanguageModelTemplate() = default;
SpeechToLanguageModelTemplate::~SpeechToLanguageModelTemplate() = default;

HRESULT SpeechToLanguageModelTemplate::Load(const std::wstring& speechModelDirectory,
                                            const std::wstring& languageModelSource,
                                            const DeviceArgs& device) noexcept
try
{
    m_speechRecognizer.reset();
    m_languageModel.reset();

    // WhisperRecognizer::Load builds the speech encoder and decoder pipelines;
    // LoadLanguageModel builds the unified language pipeline.
    auto speechRecognizer = std::make_unique<WhisperRecognizer>();
    RETURN_IF_FAILED(speechRecognizer->Load(speechModelDirectory, device));

    std::unique_ptr<winmlsamples::language::LanguageModel> languageModel;
    RETURN_IF_FAILED(winmlsamples::language::LoadLanguageModel(languageModelSource.c_str(), nullptr,
                                                               device, 0, languageModel));
    RETURN_HR_IF_NULL(E_UNEXPECTED, languageModel);

    m_speechRecognizer = std::move(speechRecognizer);
    m_languageModel = std::move(languageModel);
    return S_OK;
}
CATCH_RETURN()

HRESULT SpeechToLanguageModelTemplate::Run(
    const std::wstring& wavPath, const std::wstring& instruction, UINT32 maxNewTokens,
    const std::function<void(const std::wstring&)>& onStatus,
    const std::function<void(const std::wstring&)>& onTranscript,
    const std::function<bool(const wchar_t*)>& onFragment,
    SpeechToLanguageModelResult& result) noexcept
try
{
    result = {};

    if (m_speechRecognizer == nullptr || m_languageModel == nullptr)
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
    }

    // First task result: speech Runtime pipeline -> transcript text.
    RETURN_IF_FAILED(m_speechRecognizer->TranscribeWav(wavPath, result.transcriptUtf8,
                                                       result.transcript, onStatus));
    if (result.transcript.empty())
    {
        return HRESULT_FROM_WIN32(ERROR_NO_DATA);
    }

    if (onTranscript)
    {
        onTranscript(result.transcript);
    }

    // Second task input: keep transcript as prompt content, then let the
    // language helper apply the tokenizer's conversation template.
    const std::wstring prompt = instruction + L"\n\nTranscript:\n" + result.transcript;
    const WINML_CONVERSATION_MESSAGE message = {L"user", prompt.c_str()};
    result.generation =
        m_languageModel->GenerateConversation(&message, 1, maxNewTokens, onFragment);
    return result.generation.hr;
}
CATCH_RETURN()

WhisperRecognizer* SpeechToLanguageModelTemplate::SpeechRecognizer() const noexcept
{
    return m_speechRecognizer.get();
}
