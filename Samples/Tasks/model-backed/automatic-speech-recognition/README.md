<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Speech recognition from published weights

Load a speech model folder, bind it to the speech recognition task with
`SetModel` on a GPU execution target, and stream a transcript of a WAV file.
The app passes the model's tokenizer explicitly, and the WAV samples stay in
host memory on a CPU target; the task moves them to the GPU.

The sample sets the language and the mode when you pass them. Language support
depends on the model. Models-backed sessions transcribe only in Windows ML 2.9,
so `--mode translate` fails `CreateSession` with
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` and the sample says so.

```text
Transcript: The quick brown fox jumps over the lazy dog.
Finished because: input complete
```

The C++ sample reads clips of at most 30 seconds.

```powershell
..\..\run_model_speech_recognition.ps1
..\..\run_model_speech_recognition.ps1 -AudioPath <input.wav> -Language en
python.exe .\main.py ..\..\models\whisper-tiny ..\..\..\Runtime\speech\whisper\test_audio.wav
```

See [Models-backed Tasks](../README.md) for package and model requirements.
