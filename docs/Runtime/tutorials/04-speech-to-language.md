<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Tutorial 4: compose speech and language

This tutorial combines two Runtime pipelines:

```text
WAV -> Whisper transcript -> language prompt -> streamed response
```

## 1. Prepare all assets

```powershell
.\check_artifacts.ps1 -Sample speech-to-language-model
```

If files are missing, the script prints the commands that acquire them. Review
the publisher's terms, run the commands, then run the check again. See
[Models and artifacts](../artifacts.md) for details.

This prepares both Whisper and the unified ONNX decoder.

## 2. Build

```powershell
.\build.ps1 -Configuration Release -PackageOnly `
  -Sample speech-to-language-model
```

## 3. Run the reference track

```powershell
.\run_speech_to_language_model.ps1
```

The reference track uses ONNX Runtime for Whisper and the unified ONNX
responder. Use `-Backend llama` for ONNX Runtime Whisper plus GGUF.

Expected transcript:

```text
The quick brown fox jumps over the lazy dog.
```

The expected response is `brown`. The default instruction asks Qwen what color
the fox in the transcript is, which keeps the language-model step distinct from
Whisper transcription.

## 4. Use your own WAV

```powershell
.\run_speech_to_language_model.ps1 `
  -WavPath D:\audio\meeting.wav `
  -Instruction "Summarize this transcript in one sentence."
```

The recognizer accepts PCM or float WAV data and resamples to Whisper's 16 kHz
mono requirement. Only the bounded sample window is transcribed.

## 5. Trace the composition

Read:

- `language\speech-to-language-model\speech_to_language_model_template.cpp`;
- `language\speech-to-language-model\whisper_recognizer.cpp`;
- `shared\language_model_loader.h`.

The composition uses existing Runtime pipelines and the language session.

The Python sample composes the same two results: run
`speech\whisper\main.py` for the transcript, then pass it to the language half.
The transcript is a required argument:

```powershell
python.exe .\language\speech-to-language-model\main.py `
  --transcript "The quick brown fox jumps over the lazy dog."
```

## 6. Security boundary

Treat the transcript as untrusted prompt content. If an application later
grants tools, files, network access, or other capabilities, speech text must
not bypass normal prompt and authorization policy.

## Next

Continue to [Accelerators and providers](05-accelerators.md).
