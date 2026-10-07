<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Speech to LanguageModel

This sample transcribes a WAV file with Whisper-medium Q4F16 ONNX encoder and
decoder stages, then sends the transcript to an ONNX/ORT or GGUF language model.

Key files:

- [`speech_to_language_model_template.h`](speech_to_language_model_template.h) defines the composed sample interface;
- [`speech_to_language_model_template.cpp`](speech_to_language_model_template.cpp) runs speech recognition and language generation;
- [`whisper_recognizer.cpp`](whisper_recognizer.cpp) implements WAV loading, speech preprocessing, and Whisper encoder/decoder execution.

## Prerequisites

- The Microsoft.Windows.AI.MachineLearning NuGet package configured by
  `Directory.Packages.props`.
- Prepared Whisper-medium Q4F16 assets containing `encoder_model.onnx`,
  `decoder_model.onnx`, and `vocab.json`.
- A unified ONNX/ORT model with tokenizer assets, or a GGUF model with embedded
  or sidecar tokenizer metadata.
- A 16-bit PCM or 32-bit float WAV file. The sample converts supported input
  sample rates to Whisper's required 16 kHz mono format.
- Only the first 30 seconds are transcribed.

## Prepare, build, and run

```powershell
cd Samples\Runtime
.\check_artifacts.ps1 -Sample speech-to-language-model
.\build.ps1 -Configuration Release -PackageOnly `
  -Sample speech-to-language-model
.\run_speech_to_language_model.ps1
```

Run the GGUF language model with the ONNX Whisper stages:

```powershell
.\check_artifacts.ps1 -Sample llm-chat-gguf
.\run_speech_to_language_model.ps1 -Backend llama
```

Use `-ModelPath <path>` to select an ONNX/ORT or GGUF language model.

The no-argument path uses the acquired models and the Whisper reference WAV. The
default language instruction is `What color is the fox in the transcript? Reply
with only the color.` To supply all paths:

```powershell
.\run_speech_to_language_model.ps1 `
  -WavPath D:\path\to\speech.wav `
  -ModelPath .\models\llm\model.onnx `
  -Instruction "Summarize this transcript in one sentence."
```

The transcript is inserted into an LLM user message. Treat speech as untrusted
prompt content in an application that grants tools, data access, or other
capabilities.

The sample uses a direct DFT and fixed-shape greedy Whisper decode to focus on
Runtime composition rather than production speech-recognition features.

See [Tutorial 4: compose speech and language](../../../../docs/Runtime/tutorials/04-speech-to-language.md).

## Python

[`main.py`](main.py) passes a caller-provided transcript to an ONNX/ORT or GGUF
language model.

```powershell
python.exe .\language\speech-to-language-model\main.py `
  --transcript "The quick brown fox jumps over the lazy dog."
```

Use the Whisper sample first when the input is a WAV file. With a transcript,
Python can run an ONNX/ORT or GGUF model. For GGUF setup, see
[Python payload install](../../../../docs/Runtime/gguf-models.md#python-payload-install).
