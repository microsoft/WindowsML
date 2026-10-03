<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Python samples

The Python samples use `windowsml.runtime`, the Python projection of the same
Runtime API that the C++ samples call. Run commands from `Samples\Runtime`.

## Set up Python

Install `windowsml` with its `with-ort` extra, which adds the matching
`onnxruntime-windowsml` package; mismatched versions can fail when the Runtime
is created. The samples also use NumPy and Pillow.

```powershell
python.exe -m pip install --pre "windowsml[with-ort]" numpy pillow
```

Use the same `python.exe` for setup and for each sample command. To use wheels
that aren't on PyPI, see [Local package override](../../Samples/Runtime/localpackages/README.md#python-wheels).

## Run Python samples

Acquire the artifacts for a sample, then run its Python entry point.

```powershell
.\check_artifacts.ps1 -Sample image-classification
python.exe .\get-started\image-classification\main.py --device cpu

.\check_artifacts.ps1 -Sample super-resolution
python.exe .\vision\super-resolution\main.py --device cpu `
  --output .\super-resolution-output.png

.\check_artifacts.ps1 -Sample whisper
python.exe .\speech\whisper\main.py .\speech\whisper\test_audio.wav `
  --model-dir .\models\whisper-medium-q4f16\onnx --device cpu

.\check_artifacts.ps1 -Sample model-compilation
python.exe .\compile-and-deploy\model-compilation\main.py --device cpu

.\check_artifacts.ps1 -Sample hello-language-model
python.exe .\language\hello-language-model\main.py `
  --backend ort --device cpu --prompt "Name three primary colors."

.\check_artifacts.ps1 -Sample llm-chat
python.exe .\language\llm-chat\main.py `
  --backend ort --mode unified --device cpu `
  --prompt "Name three primary colors."

.\check_artifacts.ps1 -Sample speech-to-language-model
python.exe .\language\speech-to-language-model\main.py `
  --backend ort --device cpu `
  --transcript "The quick brown fox jumps over the lazy dog."
```

Every entry point accepts `--help`.

## Choose a backend and target

For ONNX samples, use the device and provider options described in
[Devices and execution providers](providers.md): `--device`, `--ep`,
`--performance`, `--efficiency`, and `--verbose`.

The language samples add `--backend ort` for ONNX Runtime artifacts and
`--backend llama` for GGUF artifacts. The artifact extension still selects the
Runtime backend; the command-line option chooses which sample artifact path to
load. See [Backends and model formats](backends.md).

## Understand language-model modes

| Mode | Model layout | Backend path | Use |
|---|---|---|---|
| `unified` | One ONNX, `.ort`, or GGUF model | ONNX Runtime for ONNX and `.ort`; llama.cpp for GGUF | One decoder stage and token loop. |
| `split` | `emb.onnx`, `decoder.onnx`, and `head.onnx` | ONNX Runtime | Inspect stage placement, bindings, and state. |

`llm-chat` defaults to `unified`. Request `split` to run the three-stage ONNX
Runtime decode topology:

```powershell
python.exe .\language\llm-chat\main.py `
  --backend ort --mode split --model-dir .\models\llm `
  --prompt "Name three primary colors."
```

## Run GGUF models

For GGUF setup, supported samples, optional modules, shards, and context
capacity, see [GGUF language models](gguf-models.md).

Install the llama.cpp CPU backend with the `windowsml-llama-core` wheel, then
run:

```powershell
python.exe -m pip install --pre windowsml-llama-core
python.exe .\language\hello-language-model\main.py `
  --backend llama --device cpu --prompt "Name three primary colors."
```

## Differences from C++

- The speech-to-language Python sample accepts an existing transcript. Use the
  Whisper Python sample first when the input is a WAV file.
- Model compilation demonstrates file output. The C++ sample also covers compile
  sinks and resource readers.
- Image classification and super-resolution use Pillow to decode images.
  Super-resolution writes PNG output without Media Foundation.

Python keeps each sample short while it uses the same Runtime objects. The C++
samples add the native integrations listed above.
