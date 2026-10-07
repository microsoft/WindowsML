<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Runtime sample tutorials

These tutorials form a step-by-step learning path. They use the same sample
projects in the solution, but explain what to observe and which source files to
read after each successful run.

| Order | Tutorial | What you learn |
|---:|---|---|
| 1 | [First inference](01-first-inference.md) | Runtime, model, target, stage, tensor binding, and output |
| 2 | [Tensors and media](02-tensors-and-media.md) | WIC, Media Foundation, image layouts, and device boundaries |
| 3 | [Language models](03-language-models.md) | LanguageModel helper, Runtime construction, tokenizer/state, and ONNX Runtime pipelines |
| 4 | [Speech to LanguageModel](04-speech-to-language.md) | Compose two Runtime pipelines |
| 5 | [Accelerators and providers](05-accelerators.md) | Device requests, provider pinning, diagnostics, and output checks |
| 6 | [Compile and deploy](06-compile-and-deploy.md) | Compile artifacts, reload them, and manage resources |
| 7 | [GGUF language models](07-gguf-language-models.md) | Reuse LanguageModel with the llama.cpp backend and hybrid composition |

## Before you start

1. Install Visual Studio with **Desktop development with C++**.
2. Open a PowerShell prompt in `Samples\Runtime`.
3. Build and run scripts target this PC's architecture (x64 or ARM64).
4. Start every new scenario on CPU.

Build the full solution. Builds never download models:

```powershell
.\build.ps1 -Configuration Release -PackageOnly
```

Each tutorial then acquires only the assets it needs.

Use PowerShell help at any point to inspect accepted parameters and examples:

```powershell
Get-Help .\check_artifacts.ps1 -Full
Get-Help .\run_super_resolution.ps1 -Examples
```

## How to read the samples

The sample code uses three levels of abstraction:

1. Sample helpers that wrap a common flow, for example `WinML::LanguageModel`.
2. Runtime composition: model stages, processors, connections, and state.
3. Tensor/platform integration: WIC, Media Foundation, CPU locks, and
   execution targets.

Use the tutorials in order to see the same Runtime concepts recur at increasing
levels of complexity.

When you need method, parameter, ownership, or error details behind a tutorial,
use the local [Runtime API reference](../../api-reference/README.md).

## Run the samples

Every run script describes its sample, checks the sample's artifacts, and waits
for Enter before it starts. When something is missing, it stops and prints how
to acquire it. To check every sample's artifacts at once:

```powershell
.\check_artifacts.ps1
```

Vision and compilation scenarios:

```powershell
.\run_image_classification.ps1
.\run_super_resolution.ps1
.\run_model_compilation.ps1
```

Speech and language scenarios:

```powershell
.\run_whisper.ps1
.\run_hello_language_model.ps1
.\run_llm_chat.ps1
.\run_speech_to_language_model.ps1
```

Each run script takes `-Device`. The language run scripts also take `-Backend`
to choose between the ONNX and GGUF artifact paths.
