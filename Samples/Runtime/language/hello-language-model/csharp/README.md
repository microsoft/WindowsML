<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Hello LanguageModel in C#

This is the C# version of the [Hello LanguageModel sample](../README.md). It
follows the same steps: load one unified decoder, build a one-stage pipeline,
apply the model's chat template, and stream one greedy reply. It accepts the
same options and prints the same reply as the C++ sample.

[`Program.cs`](Program.cs) parses the options and reports the result.
[`LanguageModel.cs`](LanguageModel.cs) is the C# counterpart of
`shared\language_model_loader.h` and the logits helpers in
`shared\llm_inference.h`:

- `IWinMLRuntime::LoadModelFromFile` selects the backend from the artifact:
  ONNX Runtime for `.onnx` and `.ort` files, llama.cpp for `.gguf` files.
- `IWinMLPipelineBuilder::AddModelStage` places the model on the target that
  `--device` and `--ep` select, and `RequestOutput(0)` keeps the logits.
- Before `Build`, `IWinMLStatefulStageOptions::AddStateTensorPair` declares
  each trailing `past_*` input and `present_*` output of an ONNX decoder whose
  descriptors match, and `SetSequenceCapacityHint` passes `--context` or a
  fixed cache length. The Runtime then carries the KV cache between runs. A
  GGUF stage keeps its own state and declares no pairs.
- `WinMLCreateTokenizerFromFile` creates the tokenizer from the ONNX model's
  directory or from the GGUF file, unless `--tokenizer` overrides it.
  `IWinMLStructuredConversationFormatter` applies the chat template, and
  `--raw` encodes the prompt as plain text instead.
- The loop runs the prompt once, then reads the last logits row through
  `IWinMLRawTensorFactory::CreateTensorFromRegion` on a CPU target, takes the
  argmax, prints the fragment from `IWinMLTokenizerDecoder::DecodeToken`, and
  runs that token. It stops at an end-of-sequence token, 256 new tokens, or
  the sequence capacity that `IWinMLStatefulStage` reports.

## How the bindings are generated

The sample declares no Runtime types of its own. The
`Microsoft.Windows.AI.MachineLearning` package includes Win32 metadata for the
Runtime API, including the tokenizer and conversation APIs in
`WinMLTokenizer.dll`. When a C# project also references
[CsWin32](https://github.com/microsoft/CsWin32), the package passes that
metadata to CsWin32, which generates bindings for the APIs listed in
[`NativeMethods.txt`](NativeMethods.txt) and in the shared
[`..\..\..\shared\csharp\NativeMethods.txt`](../../../shared/csharp/NativeMethods.txt)
when the project builds. Each Runtime API is listed by its full name, and the
interfaces obtained through `QueryInterface` are listed explicitly. Generated
interface methods throw an exception for a failure `HRESULT` and return their
`[out, retval]` value. See the
[image classification C# sample](../../../get-started/image-classification/csharp/README.md)
for more about the shared project settings.

`IWinMLTokenizerMetadata::GetEosTokenIds` and `GetBosTokenId` report a missing
token with `S_FALSE`, which the generated methods discard. The sample reads an
empty EOS array, and an unchanged BOS value, as "not declared".

The project references `Microsoft.Windows.AI.MachineLearning.LibLlama.Core`, as
the C++ sample does, so the llama.cpp engine modules and the `LlamaBackends`
folder are copied next to the executable for GGUF models.

## Requirements

- The .NET 8 SDK or later.
- A `Microsoft.Windows.AI.MachineLearning` package that contains
  `metadata\win32\Microsoft.Windows.AI.MachineLearning.Runtime.winmd`, and the
  matching `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` package. If the
  versions pinned in `Samples\Directory.Packages.props` do not include them,
  place packages that do in
  [`localpackages`](../../../localpackages/README.md) and pass the versions
  with `-RuntimePackageVersion` and `-LibLlamaPackageVersion`.

`build.ps1 -Sample all` builds this sample with the C++ samples, so the .NET
SDK is needed for that as well.

## Build and run

From `Samples\Runtime`, run the GGUF model on llama.cpp:

```powershell
.\check_artifacts.ps1 -Sample llm-chat-gguf
.\build.ps1 -Sample hello-language-model-csharp -Configuration Release `
  -Platform x64 -PackageOnly

.\run_hello_language_model.ps1 -Language csharp -Backend llama -Device cpu
```

Run the exported ONNX decoder on ONNX Runtime:

```powershell
.\check_artifacts.ps1 -Sample hello-language-model
.\run_hello_language_model.ps1 -Language csharp -Backend ort
```

The run script passes `-ModelPath`, `-TokenizerSource`, `-Prompt`, `-Raw`,
`-Device`, and `-Ep` to the executable as `--model`, `--tokenizer`,
`--prompt`, `--raw`, `--device`, and `--ep`. The executable also accepts
`--context <tokens>` for a sequence-capacity hint,
`--performance`/`--efficiency` for adapter selection, and `--verbose` to show
ONNX Runtime node placement and the stage's execution diagnostics:

```powershell
.\out\x64\Release\hello-language-model-csharp\hello-language-model-csharp.exe `
  --model .\models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf `
  --prompt "Explain why the sky is blue." --verbose
```

`--ep` pins an ONNX Runtime provider and cannot be used with a GGUF model.
GGUF models run on the CPU or a GPU, not an NPU.

Use `-Platform ARM64` on an Arm64 device. The platform selects the .NET runtime
identifier (`win-x64` or `win-arm64`) and therefore which native Windows ML
DLLs are copied next to the sample, so it must match the architecture the
process runs as.
