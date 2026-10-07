// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Automatic speech recognition: transcribe a WAV file with a Whisper Task.
//
// Transcribe a 16 kHz WAV file with Whisper and print the transcript as it is
// produced.
//
//   The app owns placement. It keeps the decoded PCM on a CPU target and
//   builds single-stage encoder and decoder pipelines on that same target.
//   BuildAutomaticSpeechRecognition hands the pipelines and tokenizer to the
//   Task, which runs the decode loop. The target's IWinMLAudioTensorFactory
//   turns the PCM into a Whisper waveform tensor.
//   TranscribeWaveform takes it with a cancellation source, and ReadNext
//   yields transcript updates until completion. The streamed text must match
//   the transcript from GetResult, and an error finish fails the sample.
//
// Run it
//   ..\..\run_automatic_speech_recognition.ps1 [-ModelDir <dir>] [-WavPath <wav>]
//                                                [-Platform ARM64|x64]
//                                                [-Configuration Debug|Release]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Tasks/task-lifecycle.md
//   ../../../../docs/api-reference/IWinMLTasks.md
//   ../../../../docs/api-reference/IWinMLAutomaticSpeechRecognitionTask.md

#include "speech_recognition_task.h"

#include <cstdio>

int wmain(int argumentCount, wchar_t** arguments)
try
{
    // ONNX Runtime builds the two Whisper pipelines directly from the prepared
    // encoder_model.onnx and decoder_model.onnx files.
    if (argumentCount != 3)
    {
        std::fwprintf(stderr, L"Usage: task-automatic-speech-recognition.exe "
                              L"<model-directory> <input.wav>\n");
        return 2;
    }

    using namespace winmlsamples::tasks;
    // The Runtime creates the targets and pipelines that the ASR Task composes.
    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    // WAV decoding produces host-resident PCM. Keep that application input on
    // CPU.
    ComPtr<IWinMLExecutionTarget> waveformTarget;
    THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(waveformTarget.GetAddressOf()));

    SpeechRecognitionObjects objects;
    // BuildWhisperSpeechRecognition loads encoder_model.onnx,
    // decoder_model.onnx, and tokenizer.json, then validates the typed Task
    // configuration over those caller-owned Runtime objects.
    THROW_IF_FAILED(
        BuildWhisperSpeechRecognition(runtime.Get(), waveformTarget.Get(), arguments[1], objects));
    THROW_IF_FAILED(VerifyExactRuntime(objects.task.session.Get(), runtime.Get()));

    WINML_AUTOMATIC_SPEECH_RECOGNITION_SESSION_STATE state{};
    THROW_IF_FAILED(objects.task.session->GetState(&state));
    THROW_HR_IF(E_UNEXPECTED, state != WINML_AUTOMATIC_SPEECH_RECOGNITION_SESSION_STATE_READY);

    // The Task receives both the waveform tensor and metadata describing the
    // valid sample span.
    ComPtr<IWinMLTensor> waveform;
    WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA metadata{};
    THROW_IF_FAILED(CreateWaveformTensor(waveformTarget.Get(), arguments[2], waveform, metadata));

    // Cancellation is passed into the operation; stream closure is still handled
    // separately after the final result is read.
    ComPtr<IWinMLCancellationSource> cancellation;
    THROW_IF_FAILED(objects.task.tasks->CreateCancellationSource(cancellation.GetAddressOf()));
    SpeechRecognitionResult transcription;
    // Transcribe drains the pull stream and compares the streamed text with the
    // terminal result's transcript.
    std::wprintf(L"Transcript: ");
    THROW_IF_FAILED(Transcribe(objects.task.session.Get(), waveform.Get(), metadata,
                               cancellation.Get(), true, transcription));
    std::wprintf(L"\n");
    return 0;
}
catch (...)
{
    const HRESULT result = wil::ResultFromCaughtException();
    std::fprintf(stderr, "Speech recognition failed: 0x%08X\n", result);
    return 1;
}
