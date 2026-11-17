<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Speech recognition from published weights in C#

This is the C# version of the
[speech recognition sample](../README.md). It takes the same arguments, prints
the same output, and exits with the same codes as the C++ sample, apart from
the executable name in the usage line and the C#-only `--live` option.

> [!NOTE]
> This route needs a GPU target, as the C++ sample does.

The sample loads a speech model folder with `WinMLRuntime.LoadModelFromPath`,
creates the task with the model's tokenizer, binds the model on a GPU target
with `ModelSpeechRecognitionConfiguration.SetModel`, sets the language (the
model's default when you omit it) and the mode, and validates the configuration. It reads the WAV file
into 16 kHz mono samples on the CPU, turns them into a waveform tensor with the
CPU target's `AudioTensorFactory`, and streams the transcript with
`await foreach` over `SpeechRecognizer.TranscribeWaveformAsync`. The streamed
text must match the transcript of the final result, and an error finish fails
the sample. It prints why recognition finished, then closes the session. Ctrl+C
cancels a `CancellationToken`, which cancels recognition through the Task's
cancellation source.

```text
Transcript: The quick brown fox jumps over the lazy dog.
Finished because: input complete
```

The sample explains the same errors as the C++ sample: a WAV file longer than
30 seconds (exit code 2), a model that Windows ML cannot run for speech
recognition, and `--mode translate`, which models-backed sessions do not
support in Windows ML 2.9 (`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`). On a
device without a GPU, creating the GPU target fails with
`HRESULT_FROM_WIN32(ERROR_NOT_FOUND)`:

```text
Speech recognition failed: 0x80070490
```

## Live input (C# only)

`--live` writes the same waveform to the session as live input instead of
transcribing it in one call, to show the live input interfaces. When the
session exposes `SpeechRecognitionLiveSession`, the sample calls
`BeginLiveInput` with waveform metadata whose valid sample count is the length
of the audio it will write (a count of zero fails with `E_INVALIDARG`), then
writes one-second chunks, each with its own valid sample count, with
`SpeechRecognitionLiveInput.WriteChunk`. When the session holds the maximum
number of pending chunks, `WriteChunk` does not accept the chunk; the sample
reads the stream and writes the chunk again. It writes the next chunk when the
stream reports `NeedInput`, keeps each chunk until the session's lifetime
watermark passes it, and calls `CompleteInput` after the last chunk. The output
is the same as without `--live`. A model that does not accept live input fails
with:

```text
This speech model does not accept live input (E_NOINTERFACE). Run without --live to transcribe the whole waveform.
```

> [!NOTE]
> The live input route needs a GPU and a model that accepts live input.

## How the sample calls the Runtime

The sample calls the Runtime and Task APIs through the C# sample wrapper in
[`Runtime/shared/csharp/Runtime`](../../../../Runtime/shared/csharp/Runtime/),
which gives their COM-style C API an idiomatic C# surface, and reads the WAV
file with `WhisperAudio` in
[`Runtime/shared/csharp`](../../../../Runtime/shared/csharp/WhisperAudio.cs),
which matches the C++ samples' `LoadWavFile` and the package's
`CreateWhisperWaveformTensorFromPcm`. The wrapper is sample code, not a
supported API; the [shared README](../../../../Runtime/shared/csharp/README.md)
explains why it exists and what it covers.

- Every object is `IDisposable`. `configuration.As<ModelSpeechRecognitionConfiguration>()`
  stands in for `QueryInterface`, and `session.TryAs(out SpeechRecognitionLiveSession? live)`
  returns `false` where C++ gets `E_NOINTERFACE`.
- A failure throws a .NET exception that keeps the `HRESULT`: for example,
  `NotSupportedException` for `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. The
  sample catches the exception type where the C++ sample checks a returned
  `HRESULT`.
- A speech recognition session belongs to the thread that created it.
  `SpeechRecognizer` keeps the session and its pull stream on one dedicated
  thread and exposes the transcript as an `IAsyncEnumerable` that accepts a
  `CancellationToken`. `LastOutcome` holds the outcome, including that of a
  canceled operation. `InvokeAsync` runs the live input loop on the session's
  thread, and `CloseAsync` closes the session and reports a failure to close,
  as the C++ sample does.

## Requirements

- The .NET 8 SDK or later.
- The `Microsoft.Windows.AI.MachineLearning` version pinned in
  `Samples\Directory.Packages.props`. See
  [Package requirements](../../README.md#package-requirements).
- A GPU.

## Build and run

From `Samples\Tasks`:

```powershell
.\build.ps1 -Sample model-speech-recognition-csharp -PackageOnly

.\run_model_speech_recognition.ps1 -SampleLanguage csharp
.\run_model_speech_recognition.ps1 -SampleLanguage csharp -AudioPath <input.wav> -Language en
.\run_model_speech_recognition.ps1 -SampleLanguage csharp -Live
```

The script's `-Language` is the spoken language, so `-SampleLanguage` selects
the C# sample.
`dotnet build model-backed\automatic-speech-recognition\csharp\task-model-speech-recognition-csharp.csproj -c Release -p:Platform=x64`
builds the same project. Use `-Platform ARM64` (or `-p:Platform=ARM64`) on an
Arm64 device. The executable is
`out\<platform>\<configuration>\task-model-speech-recognition-csharp\task-model-speech-recognition-csharp.exe`:

```text
task-model-speech-recognition-csharp.exe <model-directory> <tokenizer.json> <input.wav>
    [--language <tag>] [--mode transcribe|translate] [--live]
```
