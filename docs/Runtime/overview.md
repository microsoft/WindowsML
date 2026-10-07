<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Windows ML Runtime API overview

The Windows ML (WinML) Runtime API is a Windows-native foundation for running AI models
locally on CPUs, GPUs, and NPUs. An application loads models, chooses where each
one runs, connects them into pipelines, and moves data through them as tensors.
The same programming model works for ONNX models, which run on ONNX Runtime
with execution providers, and for GGUF models, which run on llama.cpp.

Two more surfaces build on the same WinML Runtime objects. The Task API adds typed
text generation, chat completion, and speech recognition, and the Windows ML
Server serves text generation to OpenAI-compatible clients on the same
computer. This page explains how the three fit together and the design goals
behind them.

The direction is a common foundation that can grow with new models, hardware,
and inference engines. Future backends should be able to join this programming
model without requiring an application to rebuild its model, tensor, and
pipeline integration around each engine. Future Task APIs can build on that
foundation for more workloads, while the developer experience evolves to make
common tasks easier to configure and use.

> [!NOTE]
> The WinML Runtime, Task, and Server APIs are experimental and may change. Report
> issues and share feedback through
> [GitHub Issues](https://github.com/microsoft/windowsml/issues).

## How the pieces fit

```text
                 Application or middleware
          choose the entry point your workload needs
              |                |                |
              v                |                |
    Windows ML Server          |                |
    OpenAI-compatible HTTP     |                |
              |                v                |
              +----------> Task APIs            |
                           task behavior        |
                                |               v
                                +--------> WinML Runtime API
                                           models, targets, pipelines,
                                           stages, tensors, state
                                                   |
                                       +-----------+-----------+
                                       v                       v
                                  ONNX Runtime             llama.cpp
                                  .onnx / .ort              .gguf
```

These are layers of composition, not three independent execution stacks.
The Server exposes Task behavior through a local HTTP endpoint. Tasks build
that behavior on WinML Runtime models, pipelines, tensors, and state. The WinML Runtime
provides the execution foundation for both.

Start at the level that fits the application. An OpenAI-compatible client can
use the Server without handling the generation loop. An application or
middleware library can use Tasks directly for typed generation, chat, or
speech behavior, while retaining the WinML Runtime objects it supplies. For finer
control, use the WinML Runtime API directly and build the execution loop yourself.

Moving down a layer reveals more control over the same workload rather than
switching to a separate engine. A Task caller builds and owns the pipelines and
stages that the Task runs. A server host owns the WinML Runtime and Task
objects that its endpoint serves. The HTTP client uses the endpoint, while
the host controls those objects.

## The WinML Runtime programming model

A WinML Runtime application follows this basic sequence:

```text
Create the WinML Runtime
  -> Load a model artifact
  -> Choose execution targets
  -> Create a pipeline builder
       Add model or processor stages
       Connect stages
       Request the outputs to retain
       Build the pipeline
  -> Bind input tensors to stages
  -> Run one pipeline iteration
  -> Read output tensors
```

| Object | Meaning |
|---|---|
| WinML Runtime ([`IWinMLRuntime`](../api-reference/IWinMLRuntime.md)) | The context in which an application loads artifacts and constructs execution objects. It creates targets and pipeline builders. It isn't itself a model or generation session. |
| Model ([`IWinMLModel`](../api-reference/IWinMLModel.md)) | An immutable model artifact, loaded but not yet prepared for hardware. It describes what can be run, not a running instance or conversation. [`IWinMLModelSchema`](../api-reference/IWinMLModelSchema.md) exposes its declared inputs and outputs. |
| Execution target ([`IWinMLExecutionTarget`](../api-reference/IWinMLExecutionTarget.md)) | The application's choice of execution or allocation environment, with the capabilities that environment supports. A stage's compute placement and a tensor's memory location are separate choices. |
| Pipeline builder ([`IWinMLPipelineBuilder`](../api-reference/IWinMLPipelineBuilder.md)) | The construction step: add model or processor stages, choose their targets, declare connections, then build the executable pipeline. |
| Pipeline ([`IWinMLPipeline`](../api-reference/IWinMLPipeline.md)) | An execution block that brings stages, data connections, and state coordination together. The application runs iterations of that block rather than scheduling each stage separately. |
| Stage ([`IWinMLStage`](../api-reference/IWinMLStage.md)) | A unit of execution within a pipeline: a model or processor on a target, with bound inputs and outputs. A stage can contain an engine's own internal execution machinery. It need not expose every operation inside a model. |
| Stateful stage ([`IWinMLStatefulStage`](../api-reference/IWinMLStatefulStage.md)) | A stage that retains sequence state across runs, such as a decoder's KV cache. Its capacity, position, and rewind operations let the application manage that state without treating it as another model. |
| Tensor ([`IWinMLTensor`](../api-reference/IWinMLTensor.md)) | Typed data with a layout, memory location, lifetime, and access rules. It is the shared data contract between application code and stages, whether the data starts in CPU memory or on a device. |
| Tokenizer ([`IWinMLTokenizer`](../api-reference/IWinMLTokenizer.md)) | The model's text encoding and decoding boundary. Formatter interfaces can apply conversation structure and chat templates where supported. |

The distinction between model, stage, and pipeline is central. A model is the
artifact you bring. A stage makes that model executable on a chosen target and
provides the bindings and state used during execution. A pipeline composes
stages into a block the application can run and reuse.

For example, a GGUF decoder can remain one stage, with llama.cpp managing its
internal computation and sequence state. The split ONNX language sample
instead uses embedding, decoder, and head stages connected in one pipeline.
Both fit the same object model: the API describes the execution boundaries
the application needs, not a requirement to break every engine into the same
internal structure.

Python applications use the same objects through `windowsml.runtime`.
[Common patterns](../api-reference/CommonPatterns.md) shows the flow in C++.

## WinML Runtime design goals

### One programming model across backends

The model file selects the backend: `.onnx` and `.ort` files run on ONNX
Runtime, and `.gguf` files run on llama.cpp. After loading, the targets,
pipeline builder, stage bindings, tensors, and [`Run`](../api-reference/IWinMLPipeline.md#run) are the same for both, so
[Hello LanguageModel](../../Samples/Runtime/language/hello-language-model/) streams a reply from an
ONNX or a GGUF model with the same application flow. Loading a model doesn't
prepare it for hardware. [`Build`](../api-reference/IWinMLPipelineBuilder.md#build) prepares each model for the target of its
stage. [Backends and model formats](backends.md) covers the details.

### Placement that the application chooses

Each stage runs on an execution target. The application can request a hardware
class with an efficiency or performance preference, choose a specific adapter
or D3D12 device, use the CPU, or let the WinML Runtime place the stage. A preference
orders the devices within the requested class rather than moving the stage to
another class. When the requested device class or device isn't available, target creation fails
and the application decides what to request next. For ONNX models, the
application can also pin a stage to one execution provider. After `Build`,
each stage reports the target that it resolved to.

The WinML Runtime doesn't retry a failed build on another target or provider.
Within ONNX Runtime, operators that a provider declines can run on the CPU,
sometimes the whole model, so check where a model runs when that matters.
[Devices and execution providers](providers.md) covers device options,
provider pinning, and failures.

### Pipelines that the application declares

A pipeline connects executable stages. It is not the operator graph inside a
model. One stage can run a whole model, with the backend managing its internal
operations. A model stage runs a loaded model, and a
processor stage runs a tensor operation created by the target's processor
factory. [`Connect`](../api-reference/IWinMLPipelineBuilder.md#connect) passes an output to a later stage's input within one
iteration. [`ConnectNextIteration`](../api-reference/IWinMLPipelineBuilder.md#connectnextiteration) passes an output to an input in the next
iteration, for loops such as feeding a sampled token back into a decoder.
Each `Run` executes one iteration.
[Whisper](../../Samples/Runtime/speech/whisper/) uses separate encoder and decoder pipelines, and
[LLM chat](../../Samples/Runtime/language/llm-chat/) can split a language model into three connected
stages.

Language models use the same pipelines. A stateful decoder retains state
between runs, while the application or a Task runs the token loop: bind tokens,
run the pipeline, read logits, and choose the next token. See
[Manual text decode loop](../api-reference/CommonPatterns.md#pattern-9-manual-text-decode-loop).

### State that the application can see

Stages that keep state between runs, such as a language model's KV cache,
expose sequence capacity, current position, and [`RewindTo`](../api-reference/IWinMLStatefulStage.md#rewindto) through
`IWinMLStatefulStage`. [`ResetExecutionState`](../api-reference/IWinMLPipeline.md#resetexecutionstate) resets pipeline state without
rebuilding the pipeline or changing input bindings. For models that carry
state as input and output tensors, [`IWinMLStatefulStageOptions`](../api-reference/IWinMLStatefulStageOptions.md) pairs those
tensors before `Build`, and the application can own them. The application
decides when to keep, rewind, or reset state.

### Prepare once, run many times

Preparing a model for a GPU or NPU can take a long time. Where a target
supports it, [`IWinMLModelCompiler`](../api-reference/IWinMLModelCompiler.md), queried from the target, writes a compiled
artifact to a file or to an application-provided sink. The application loads
the artifact later with the same [`LoadModelFromFile`](../api-reference/IWinMLRuntime.md#loadmodelfromfile) and [`LoadModelFromBuffer`](../api-reference/IWinMLRuntime.md#loadmodelfrombuffer)
calls as any other model. Compiled artifacts can be specific to the hardware
and backend used to prepare them.
[Model compilation](../../Samples/Runtime/compile-and-deploy/model-compilation/) compiles a model,
reloads it, and runs it.

### Backend detail when you need it

The core interfaces don't require backend-specific concepts. When an
application needs them, extension interfaces are available through
`QueryInterface`. ONNX Runtime extensions provide provider-pinned targets,
named stage bindings, provider options and session settings, shared provider
contexts, and diagnostics about the provider a stage selected.
[Managed shared context](../../Samples/Runtime/interop/managed-shared-context/) runs two ONNX
Runtime stages in one shared provider context. Applications can use these
extensions without making them part of every model's execution flow.

## The Tensor API: data, memory, and native integration

The Tensor API is the data side of the WinML Runtime foundation. It gives application
code, Tasks, and model stages a common way to describe data and control its
memory, lifetime, and access. Choosing where a model runs is one decision.
Choosing where its inputs live and how results reach the application is another.

Tensor factories and native adapters turn images, NV12 video frames, PCM audio,
and token IDs into tensors in the layout and data type a model needs. Image
paths can also resize and normalize pixels. For data already in the right
layout, the raw tensor factory can copy a CPU buffer or wrap it in place.
Tensor locks provide CPU access, with synchronized access when a copy or
readback is needed.

Inputs are bound by index and stay bound until they're replaced. Outputs go to
tensors the application provides, or to WinML Runtime-owned tensors the application
reads after `Run`.

This release's samples primarily use CPU-visible inputs and results, including
when inference runs on a GPU or NPU. They demonstrate native data conversion
and tensor ownership, not an end-to-end GPU zero-copy path. A view over a CPU
buffer avoids copying that buffer when the tensor is created. It does not
establish that a backend can execute on it without further data movement.

The Tensor API is evolving toward more efficient data paths, including
zero-copy resource sharing where the workload supports it. The reference
includes D3D12 buffer bindings and synchronization interfaces for working with
data already on a device. Whether a stage can use that data in place depends
on the resource, layout, synchronization, target, and backend. The
[GPU buffer binding pattern](../api-reference/CommonPatterns.md#pattern-6-gpu-tensor-zero-copy)
illustrates the interface usage rather than a validated sample scenario.

The goal is to reduce unnecessary transfers and let inference share more
data with graphics and media workloads, while keeping a common data API across
backends. Format conversions can still produce new tensor data.

## The Task API

The Task API adds typed tasks on top of WinML Runtime objects the application
creates. The application still loads models, creates targets, builds
pipelines, and keeps conversation history. Objects supplied to one Task must
come from the same WinML Runtime. Tasks handle work that applications and middleware
would otherwise implement themselves:

- **Text generation** runs the token loop with sampling, optional constraints,
  streaming, and cancellation. The final result reports token counts and why
  generation stopped. It works with one unified pipeline or separate prefill
  and decode pipelines.
- **Speculative decoding** proposes several draft tokens and verifies them in
  one step on stages that support multi-position verification and rollback.
  Drafts come from the model's predictor, a smaller draft model, or matches in
  earlier text. The [Text generation](../../Samples/Tasks/language/text-generation/)
  sample uses it with GGUF models.
- **Chat completion** applies the model's chat template to supplied messages
  and separates content, reasoning, and tool calls in the output. When a
  request extends the tokens the session already holds, only the new part is
  evaluated, and the result reports how many prompt tokens were reused.
- **Speech recognition** runs application-built Whisper encoder and decoder
  pipelines and turns audio into a transcript.

Tasks don't load models, choose targets or backends, run tools, or retry
requests. Applications and libraries can use this common behavior and still
reach the WinML Runtime objects underneath. An application that needs a different
loop or policy can skip Tasks and use the WinML Runtime API directly.
[Task and WinML Runtime lifecycle](../Tasks/task-lifecycle.md) shows ownership,
sessions, streams, and cancellation.

The current Tasks are a starting point, not a fixed list of workloads. Future
Task APIs and improvements to the developer experience can make more of this
shared behavior reusable, with simpler setup and less application code. The
design goal is to keep that convenience connected to the same WinML Runtime objects,
so developers can start with a task and still take control of execution when
they need it.

## Windows ML Server

The Windows ML Server serves text generation to other processes on the same
computer through an OpenAI-compatible Chat Completions endpoint. Coding agents
and OpenAI client libraries can use a local model without a different client
protocol.

The hosting application prepares each model from WinML Runtime and Task objects,
so it decides which model is loaded and where it runs. The server validates
requests, formats conversations with the model's tokenizer, runs generation,
and streams results. It doesn't load models, select targets, download files,
run tools, or keep conversation history between requests.

The server listens only on loopback and requires an access key created each
time it starts. For development and testing, the `WinMLServer.exe` host loads a model
file and serves it from the command line. See the
[Server samples](../../Samples/Server/README.md).

## Languages and packages

The C++ samples use the `Microsoft.Windows.AI.MachineLearning` NuGet package
and add `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` for GGUF support.
[`Directory.Packages.props`](../../Samples/Directory.Packages.props) lists the sample
versions.

Python applications use `windowsml.runtime`. Install the `with-ort` extra for
the matching ONNX Runtime package, and install the `windowsml-llama-core` wheel
for GGUF support:

```powershell
python.exe -m pip install --pre "windowsml[with-ort]"
python.exe -m pip install --pre windowsml-llama-core
```

The Python samples keep each scenario short. The C++ samples add native
integrations such as WIC and Media Foundation.
[Python samples](python-samples.md#differences-from-c) lists the differences.

## Where to go next

| Goal | Read |
|---|---|
| Run the samples in order | [Tutorials](tutorials/README.md) |
| Understand backends and model formats | [Backends and model formats](backends.md) |
| Choose devices and execution providers | [Devices and execution providers](providers.md) |
| Run GGUF language models | [GGUF language models](gguf-models.md) |
| Create, bind, and read tensors | [`IWinMLTensor`](../api-reference/IWinMLTensor.md) |
| Use typed Tasks | [Task API samples](../../Samples/Tasks/README.md) |
| Serve a model to OpenAI-compatible clients | [Server samples](../../Samples/Server/README.md) |
| Look up interfaces and examples | [WinML Runtime API reference](../api-reference/README.md) |
