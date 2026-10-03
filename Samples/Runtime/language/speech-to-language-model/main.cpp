// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Speech to LanguageModel: transcribe a WAV, then ask a language model about it.
//
// Transcribe a WAV file with Whisper, then ask an ONNX/ORT or GGUF language
// model about the transcript and stream its answer.
//
//   SpeechToLanguageModelTemplate::Load loads both models up front with the
//   same -Device and -Ep, each in its own Runtime: WhisperRecognizer uses
//   separate encoder and decoder pipelines with greedy decoding, and
//   LoadLanguageModel builds one "decoder" stage as in hello-language-model.
//   Run transcribes the WAV, sends the instruction, a blank line,
//   "Transcript:", and the transcript as one user message, and streams a
//   greedy reply of up to 256 tokens through the chat template. Only the
//   transcript text passes between the two models.
//
// Run it
//   .\run_speech_to_language_model.ps1 [-Backend ort|llama] [-ModelPath <path>]
//                                       [-WavPath <path>] [-Instruction <text>]
//                                       [-Device cpu|gpu|npu] [-Ep <name>]
//
// Learn more (paths relative to this file)
//   ../../../../docs/Runtime/tutorials/04-speech-to-language.md
//   ../../../../docs/Runtime/tutorials/07-gguf-language-models.md
//   ../../../../docs/api-reference/CommonPatterns.md (patterns 1, 4, and 9)

#include <cstdio>
#include <fcntl.h>
#include <iostream>
#include <io.h>

#include <wil/result.h>

#include "common.h"
#include "language_args.h"
#include "language_console.h"
#include "language_model_loader.h"
#include "speech_to_language_model_template.h"

namespace
{
HRESULT RunSample(int argc, wchar_t** argv) noexcept
try
{
    winmlsamples::language_args::SpeechToLanguageOptions options;
    bool handledHelp = false;
    RETURN_IF_FAILED(winmlsamples::language_args::ParseSpeechToLanguageOptions(argc, argv, options,
                                                                               handledHelp));
    if (handledHelp)
    {
        return S_OK;
    }

    if (options.speechModelDirectory.empty() || options.languageModelSource.empty() ||
        !FileExists(options.wavPath))
    {
        fwprintf(stderr, L"Required assets were not found. Run "
                         L".\\check_artifacts.ps1 -Sample speech-to-language-model first.\n");
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }

    const winmlsamples::language::LanguageArtifactKind languageArtifactKind =
        winmlsamples::language::GetLanguageArtifactKind(options.languageModelSource);
    if (languageArtifactKind == winmlsamples::language::LanguageArtifactKind::Unknown)
    {
        fwprintf(stderr, L"Unsupported language artifact: %s\n",
                 options.languageModelSource.c_str());
        return E_INVALIDARG;
    }

    if (languageArtifactKind == winmlsamples::language::LanguageArtifactKind::Gguf &&
        !options.device.epName.empty())
    {
        fwprintf(stderr, L"--ep applies to ORT and cannot be used with GGUF.\n");
        return E_INVALIDARG;
    }

    if (options.device.deviceType == WINML_EXECUTION_TARGET_KIND_NPU ||
        !options.device.epName.empty())
    {
        RETURN_IF_FAILED(PrepareAndValidateExecutionProviders(
            options.device.deviceType,
            options.device.epName.empty() ? nullptr : options.device.epName.c_str()));
    }

    // The template loads the speech recognizer and unified language model as
    // separate sample helper objects (WhisperRecognizer and LanguageModel), then
    // composes their outputs in Run.
    std::wcout << L"Loading composed speech-to-language template...\n";
    std::wcout << L"Speech backend: ONNX / ORT\n";
    std::wcout << L"Language backend: "
               << winmlsamples::language::LanguageArtifactName(languageArtifactKind) << L"\n";
    SpeechToLanguageModelTemplate taskTemplate;
    RETURN_IF_FAILED(taskTemplate.Load(options.speechModelDirectory, options.languageModelSource,
                                       options.device));

    // Run first transcribes the WAV, then formats instruction + transcript as
    // a language-model user message and streams generated fragments.
    SpeechToLanguageModelResult result;
    const HRESULT runHr = taskTemplate.Run(
        options.wavPath, options.instruction, 0,
        [](const std::wstring& status) {
            std::wcout << L"  " << status << L"\n";
        },
        [](const std::wstring& transcript) {
            std::wcout << L"\nTranscript:\n" << transcript << L"\n\n";
            std::wcout << L"Response:\n" << std::flush;
        },
        winmlsamples::language::StreamFragment, result);
    if (runHr == S_FALSE)
    {
        fwprintf(stderr, L"Language generation exceeded the fixed context capacity.\n");
        return S_FALSE;
    }

    RETURN_IF_FAILED(runHr);
    winmlsamples::language::PrintGenerationStats(result.generation);
    std::wcout << L"\n";
    return S_OK;
}
CATCH_RETURN()
} // namespace

int wmain(int argc, wchar_t** argv)
{
    _setmode(_fileno(stdout), _O_U8TEXT);

    const HRESULT hr = RunSample(argc, argv);
    if (hr == S_FALSE)
    {
        return 2;
    }

    if (FAILED(hr))
    {
        fwprintf(stderr, L"speech-to-language-model failed with HRESULT 0x%08X.\n",
                 static_cast<unsigned int>(hr));
        return 1;
    }

    return 0;
}
