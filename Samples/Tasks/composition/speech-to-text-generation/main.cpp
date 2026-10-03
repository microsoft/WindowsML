// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Speech to text generation: feed an ASR transcript into text generation.
//
// Transcribe a WAV file with the ASR Task, then ask the Text Generation Task
// about the transcript as one chat turn and stream the reply.
//
//   One Runtime and one CPU target serve both Tasks. The app builds the
//   Whisper pipelines and the language model pipeline, each helper creates
//   its own IWinMLTasks and session, and the sample confirms both sessions
//   report that Runtime. The ASR stream is drained and the transcript
//   printed; then the instruction, a blank line, "Transcript:", and the
//   transcript are formatted with the chat template and passed to
//   GenerateTokens for a greedy, streamed reply. Each operation has its own
//   cancellation source, and the transcript reaches generation only as text.
//
// Run it
//   ..\..\run_speech_to_text_generation.ps1 [-LanguageBackend ort|llama]
//                                             [-ModelDir <dir>] [-WavPath <wav>]
//                                             [-LanguageModelPath <path>]
//                                             [-TokenizerSource <path>]
//                                             [-Instruction <text>]
//                                             [-MaxNewTokens <count>]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Tasks/task-lifecycle.md
//   ../../../../docs/api-reference/IWinMLTasks.md
//   ../../../../docs/api-reference/IWinMLAutomaticSpeechRecognitionTask.md
//   ../../../../docs/api-reference/IWinMLTextGenerationTask.md

#include "speech_recognition_task.h"
#include "text_generation_task.h"

#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>

int wmain(int argumentCount, wchar_t** arguments)
try
{
    using namespace winmlsamples::tasks;

    if (argumentCount < 5 || argumentCount > 8)
    {
        std::fwprintf(stderr, L"Usage: task-speech-to-text-generation.exe "
                              L"<whisper-directory> <input.wav> <model> "
                              L"<tokenizer-source|-> [instruction] [max-new-tokens] "
                              L"[ort|llama language-backend]\n");
        return 2;
    }

    LPCWSTR tokenizerSource = std::wcscmp(arguments[4], L"-") == 0 ? nullptr : arguments[4];
    LPCWSTR instruction =
        argumentCount >= 6 ? arguments[5]
                           : L"What color is the fox in the transcript? Reply with only the color.";
    UINT32 maxNewTokens = 32;
    if (argumentCount >= 7 && !ParseTokenCount(arguments[6], maxNewTokens))
    {
        std::fwprintf(stderr, L"max-new-tokens must be a positive 32-bit integer.\n");
        return 2;
    }

    const std::wstring modelExtension = GetLowercaseExtension(arguments[3]);
    const std::wstring languageBackend =
        argumentCount == 8 ? arguments[7] : (tokenizerSource == nullptr ? L"llama" : L"ort");
    if (languageBackend != L"ort" && languageBackend != L"llama")
    {
        std::fwprintf(stderr, L"language backend must be ort or llama.\n");
        return 2;
    }

    if (languageBackend == L"llama" && modelExtension != L".gguf")
    {
        std::fwprintf(stderr, L"llama requires a GGUF model.\n");
        return 2;
    }

    if (languageBackend == L"ort" && modelExtension != L".onnx" && modelExtension != L".ort")
    {
        std::fwprintf(stderr, L"ort requires an ONNX or ORT model.\n");
        return 2;
    }

    if (languageBackend != L"llama" && tokenizerSource == nullptr)
    {
        std::fwprintf(stderr, L"ORT requires a tokenizer source.\n");
        return 2;
    }

    // One Runtime can own both independent Task compositions.
    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    ComPtr<IWinMLExecutionTarget> target;
    THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(target.GetAddressOf()));

    SpeechRecognitionObjects speech;
    // Build the ASR Task first; it owns the speech session used to produce the
    // transcript string.
    THROW_IF_FAILED(
        BuildWhisperSpeechRecognition(runtime.Get(), target.Get(), arguments[1], speech));
    TextGenerationObjects text;
    // Build the Text Generation Task as a separate session over the same Runtime.
    // This composition sample keeps the default greedy selection; the text
    // generation sample demonstrates the sampling options.
    const SamplingArguments sampling;
    THROW_IF_FAILED(BuildTextGeneration(runtime.Get(), target.Get(), arguments[3], tokenizerSource,
                                        maxNewTokens, sampling, text));
    IWinMLTextGenerationSession* textSession = text.task.session.Get();
    IWinMLTextGenerationOptions* textOptions = text.task.options.Get();
    IWinMLTasks* textTasks = text.task.tasks.Get();
    THROW_IF_FAILED(VerifyExactRuntime(speech.task.session.Get(), runtime.Get()));
    THROW_IF_FAILED(VerifyExactRuntime(textSession, runtime.Get()));

    // The app owns conversion from the WAV file to the Runtime waveform tensor.
    ComPtr<IWinMLTensor> waveform;
    WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA metadata{};
    THROW_IF_FAILED(CreateWaveformTensor(target.Get(), arguments[2], waveform, metadata));

    ComPtr<IWinMLCancellationSource> speechCancellation;
    THROW_IF_FAILED(speech.task.tasks->CreateCancellationSource(speechCancellation.GetAddressOf()));
    SpeechRecognitionResult transcription;
    // Run ASR to completion before constructing the text-generation prompt.
    THROW_IF_FAILED(Transcribe(speech.task.session.Get(), waveform.Get(), metadata,
                               speechCancellation.Get(), false, transcription));
    std::wprintf(L"Transcript: %ls\n", transcription.transcript.c_str());

    // The composition boundary is plain text; the app can inspect or transform
    // the transcript before starting the next Task operation. The request goes
    // to the language model as a chat turn, so an instruct model answers and
    // then ends its turn.
    const std::wstring request =
        std::wstring(instruction) + L"\n\nTranscript:\n" + transcription.transcript;
    std::vector<UINT32> promptTokens;
    THROW_IF_FAILED(EncodeChatPrompt(text.task.tokenizer.Get(), request.c_str(), promptTokens));
    ComPtr<IWinMLCancellationSource> textCancellation;
    THROW_IF_FAILED(textTasks->CreateCancellationSource(textCancellation.GetAddressOf()));
    TextGenerationResult generation;
    // The second pull stream is read independently from the ASR stream.
    std::wprintf(L"Generated response: ");
    THROW_IF_FAILED(GenerateTokens(textSession, textOptions, promptTokens, textCancellation.Get(),
                                   true, generation));
    std::wprintf(L"\n");
    return 0;
}
catch (...)
{
    const HRESULT result = wil::ResultFromCaughtException();
    std::fprintf(stderr, "Task composition failed: 0x%08X\n", result);
    return 1;
}
