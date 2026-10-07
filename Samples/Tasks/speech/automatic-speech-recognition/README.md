<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Automatic speech recognition

This sample builds Whisper encoder and decoder pipelines and binds them to the
Automatic Speech Recognition Task.

It converts a PCM WAV file into a waveform tensor, verifies the session is
ready, reads the stream, and compares the streamed transcript with the final
result. WAV decoding produces host memory, so the sample keeps that input on
CPU.

The sample loads `encoder_model.onnx` and `decoder_model.onnx` through ONNX
Runtime.

```powershell
..\..\build.ps1 -Sample automatic-speech-recognition -PackageOnly
..\..\run_automatic_speech_recognition.ps1
```

The input tensor contains mono floating-point samples plus sample-rate and
valid-length metadata. Transcription is a pull stream just like text
generation: updates are incremental, while the final result contains the
complete transcript, finish reason, and error code. See
[Task and Runtime lifecycle](../../../../docs/Tasks/task-lifecycle.md) and
[`IWinMLAutomaticSpeechRecognitionTask`](../../../../docs/api-reference/IWinMLAutomaticSpeechRecognitionTask.md).
