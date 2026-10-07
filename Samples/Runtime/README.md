<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Windows ML Runtime samples

The Windows ML Runtime API gives Windows applications one programming model for
local AI on CPUs, GPUs, and NPUs. The same Runtime objects run ONNX models
through ONNX Runtime and GGUF models through llama.cpp. For ONNX models,
hardware acceleration comes from execution providers that Windows installs and
keeps up to date. These samples cover the API in C++ and Python, from a first
image classifier to multi-turn chat and multi-stage pipelines.

> [!NOTE]
> The Runtime and Task APIs are new and still evolving, and they may change.
> Report issues and share feedback through
> [GitHub Issues](https://github.com/microsoft/windowsml/issues).

## Why use the Runtime API

- The model file selects the backend. `.onnx` and `.ort` files run on ONNX
  Runtime, `.gguf` files run on llama.cpp, and the calls that load, place, and
  run a model are the same for both.
- Each pipeline stage requests a CPU, GPU, or NPU execution target, and the
  application can query the hardware that each stage resolved to.
- Pipelines connect models and processors, hand tensors from one stage to the
  next, and keep state, such as a language model's KV cache, between runs.
- Tokenizers, chat templates, and tensor factories for images, audio, and text
  are part of the API.
- A compiled model can be saved and reloaded, so later runs skip compilation.

The [Task API samples](../Tasks/README.md) build on these Runtime objects with
typed tasks for text generation and speech recognition, and the
[Server samples](../Server/README.md) serve a text-generation model to
OpenAI-compatible clients.

The [Runtime API overview](../../docs/Runtime/overview.md) explains how the three fit together
and the design goals behind them.

## Samples

Every sample has a C++ version, and most also have a Python version that uses
`windowsml.runtime`.

| Area | Sample | Languages | What it demonstrates |
|---|---|---|---|
| Get started | [Image classification](get-started/image-classification/) | C++, Python | Turn an image into a tensor, run a one-stage pipeline, and read the top classes. |
| Vision | [Super-resolution](vision/super-resolution/) | C++, Python | Convert a video frame to a tensor, upscale it 2x, and convert the result back. |
| Language | [Hello LanguageModel](language/hello-language-model/) | C++, Python | Load an ONNX or GGUF language model and stream a reply with the same code. |
| Language | [LLM chat](language/llm-chat/) | C++, Python | Add conversation state, generation limits, and a split three-stage ONNX pipeline. |
| Speech | [Whisper](speech/whisper/) | C++, Python | Prepare audio and transcribe it with separate encoder and decoder pipelines. |
| Composition | [Speech to LanguageModel](language/speech-to-language-model/) | C++, Python | Send a Whisper transcript to an ONNX or GGUF language model. |
| Compile and deploy | [Model compilation](compile-and-deploy/model-compilation/) | C++, Python | Compile a model for a target, then reload and run the compiled artifact. |
| Interop | [Managed shared context](interop/managed-shared-context/) | C++ | Run two ONNX Runtime stages in one Runtime-managed provider context. |

## Quick start

Run these commands from `Samples\Runtime` in a PowerShell prompt:

```powershell
.\check_artifacts.ps1 -Sample image-classification
.\build.ps1 -Configuration Release -PackageOnly -Sample image-classification
.\run_image_classification.ps1 -Device cpu
```

`check_artifacts.ps1` never downloads anything. When a file is missing, it
prints the command that acquires it. Review the publisher's terms, run the
command, then run the check again. The build writes the sample to
`out\<platform>\Release\image-classification`.

To run the Python version, set up Python as described in
[Python samples](../../docs/Runtime/python-samples.md), then run:

```powershell
python.exe .\get-started\image-classification\main.py --device cpu
```

To build another sample, pass its folder name to `-Sample`, for example
`-Sample llm-chat`, or omit `-Sample` to build every sample. Every PowerShell
script has built-in help:

```powershell
Get-Help .\run_llm_chat.ps1 -Full
```

## Preview scope

This preview is for trying the shape of the API: how an application loads
models, places stages on hardware, and connects them into pipelines. Keep
these points in mind while you try it:

- **Performance isn't a goal of these samples yet.** The first run on a GPU or
  NPU can include a long model compilation, and the samples don't cache
  compiled models between runs. [Model compilation](compile-and-deploy/model-compilation/)
  shows how to compile once and reload the result.
- **The language model samples target CPU and GPU.** The bundled language model
  export isn't prepared for NPUs, so the language samples refuse `-Device npu`
  unless `-ModelPath` names a model prepared for your NPU.
- **Hardware coverage comes from the execution providers and drivers on the
  device.** Which models run on a GPU or NPU, and how well, depends on them. The
  CPU target runs every sample and is the reference for correct output.

### Known issues

- With some execution providers, llama.cpp GPU modules, and drivers, a sample
  can fail while it builds the pipeline, print incorrect output, or exit with an
  error after it prints its result. Try the CPU target or another provider, and
  report the device, driver, and provider through
  [GitHub Issues](https://github.com/microsoft/windowsml/issues).
- Some GPU providers don't accept the quantized image classification model.
  Depending on the provider, ONNX Runtime runs the model on the CPU instead, or
  the pipeline build fails.
- `-Device gpu` without `-Ep` uses the Runtime's default GPU target, which can
  be WebGPU even when another GPU provider is installed. Use `-Ep` to choose a
  provider.
- `-Diagnostics` and `--verbose` keep detailed logging on while the model runs.
  Use them to check placement, not to measure speed.
- Model compilation's in-memory modes (`--mode sink` and `--mode zerocopy`) can
  fail to reload the compiled model with some providers. `--mode file` works.
- Some providers save a compiled copy of a model next to the model file. The
  copy can be as large as the model, so the models folder must be writable and
  have that space free.

## Learn more

| Topic | Where to read |
|---|---|
| A step-by-step path through the samples | [Tutorials](../../docs/Runtime/tutorials/README.md) |
| Backends, devices, execution providers, and model artifacts | [Runtime documentation](../../docs/Runtime/README.md) |
| Python setup and commands | [Python samples](../../docs/Runtime/python-samples.md) |
| GGUF setup and llama.cpp GPU backends | [GGUF language models](../../docs/Runtime/gguf-models.md) |
| Interfaces, methods, and error codes | [Runtime API reference](../../docs/api-reference/README.md) |

## Package restore

NuGet restore uses nuget.org and the [`localpackages`](localpackages/README.md)
override directory. The package versions the samples use are pinned in
[`Samples\Directory.Packages.props`](../Directory.Packages.props).
