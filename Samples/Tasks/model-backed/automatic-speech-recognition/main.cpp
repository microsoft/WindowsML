// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Speech recognition from published weights: bind a speech model folder to the
// speech recognition task and stream a transcript of a WAV file. The app
// passes the model's tokenizer explicitly.

#include "model_task.h"
#include "speech_recognition_task.h"

#include <string>

namespace
{

LPCWSTR DescribeFinishReason(WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON reason) noexcept
{
    switch (reason)
    {
    case WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON_INPUT_COMPLETE:
        return L"input complete";
    case WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON_CAPACITY:
        return L"reached the model's capacity";
    case WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON_CANCELED:
        return L"canceled";
    default:
        return L"error";
    }
}

} // namespace

int wmain(int argumentCount, wchar_t** arguments)
try
{
    using namespace winmlsamples::tasks;

    LPCWSTR positional[3]{};
    LPCWSTR language = nullptr;
    bool translate = false;
    int positionalCount = 0;
    bool validArguments = true;

    for (int index = 1; index < argumentCount && validArguments; ++index)
    {
        const std::wstring argument = arguments[index];
        if (argument == L"--language" && index + 1 < argumentCount)
        {
            language = arguments[++index];
        }
        else if (argument == L"--mode" && index + 1 < argumentCount)
        {
            const std::wstring mode = arguments[++index];
            translate = mode == L"translate";
            validArguments = translate || mode == L"transcribe";
        }
        else if (argument.starts_with(L"--") || positionalCount == ARRAYSIZE(positional))
        {
            validArguments = false;
        }
        else
        {
            positional[positionalCount++] = arguments[index];
        }
    }

    if (!validArguments || positionalCount != ARRAYSIZE(positional))
    {
        std::fwprintf(stderr, L"Usage: task-model-speech-recognition.exe <model-directory> "
                              L"<tokenizer.json> <input.wav> [--language <tag>] "
                              L"[--mode transcribe|translate]\n");
        return 2;
    }

    LPCWSTR modelDirectory = positional[0];
    LPCWSTR tokenizerPath = positional[1];
    WavData audio;
    THROW_IF_FAILED(LoadWavFile(positional[2], audio));

    if (audio.truncated)
    {
        std::fwprintf(stderr, L"Input audio exceeds the sample's 30-second WAV limit. "
                              L"Split it into clips of at most 30 seconds; no transcript "
                              L"was produced.\n");
        return 2;
    }

    THROW_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                audio.samples.empty() || audio.sampleRate != kWhisperSampleRate);

    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    ComPtr<IWinMLExecutionTarget> target;
    THROW_IF_FAILED(runtime->CreateExecutionTarget(WINML_EXECUTION_TARGET_KIND_GPU,
                                                   WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE,
                                                   target.GetAddressOf()));
    ComPtr<IWinMLModel> model;
    THROW_IF_FAILED(runtime->LoadModelFromPath(modelDirectory, nullptr, model.GetAddressOf()));
    ComPtr<IWinMLTokenizer> tokenizer;
    THROW_IF_FAILED(WinMLCreateTokenizerFromFile(tokenizerPath, tokenizer.GetAddressOf()));
    ComPtr<IWinMLTasks> tasks;
    THROW_IF_FAILED(WinMLCreateTasks(runtime.Get(), IID_PPV_ARGS(tasks.GetAddressOf())));
    ComPtr<IWinMLAutomaticSpeechRecognitionTaskFactory> factory;
    THROW_IF_FAILED(tasks.As(&factory));
    ComPtr<IWinMLAutomaticSpeechRecognitionTask> task;
    THROW_IF_FAILED(
        factory->CreateAutomaticSpeechRecognitionTask(tokenizer.Get(), task.GetAddressOf()));
    ComPtr<IWinMLAutomaticSpeechRecognitionConfiguration> configuration;
    THROW_IF_FAILED(task->CreateConfiguration(configuration.GetAddressOf()));
    ComPtr<IWinMLModelAutomaticSpeechRecognitionConfiguration> modelConfiguration;
    THROW_IF_FAILED(configuration.As(&modelConfiguration));

    const HRESULT bound = modelConfiguration->SetModel(target.Get(), model.Get(), nullptr);
    if (IsNotSupported(bound))
    {
        std::fwprintf(stderr, L"Windows ML cannot run this model for speech recognition "
                              L"(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). Use a supported "
                              L"speech model folder, such as openai/whisper-tiny.\n");
        return 1;
    }

    THROW_IF_FAILED(bound);

    // Without a language, the model's default applies.
    THROW_IF_FAILED(configuration->SetLanguage(language));
    THROW_IF_FAILED(
        configuration->SetMode(translate ? WINML_AUTOMATIC_SPEECH_RECOGNITION_MODE_TRANSLATE
                                         : WINML_AUTOMATIC_SPEECH_RECOGNITION_MODE_TRANSCRIBE));
    THROW_IF_FAILED(configuration->Validate());
    ComPtr<IWinMLAutomaticSpeechRecognitionSession> session;
    const HRESULT created = task->CreateSession(configuration.Get(), session.GetAddressOf());
    if (translate && IsNotSupported(created))
    {
        std::fwprintf(stderr, L"Models-backed speech recognition transcribes only; this Windows ML "
                              L"release doesn't translate with a model-backed configuration "
                              L"(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)).\n");
        return 1;
    }

    THROW_IF_FAILED(created);
    StreamCloser sessionCloser(session.Get());

    // WAV parsing stays outside the task. Its input is host-resident PCM.
    ComPtr<IWinMLExecutionTarget> waveformTarget;
    THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(waveformTarget.GetAddressOf()));
    ComPtr<IWinMLTensor> waveform;
    WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA metadata{};
    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
    format.nChannels = 1;
    format.nSamplesPerSec = audio.sampleRate;
    format.wBitsPerSample = sizeof(float) * 8;
    format.nBlockAlign = sizeof(float);
    format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
    ComPtr<IWinMLAudioTensorFactory> audioFactory;
    THROW_IF_FAILED(waveformTarget.As(&audioFactory));
    THROW_IF_FAILED(winml::tasks::CreateWhisperWaveformTensorFromPcm(
        audioFactory.Get(), reinterpret_cast<const BYTE*>(audio.samples.data()),
        audio.samples.size() * sizeof(float), &format, waveform.GetAddressOf(), &metadata));
    ComPtr<IWinMLCancellationSource> cancellation;
    THROW_IF_FAILED(tasks->CreateCancellationSource(cancellation.GetAddressOf()));
    SpeechRecognitionResult result;
    std::wprintf(L"Transcript: ");
    THROW_IF_FAILED(
        Transcribe(session.Get(), waveform.Get(), metadata, cancellation.Get(), true, result));
    std::wprintf(L"\nFinished because: %ls\n", DescribeFinishReason(result.finishReason));
    THROW_IF_FAILED(session->Close());
    return 0;
}
catch (...)
{
    winmlsamples::tasks::ReportFailure(L"Speech recognition", wil::ResultFromCaughtException());
    return 1;
}
