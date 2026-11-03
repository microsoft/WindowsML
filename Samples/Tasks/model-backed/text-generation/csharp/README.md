<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Text generation from published weights in C#

This is the C# version of the [text generation sample](../README.md). It also
covers the explicit llama.cpp route of the
[llama.cpp sample](../../llama-text-generation/) behind `--llama`, so one
project shows both ways an app can hand a model to a text generation Task.
It prints the same output as the C++ samples.

| Route | How the app loads the model | Target | Options |
|---|---|---|---|
| Model provider (default) | `IWinMLRuntime::LoadModelFromPath` for a checkpoint folder, `LoadModelFromFile` for a GGUF file | GPU, performance preference | `--capacity <tokens>`, `--program-cache <directory>` |
| llama.cpp (`--llama`) | `IWinMLLlamaModelLoader::LoadModelFromFile` for a GGUF file | CPU, or GPU with `--device gpu` | `--draft-model <draft.gguf>`, `--device cpu\|gpu` |

Either way the sample calls `SetModel` without `SetTokenizer`, creates the task
with the configuration's tokenizer (`GetTokenizer`), formats the prompt as one
user turn with the model's chat template, and streams the reply from the pull
stream. It prints the token counts and why generation stopped. With
`--draft-model`, it enables speculative decoding and prints how many draft
tokens the model accepted. Ctrl+C cancels generation through the Task's
cancellation source.

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

## How the bindings are generated

The sample declares no Runtime or Task types of its own. The
`Microsoft.Windows.AI.MachineLearning` package includes Win32 metadata for the
Runtime and Task APIs, and
[CsWin32](https://github.com/microsoft/CsWin32) generates bindings for the APIs
listed in `NativeMethods.txt` when the project builds.

- The project imports
  [`WinMLSamples.CSharp.props`](../../../../Runtime/shared/csharp/WinMLSamples.CSharp.props),
  which the C# samples share, and builds into `Samples\Tasks\out`.
- CsWin32 combines every `NativeMethods.txt`. [This sample's list](NativeMethods.txt)
  adds `WinMLCreateTasks`, the Task interfaces it obtains through
  `QueryInterface` (`IWinMLTextGenerationTaskFactory`,
  `IWinMLModelTextGenerationConfiguration` and the speculative interfaces),
  `IWinMLLlamaModelLoader` and `IWinMLStructuredConversationFormatter`. Task
  APIs are in the `Microsoft.Windows.AI.MachineLearning.Runtime.Tasks`
  namespace and are listed by their full names.
- Generated interface methods throw an exception for a failure `HRESULT` and
  return their `[out, retval]` value. The sample catches the exception and
  checks its `HResult` where the C++ sample checks a returned `HRESULT`.
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
