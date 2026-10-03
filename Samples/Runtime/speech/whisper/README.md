<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Whisper speech-to-text

This sample transcribes audio locally with the Whisper-medium Q4F16
encoder and decoder. It demonstrates audio preprocessing, two independently
built pipelines, tensor handoff between those pipelines, and an autoregressive
decoder loop.

## Run the reference WAV track

From `Samples\Runtime`:

```powershell
.\check_artifacts.ps1 -Sample whisper
.\build.ps1 -Configuration Release -PackageOnly

.\run_whisper.ps1 -Device cpu
```

The Python sample is console-only and takes its model directory explicitly.
After [setting up Python](../../../../docs/Runtime/python-samples.md#set-up-python), run:

```powershell
python.exe .\speech\whisper\main.py .\speech\whisper\test_audio.wav `
  --model-dir .\models\whisper-medium-q4f16\onnx --device cpu
```

The Python sample uses the Runtime PCM tensorization projection for format
conversion and channel mixing, NumPy for Whisper's log-mel transform, and the
same encoder/decoder ordinal binding and greedy token loop as C++. Both readers
load at most the source frames needed for Whisper's supported 30-second window.

The included WAV file says, "The quick brown fox jumps over the lazy dog."

After the pinned ONNX-community download is verified, acquisition fixes the
model's symbolic dimensions to the sample's tensor shapes:
encoder `[1,80,3000]`, encoder state `[1,1500,1024]`, and decoder token capacity
128. The Runtime consumes the prepared encoder and decoder files.
Preparation uses an existing Python environment with `onnx==1.22.0`, or creates
a local tool environment for that dependency.

Use the [provider guide](../../../../docs/Runtime/providers.md) to select another available
device/provider combination. `run_whisper.ps1` accepts the equivalent `-Device`,
`-Ep`, `-Performance`, and `-Efficiency` parameters.

## Run the interactive microphone track

Launch the executable without a WAV path:

```powershell
$platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }
& ".\out\$platform\Release\whisper-speech-to-text\whisper-speech-to-text.exe" --device cpu
```

The Win32 UI can record, play, and transcribe up to five seconds of audio.

## API path

1. Convert PCM audio to a log-mel spectrogram.
2. Run the encoder pipeline.
3. Bind the encoder output to the decoder pipeline.
4. Rebind token inputs and run one decoder step at a time.
5. Materialize and read back only the current `[1,1,vocab]` logits region.
6. Decode byte-level GPT-2 tokens with incremental UTF-8 handling.
7. Report whether decoding reached EOT or was truncated by a token/capacity
   limit.

For the composition that feeds this transcript to a language model, see
[Tutorial 4: compose speech and language](../../../../docs/Runtime/tutorials/04-speech-to-language.md).
