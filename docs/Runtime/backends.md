<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Backends and model formats

`LoadModelFromFile` recognizes the artifact from its file type. In a pipeline
that the application builds, the file type selects the Runtime backend, and the
application still uses the same Runtime objects: model, target, pipeline
builder, stages, bindings, `Run`, and output tensors.

| Artifact | Backend in a pipeline | Typical sample use |
|---|---|---|
| `.onnx` | ONNX Runtime | Vision, speech, language, and compilation inputs. |
| `.ort` | ONNX Runtime | Preoptimized ONNX Runtime artifacts. |
| `.gguf` | llama.cpp, with the llama.cpp payload deployed | Unified decoder language samples. |
| `.oir`, `.mlirbc`, `.mlir` | CGC | Models compiled for a GPU target, see [The CGC backend](#the-cgc-backend). |

Device selection is separate. A sample can request `cpu`, `gpu`, or `npu`
while the model file type continues to choose the backend.

## Models given to a Task

A Models-backed Task takes a loaded model instead of a pipeline, and the call
that loaded the model decides how it runs:

| Load call | Model | How a Task runs it |
|---|---|---|
| `LoadModelFromPath` | A checkpoint folder with `config.json`, such as a Hugging Face folder of safetensors weights | Windows ML plans the model into CGC programs and runs it on the GPU target the application picked. |
| `LoadModelFromFile` or `LoadModelFromPath` | A GGUF file | The same as a checkpoint folder. A file whose architecture or weight types Windows ML does not support fails with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. |
| `IWinMLLlamaModelLoader::LoadModelFromFile` | A GGUF file | llama.cpp runs it, for text generation. |
| `LoadModelFromFile` for each exported graph, then `SetComposition` | Exported `.onnx` graphs, CGC artifacts, or a GGUF decoder | Each graph runs on its pipeline backend, composed as the description says. |

Windows ML never moves a model from one of these paths to another. To run a
GGUF file that Windows ML does not support, load it with the llama.cpp loader.
See [Models-backed Tasks](../../Samples/Tasks/model-backed/README.md).

## The CGC backend

CGC is the Windows-native backend that compiles a model into programs for a
Direct3D 12 device, and runs them on the GPU target the application picked. It
is new in Windows ML 2.9 and serves two kinds of work:

| Work | How it reaches CGC |
|---|---|
| Published weights given to a Models-backed Task | `SetModel` or `CreateDecisionTaskFromModel` with a model loaded by `LoadModelFromPath` or `LoadModelFromFile`. Windows ML plans the model, and the first session compiles its programs. |
| A CGC artifact in a pipeline or a composition | `LoadModelFromFile` or `LoadModelFromBuffer` loads an `.oir`, `.mlirbc`, or `.mlir` artifact, and `AddModelStage` or `SetComposition` runs it as a CGC stage. |

An application produces CGC artifacts with
[`IWinMLModelCompiler`](../api-reference/IWinMLModelCompiler.md) on a GPU
target that isn't pinned to an execution provider. The compiler imports an
ONNX model and writes one of two
[compiled model forms](../api-reference/IWinMLModelCompiler.md#compiled-model-form):

- **Device-targeted**, the default, finishes compilation for the device, driver,
  and Windows ML version that compiled it. It loads fastest, and another device
  or software version rejects it.
- **Durable** stops before the hardware-specific work and finishes it when the
  artifact loads, so it keeps loading across devices and updates.

Name a compiled artifact `.oir` for the device-targeted form and `.mlirbc` for
the durable form so that `LoadModelFromFile` recognizes it. See
[Compile for the CGC backend](tutorials/06-compile-and-deploy.md#5-compile-for-the-cgc-backend).

The first session of a Models-backed Task compiles its programs, which can take
several seconds. A program cache directory (`compiledProgramDirectory` in the
model options of the text generation, speech recognition, and decision Tasks)
keeps the compiled programs for later processes. By default it keeps only the
programs, and a later session prepares the weights from the model's files
again. `WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS_AND_WEIGHTS` also keeps the
prepared weights, about one copy for each program, so a later session reads
them instead. See
[Compiled programs and stored weights](../api-reference/IWinMLTasks.md#compiled-programs-and-stored-weights).

[`IWinMLStageExecutionDiagnostics`](../api-reference/IWinMLStageExecutionDiagnostics.md)
reports a CGC stage's work as compiled subgraphs, by execution technology, such
as `D3D12_DRIVER` and `DIRECTML`.

### Deployment

The CGC backend uses the Direct3D 12 Agility SDK. The
`Microsoft.Windows.AI.MachineLearning` package depends on
`Microsoft.Direct3D.D3D12` 1.620.1, which NuGet restores with it, and deploys
`D3D12\D3D12Core.dll` beside the application, including in .NET publish
output. The CGC modules and `DirectML.dll` are deployed beside the
application for x64 and Arm64 targets; packages with the CGC backend deploy
DirectML even when `WinMLCopyDirectML` is `false`, because the backend
requires it. ARM64EC applications get no CGC payload. The CGC and Models-backed
Task modules make the x64 and Arm64 native payload of the package a little more
than twice as large as in Windows ML 2.7, about 110 MB per architecture.

## Unified and split language layouts

The language samples use two layouts:

| Layout | Files | Backend path | What it shows |
|---|---|---|---|
| `unified` | One `.onnx`, `.ort`, or `.gguf` decoder | ONNX Runtime for `.onnx` and `.ort`; llama.cpp for `.gguf` | One decoder stage and a token loop. |
| `split` | `emb.onnx`, `decoder.onnx`, and `head.onnx` | ONNX Runtime | Stage placement, caller-owned bindings, and Runtime state. |

The split topology is:

```text
tokens -> emb.onnx -> decoder.onnx -> head.onnx -> logits
```

The application binds tokens to embedding input `0`. It binds the hidden state
from embedding output `0` to decoder input `0`, sequence position to decoder
input `1`, and the attention mask to decoder input `2`. Decoder output `0`
feeds head input `0`; the application reads logits from head output `0`.

The Python split sample reads token, position, mask, and logits tensor
properties from Runtime stage schemas. The mask shape supplies the sequence
capacity, and tokenizer metadata supplies terminal token IDs. The exporter also
emits `prefill.onnx` and `runtime_pipeline.json`; the Runtime samples do not use
them.

## Placement and residency

Model placement and tensor residency are different concepts.

- Model placement selects where a Runtime stage executes.
- Tensor residency describes where a tensor is stored when the application
  binds it or reads it.

For example, a GGUF model can use an optional accelerator module while the
sample reads logits through a CPU tensor. Runtime tensor movement is part of the
pipeline execution boundary.

## Composing pipelines with different backends

Speech to LanguageModel composes an ONNX Runtime speech pipeline with either an
ONNX Runtime or llama.cpp language pipeline. The model file loaded for the
language half selects that backend; the surrounding Runtime flow does not
change.

For GGUF payload setup, see [GGUF models](gguf-models.md).
