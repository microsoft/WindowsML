<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Text generation from published weights in C#

This is the C# version of the [text generation sample](../README.md). It also
covers the explicit llama.cpp route of the
[llama.cpp sample](../../llama-text-generation/) behind `--llama`, so one
project shows both ways an app can hand a model to a text generation Task.
It prints the same output as the C++ samples.

| Route | How the app loads the model | Target | Options |
|---|---|---|---|
| Model provider (default) | `WinMLRuntime.LoadModelFromPath` for a checkpoint folder, `LoadModelFromFile` for a GGUF file | GPU, performance preference | `--capacity <tokens>`, `--program-cache <directory>` |
| llama.cpp (`--llama`) | `LlamaModelLoader.LoadModelFromFile` for a GGUF file | CPU, or GPU with `--device gpu` | `--draft-model <draft.gguf>`, `--device cpu\|gpu` |

Either way the sample calls `SetModel` without `SetTokenizer`, creates the task
with the configuration's tokenizer (`GetTokenizer`), formats the prompt as one
user turn with the model's chat template, encodes it, and streams the reply
with `await foreach` over `TextGenerator.GenerateTokensAsync`. It prints the
token counts and why generation stopped, then closes the session. With
`--draft-model`, it enables speculative decoding and prints how many draft
tokens the model accepted. Ctrl+C cancels a `CancellationToken`, which cancels
generation through the Task's cancellation source.

```text
Prompt: Write one sentence about the ocean.
Reply: The ocean, a vast and boundless body of water, is a vital ecosystem that ...
Tokens: prompt=36 generated=60
Finished because: end-of-sequence token
```

The sample explains the same errors as the C++ samples: a model that Windows
ML does not support (`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` from
`SetModel`), a package without the llama.cpp loader (`E_NOINTERFACE`), a
missing llama.cpp payload, and a GPU target that llama.cpp cannot use. It also
reports a device without a GPU, where the model provider route cannot run.

## How the sample calls the Runtime

The sample calls the Runtime and Task APIs through the C# sample wrapper in
[`Runtime/shared/csharp/Runtime`](../../../../Runtime/shared/csharp/Runtime/),
which gives their COM-style C API an idiomatic C# surface. The sample code has
no pointers or `HRESULT` checks. The wrapper is sample code, not a supported
API; the [shared README](../../../../Runtime/shared/csharp/README.md) explains
why it exists and what it covers.

- Every object is `IDisposable`. `configuration.As<ModelTextGenerationConfiguration>()`
  stands in for `QueryInterface`, and `runtime.TryAs(out LlamaModelLoader? loader)`
  returns `false` where the C++ sample gets `E_NOINTERFACE`.
- A failure throws a .NET exception that keeps the `HRESULT`: for example,
  `NotSupportedException` for `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. The
  sample catches the exception type where the C++ sample checks a returned
  `HRESULT`.
- A text generation session belongs to the thread that created it.
  `TextGenerator` keeps the session and its pull stream on one dedicated
  thread and exposes the reply as an `IAsyncEnumerable` that accepts a
  `CancellationToken`. `LastResult` holds the result, including the result
  of a canceled generation, and `CloseAsync` closes the session and reports a
  failure to close, as the C++ sample does.
- The project imports
  [`WinMLSamples.CSharp.props`](../../../../Runtime/shared/csharp/WinMLSamples.CSharp.props),
  which the C# samples share and which compiles the wrapper, and builds into
  `Samples\Tasks\out`.
- The project references `Microsoft.Windows.AI.MachineLearning.LibLlama.Core`,
  as the C++ llama.cpp sample does. The package copies the llama.cpp engine
  next to the executable and its CPU backend to `LlamaBackends\cpu`.

## Requirements

- The .NET 8 SDK or later.
- The `Microsoft.Windows.AI.MachineLearning` and
  `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` versions pinned in
  `Samples\Directory.Packages.props`. See
  [Package requirements](../../README.md#package-requirements).
- A GPU for the default route. The `--llama` route runs on the CPU.

## Build and run

From `Samples\Tasks`:

```powershell
.\build.ps1 -Sample model-text-generation-csharp -PackageOnly

.\run_model_text_generation.ps1 -Language csharp
.\run_model_text_generation.ps1 -Language csharp -Format gguf -ProgramCache .\models\program-cache
.\run_llama_text_generation.ps1 -Language csharp
.\run_llama_text_generation.ps1 -Language csharp -DraftModel <draft.gguf>
```

`dotnet build model-backed\text-generation\csharp\task-model-text-generation-csharp.csproj -c Release -p:Platform=x64`
builds the same project. Use `-Platform ARM64` (or `-p:Platform=ARM64`) on an
Arm64 device. The executable is
`out\<platform>\<configuration>\task-model-text-generation-csharp\task-model-text-generation-csharp.exe`:

```text
task-model-text-generation-csharp.exe <checkpoint-folder|model.gguf> [prompt] [max-new-tokens]
    [--capacity <tokens>] [--program-cache <directory>]
task-model-text-generation-csharp.exe --llama <model.gguf> [prompt] [max-new-tokens]
    [--draft-model <draft.gguf>] [--device cpu|gpu]
```
