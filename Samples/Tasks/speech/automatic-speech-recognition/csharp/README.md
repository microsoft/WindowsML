<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Automatic speech recognition in C#

This is the C# version of the
[automatic speech recognition sample](../README.md). It takes the same
arguments, prints the same output, and exits with the same codes as the C++
sample, apart from the executable name in the usage line.

The sample builds single-stage Whisper encoder and decoder pipelines on a CPU
target from the prepared `encoder_model.onnx` and `decoder_model.onnx`. The
encoder stage fixes its symbolic `feature_size` and `encoder_sequence_length`
dimensions (`OnnxSymbolicDimensionOverrides`) and disables two ONNX Runtime
optimizers that the prepared encoder needs disabled (`OrtStageOptions`). The
sample then does what the package's C++ helper
`BuildAutomaticSpeechRecognition` does: it creates the task with the model's
`tokenizer.json`, binds the pipelines with
`EncoderDecoderSpeechRecognitionConfiguration.SetBindings`, validates the
configuration, and creates the session.

It checks that the session belongs to the Runtime the sample created, that the
session is ready, and that it also takes encoder input
(`SpeechRecognitionEncoderInput`), as encoder-decoder sessions do. It reads the
WAV file into 16 kHz mono samples, turns them into a waveform tensor with the
CPU target's `AudioTensorFactory`, and streams the transcript with
`await foreach` over `SpeechRecognizer.TranscribeWaveformAsync`. The streamed
text must match the transcript of the final result, and an error finish fails
the sample. Ctrl+C cancels a `CancellationToken`, which cancels recognition
through the Task's cancellation source; the sample prints what it recognized
until then.

```text
Transcript:  The quick brown fox jumps over the lazy dog.
```

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

- Every object is `IDisposable`. `stage.As<OrtStageOptions>()` and
  `configuration.As<EncoderDecoderSpeechRecognitionConfiguration>()` stand in
  for `QueryInterface`, and `IsSameObject` compares COM identity as the C++
  sample does.
- A failure throws a .NET exception that keeps the `HRESULT`, which the sample
  prints.
- A speech recognition session belongs to the thread that created it.
  `SpeechRecognizer` keeps the session and its pull stream on one dedicated
  thread and exposes the transcript as an `IAsyncEnumerable` that accepts a
  `CancellationToken`. `InvokeAsync` runs the session checks on that thread,
  and `LastOutcome` holds the outcome, including that of a canceled operation.

## Build and run

From `Samples\Tasks`:

```powershell
.\build.ps1 -Sample automatic-speech-recognition-csharp -PackageOnly
.\run_automatic_speech_recognition.ps1 -Language csharp
```

`dotnet build speech\automatic-speech-recognition\csharp\task-automatic-speech-recognition-csharp.csproj -c Release -p:Platform=x64`
builds the same project. The executable is
`out\<platform>\<configuration>\task-automatic-speech-recognition-csharp\task-automatic-speech-recognition-csharp.exe`:

```text
task-automatic-speech-recognition-csharp.exe <model-directory> <input.wav>
```
