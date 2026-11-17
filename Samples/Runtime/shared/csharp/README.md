<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Shared C# helpers and the sample wrapper

The C# samples in `Samples\Runtime` and `Samples\Tasks` import
[`WinMLSamples.CSharp.props`](WinMLSamples.CSharp.props). It targets .NET 8,
references the `Microsoft.Windows.AI.MachineLearning` package and
[CsWin32](https://github.com/microsoft/CsWin32), sets the runtime identifier
that copies the native Windows ML DLLs next to the executable, and compiles
the sample wrapper in [`Runtime`](Runtime/) and the helpers in this folder into
each sample.

## The sample wrapper

The Windows ML Runtime and Task APIs are COM-style C APIs. Called directly
from C#, they read like interop: factory functions with `void**` out
parameters, calls through interface pointers, hand-written `HRESULT` checks,
and manual `Release`. The [`Runtime`](Runtime/) folder holds a small wrapper
that gives the samples an idiomatic C# surface instead, so the sample code
itself has no pointers, `unsafe` blocks, or `HRESULT` constants:

```csharp
using WinMLSamples.Runtime;

using WinMLRuntime runtime = WinMLRuntime.Create();
using ExecutionTarget target = runtime.CreateCpuExecutionTarget();
using Model model = runtime.LoadModelFromFile(modelPath);
using PipelineBuilder builder = runtime.CreatePipelineBuilder();
using Stage stage = builder.AddModelStage(model, target, "classifier");
stage.RequestOutput(0);
using Pipeline pipeline = builder.Build();

using Tensor input = target.CreateTensor<float>(pixels, [1, 3, 224, 224]);
stage.BindInput(0, input);
pipeline.Run();

using Tensor output = stage.GetOutput(0);
using TensorLock<float> scores = output.Lock<float>();
ReadOnlySpan<float> values = scores.AsReadOnlySpan(); // a CPU tensor's own memory
```

```csharp
using WinMLSamples.Runtime.Tasks;

await using TextGenerator generator = await TextGenerator.CreateAsync(tasks, task, configuration);
await foreach (TextGenerationUpdate update in generator.GenerateTokensAsync(promptTokens, options, cancellationToken))
{
    Console.Write(update.Text);
}
```

> [!IMPORTANT]
> The wrapper is sample code, not a supported API. It is not shipped in a
> package and may change with the samples. It covers every interface of the
> Task API (see [Task API coverage](#task-api-coverage)) and the parts of the
> Runtime API that the samples call. Not every member has run: the
> coverage table says which sample exercises each interface. You can
> copy the wrapper into an app, but you then maintain it.

What the wrapper provides:

- **Ownership.** Each object owns one reference to its native interface and
  releases it in `Dispose`, so `using` releases memory and device resources
  deterministically. A finalizer releases objects that were not disposed.
  `As<T>()` and `TryAs<T>(out T)` query an object for another interface and
  return an object that you dispose. A property that returns a related object,
  such as `ExecutionTarget.RawTensorFactory`, returns one that its parent owns;
  disposing it does nothing.
- **Errors.** A failure `HRESULT` throws a .NET exception whose `HResult` is
  the native value, with the system message for it. Failures with a .NET
  meaning throw that type, such as `NotSupportedException` for
  `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` and `FileNotFoundException` for
  `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)`; only other failures throw
  `WinMLException`, so catch by type or test `HResult`.
  [`WinMLException.cs`](Runtime/WinMLException.cs) lists the mapping. Success
  codes, including `S_FALSE`, never throw. Where `S_FALSE` means something, the
  wrapper returns `bool`, `null`, or uses a `Try` method, such as
  `TokenizerMetadata.TryGetBosTokenId`.
- **.NET names and types.** `IWinMLExecutionTarget` is `ExecutionTarget`,
  `WINML_EXECUTION_TARGET_KIND_GPU` is `ExecutionTargetKind.Gpu`, simple
  getters and setters are properties, strings are `string`, and arrays are
  `Span<T>`, `ReadOnlySpan<T>`, or `T[]`.
- **Tensors.** `Tensor.Lock<T>()` returns a `TensorLock<T>`; disposing it
  unlocks the tensor. For a CPU tensor, its `Span` and `AsReadOnlySpan()` are
  the tensor's own memory. A device tensor needs
  `TensorLockFlags.AllowSynchronizedCpuAccess`, and its span is then a staged
  copy; call `Commit()` to keep writes to it. Write only through a lock taken
  with a write mode, and don't copy a `TensorLock<T>`.
  `ExecutionTarget.CreateTensor<T>` copies a span into a new tensor, and
  `ImageTensorFactory` and `RawTensorFactory` wrap the Runtime's tensor
  factories.
- **Token streaming.** A text generation session belongs to the thread that
  created it ([`IWinMLTextGenerationTask`](../../../../docs/api-reference/IWinMLTextGenerationTask.md)).
  `TextGenerator` keeps a session on a dedicated `SessionThread` and streams
  tokens as `IAsyncEnumerable<TextGenerationUpdate>`. Cancelling the
  `CancellationToken`, leaving the loop early, or disposing the generator
  cancels generation in the Runtime. After a cancel or an early exit,
  `LastResult` holds the result of the generation; disposing the generator
  releases it. On the session's own thread, `foreach` over a
  `TextGenerationPullStream` reads `TextGenerationFragment` values whose text
  is a `ReadOnlySpan<char>` borrowed until the next read, with no managed
  allocation per token. No sample uses that loop; it is there for apps that
  manage the session thread themselves.
- **Chat completion.** `WinMLTasks.ChatCompletion` creates chat completion
  tasks, which compose a tokenizer's chat template with a text generation
  session. `ChatGenerator` owns both sessions on a `SessionThread` and streams
  a `ConversationRequest` (roles, tool calls and results, tools, tool choice,
  response format, and thinking) as `IAsyncEnumerable<ChatCompletionUpdate>`
  content, reasoning, and tool-call events. `LastOutcome` is a managed copy of
  the result, with token counts and how many prompt tokens the session reused
  from the previous turn. `TextGenerationOptionValues` sets sampling options
  and stop tokens, and `TextGenerationSession.Capabilities` reports whether a
  session can constrain its tokens, which tool calls need.
- **Speech recognition and decisions.** `SpeechRecognizer` owns a speech
  recognition session on a `SessionThread` and streams
  `IAsyncEnumerable<SpeechRecognitionUpdate>` values; `LastOutcome` copies
  the result. `Decider.DecideAsync` runs a decision on its session's thread.
  Both map a `CancellationToken` to a `CancellationSource`, cancel on dispose,
  and offer `CloseAsync`. Live speech input and the optional text generation
  views, such as `TextGenerationSequenceDisclosure`, are bound to the session's
  thread: run them through `InvokeAsync`, as the
  [text generation features sample](../../../Tasks/model-backed/text-generation-features/csharp/)
  does.
- **Optional interfaces.** Interfaces that the Task API exposes through
  `QueryInterface`, such as `TextGenerationSpeculativeSupport` on a
  configuration or `ChatCompletionEventProvenance` on a chat event, are
  wrapper types that you get with `As<T>()` or `TryAs<T>(out T)`.
- **Three kinds of ownership.** Most objects are owned: `Dispose` releases
  them, and closes a session, stream, or live input that was not closed. A
  property that returns a related object, such as `WinMLTasks.TextGeneration`,
  returns one its parent owns, and disposing it does nothing. An object that
  another object owns, such as the text generation session that
  `ChatCompletionSession.GetTextGenerationSession` returns, is borrowed:
  `Dispose` releases it without closing it, and `Close` throws. `As<T>()`
  returns an owned object, so don't query a session view, such as
  `SpeechRecognitionLiveSession`, back to its session type and dispose the
  result: that closes the session. Descriptors that a `Get` method returns,
  such as `TextGenerationDraftPipeline`, own their objects; ones you create
  don't, and disposing them does nothing.
- **Devices.** `DXCoreAdapter.FindBest` ranks the GPUs or NPUs that DXCore
  reports, and `WinMLRuntime.CreateExecutionTarget` has overloads for a device
  class, an adapter, and a registered ONNX Runtime execution provider. A
  target keeps the adapter or hardware target it was created from alive.

The wrapper calls internal bindings that CsWin32 generates from the Win32
metadata in the package. [`NativeMethods.txt`](NativeMethods.txt) lists the
APIs it calls, and [`NativeMethods.json`](NativeMethods.json) generates them as
unmanaged structs (`allowMarshaling: false`) whose methods all return their
`HRESULT` (`preserveSigMethods: ["*"]`), so the wrapper decides which codes are
errors. Most wrapper methods make one interface call and check its `HRESULT`.

To use a Runtime API that the wrapper does not cover yet, add the API to
`NativeMethods.txt` and a method to the wrapper type, following the existing
methods.

## Naming conventions

The wrapper maps native names to .NET names with the rules below, which are
the usual rules for projecting a COM-style API into C#. These names are not a
commitment to any future API. The wrapper's namespaces, `WinMLSamples.Runtime`
and `WinMLSamples.Runtime.Tasks`, are distinct from the API's
`Microsoft.Windows.AI.MachineLearning.Runtime` namespaces, so the two cannot
collide.

| Native | Wrapper |
|---|---|
| `IWinMLRuntime`, `IWinMLTasks`, `IWinMLExecutionTarget`, ... | `WinMLRuntime`, `WinMLTasks`, `ExecutionTarget`, ...: interfaces drop `IWinML`, and the two root objects drop only `I`. |
| `WinMLCreateRuntime`, `WinMLCreateTasks`, `WinMLCreateTokenizerFromFile` | `WinMLRuntime.Create()`, `WinMLTasks.Create(runtime)`, `Tokenizer.CreateFromFile(path)` |
| `WINML_EXECUTION_TARGET_KIND_GPU` | `ExecutionTargetKind.Gpu` |
| `GetX` with no arguments that returns a value, `SetX` with one | Property `X`. Getters that return a new object or array stay methods, such as `GetTokenizer()` and `GetTokenIds()`. |
| `[out, retval]` value or interface | Return value |
| `PCWSTR` | `string` |
| Counted array input | `ReadOnlySpan<T>`, or a list in a request object |
| `QueryInterface` | `As<T>()`, `TryAs<T>(out T)` |
| Structure with a `presentFields` mask | Class with nullable properties |
| Failure `HRESULT` | .NET exception |

Some members have no native counterpart: `DXCoreAdapter`, `SessionThread`,
`TextGenerator`, `ChatGenerator` and its `ChatCompletionUpdate` and
`ChatCompletionOutcome` records, `SpeechRecognizer` and its
`SpeechRecognitionUpdate` and `SpeechRecognitionOutcome` records, `Decider`,
`TensorLock<T>`, `MediaEncoding`, `TextGenerationOutputBinding`,
`TextGenerationDraftPipeline`, `GenerationSegment`, the adapter and provider
overloads of `WinMLRuntime.CreateExecutionTarget`, `TryCreateExecutionTarget`,
`TokenizerMetadata.TryGetBosTokenId`, `WinMLObject.IsSameObject`, and
`WinMLException.NotSupported`, `InvalidData`, `NoInterface`, and
`Unexpected`. The last four create the .NET exception for a condition the
sample detects itself, with the `HRESULT` the C++ samples report for it, so
the C# and C++ samples exit and report alike.

The speech recognition types drop `Automatic` after the factory and the task:
`IWinMLAutomaticSpeechRecognitionSession` is `SpeechRecognitionSession`, and
`IWinMLModelAutomaticSpeechRecognitionConfiguration` is
`ModelSpeechRecognitionConfiguration`. An `[out] BOOL` that says whether
another out value is present becomes a nullable value, such as
`SpeechRecognitionLiveInput.LifetimeWatermark`.

## Task API coverage

Every interface of the Task API in Windows ML 2.9 has a wrapper type. The
samples column names the C# samples that call it, directly or through
`TextGenerator`, `ChatGenerator`, `SpeechRecognizer`, or `Decider`:
chat ([chat completion](../../../Tasks/language/chat-completion/csharp/)),
text ([Models-backed text generation](../../../Tasks/model-backed/text-generation/csharp/)),
features ([text generation features](../../../Tasks/model-backed/text-generation-features/csharp/)),
model ASR ([Models-backed speech recognition](../../../Tasks/model-backed/automatic-speech-recognition/csharp/)),
ASR ([speech recognition](../../../Tasks/speech/automatic-speech-recognition/csharp/)), and
decision ([typed decisions](../../../Tasks/model-backed/decision/csharp/)).
The model ASR and decision samples need a GPU target.

| Interface | Wrapper | Samples |
|---|---|---|
| `IWinMLTasks` | `WinMLTasks` | all |
| `IWinMLCancellationSource` | `CancellationSource` | all that stream or decide |
| `IWinMLTaskRuntimeIdentity` | `TaskRuntimeIdentity` | features, ASR |
| `IWinMLTextGenerationTaskFactory` | `TextGenerationTaskFactory` | chat, text, features |
| `IWinMLTextGenerationOptions` | `TextGenerationOptions` | chat, text, features |
| `IWinMLTextGenerationConfiguration` | `TextGenerationConfiguration` | chat, text, features |
| `IWinMLModelTextGenerationConfiguration` | `ModelTextGenerationConfiguration` | text, features (`SetModel`; `SetComposition` is not run) |
| `IWinMLTextGenerationTask` | `TextGenerationTask` | chat, text, features |
| `IWinMLTextGenerationSession` | `TextGenerationSession` | chat, text, features (`CreateMediaEncoderFromFile` is not run) |
| `IWinMLTextGenerationSequenceDisclosure` | `TextGenerationSequenceDisclosure` | features |
| `IWinMLTextGenerationConstraintSession` | `TextGenerationConstraintSession` | features (token prompts) |
| `IWinMLTextGenerationConstraintContinuation` | `TextGenerationConstraintContinuation` | features |
| `IWinMLTextGenerationPullStream` | `TextGenerationPullStream` | text, features |
| `IWinMLTextGenerationResult` | `TextGenerationResult` | text, features |
| `IWinMLTextGenerationSpeculativeConfiguration` | `TextGenerationSpeculativeConfiguration` | text with `--draft-model`, features |
| `IWinMLTextGenerationSpeculativeSupport` | `TextGenerationSpeculativeSupport` | features |
| `IWinMLTextGenerationSpeculativeResult` | `TextGenerationSpeculativeResult` | text with `--draft-model`, features |
| `IWinMLTextGenerationWatermarkConfiguration` | `TextGenerationWatermarkConfiguration` | features |
| `IWinMLTextGenerationWatermarkResult` | `TextGenerationWatermarkResult` | features |
| `IWinMLTextGenerationWatermarkDetector` | `TextGenerationWatermarkDetector` | features |
| `IWinMLChatCompletionTaskFactory` | `ChatCompletionTaskFactory` | chat, features |
| `IWinMLChatCompletionTask` | `ChatCompletionTask` | chat, features |
| `IWinMLChatCompletionSession` | `ChatCompletionSession` | chat, features (`GenerateWithMedia` and the media encoder are not run) |
| `IWinMLChatCompletionPullStream` | `ChatCompletionPullStream` | chat, features |
| `IWinMLChatCompletionResult` | `ChatCompletionResult` | chat, features |
| `IWinMLChatCompletionEventProvenance` | `ChatCompletionEventProvenance` | features |
| `IWinMLAutomaticSpeechRecognitionTaskFactory` | `AutomaticSpeechRecognitionTaskFactory` | model ASR, ASR |
| `IWinMLAutomaticSpeechRecognitionConfiguration` | `SpeechRecognitionConfiguration` | model ASR, ASR |
| `IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration` | `EncoderDecoderSpeechRecognitionConfiguration` | ASR |
| `IWinMLModelAutomaticSpeechRecognitionConfiguration` | `ModelSpeechRecognitionConfiguration` | model ASR |
| `IWinMLAutomaticSpeechRecognitionTask` | `AutomaticSpeechRecognitionTask` | model ASR, ASR |
| `IWinMLAutomaticSpeechRecognitionSession` | `SpeechRecognitionSession` | model ASR, ASR |
| `IWinMLAutomaticSpeechRecognitionEncoderInput` | `SpeechRecognitionEncoderInput` | ASR (queried; no sample computes log-mel input) |
| `IWinMLAutomaticSpeechRecognitionLiveSession` | `SpeechRecognitionLiveSession` | model ASR with `--live` |
| `IWinMLAutomaticSpeechRecognitionLiveInput` | `SpeechRecognitionLiveInput` | model ASR with `--live` |
| `IWinMLAutomaticSpeechRecognitionPullStream` | `SpeechRecognitionPullStream` | model ASR, ASR |
| `IWinMLAutomaticSpeechRecognitionResult` | `SpeechRecognitionResult` | model ASR, ASR |
| `IWinMLDecisionTaskFactory` | `DecisionTaskFactory` | decision |
| `IWinMLDecisionTask` | `DecisionTask` | decision |
| `IWinMLDecisionSession` | `DecisionSession` | decision |
| `IWinMLDecisionResult` | `DecisionResult` | decision |

The members that no sample runs need a model the sample catalog lacks: a
multimodal model and projector for the media encoder, `GenerateWithMedia`,
and embedding segments, and an exported ONNX decoder for `SetComposition`.
The Task API also returns Runtime objects that the wrapper covers only as far
as the Task API needs them: `MediaEncoder`, `SequenceSegmentLayout`,
`TokenConstraint`, `TokenizerConstraintFactory`, `ConversationOutputParser`,
and `AudioTensorFactory`.

## Files

| File | Purpose |
|---|---|
| `Runtime\WinMLObject.cs` | Base class: one owned reference, `Dispose`, finalizer, `As<T>`, `TryAs<T>`. |
| `Runtime\WinMLException.cs` | `HRESULT` to .NET exception mapping. |
| `Runtime\Types.cs` | Enumerations and descriptors: `ExecutionTargetKind`, `TensorDataType`, `ImageTensorDescription`, `ConversationRequest`, ... |
| `Runtime\WinMLRuntime.cs` | `WinMLRuntime`, `ExecutionTarget`, `Model`, `ModelSchema`, `OrtCompatibility`, `LlamaModelLoader`. |
| `Runtime\DXCoreAdapter.cs` | DXCore adapter enumeration and ranking. |
| `Runtime\Pipeline.cs` | `PipelineBuilder`, `Pipeline`, `Stage`, and the stage schema, state, and diagnostics interfaces. |
| `Runtime\StageOptions.cs` | ONNX symbolic dimension overrides and ONNX Runtime session entries for a stage. |
| `Runtime\OrtModelSchema.cs` | Finds an ONNX model's inputs and outputs by name. |
| `Runtime\Tensors.cs` | `Tensor`, `TensorLock<T>`, `RawTensorFactory`, `ImageTensorFactory`. |
| `Runtime\AudioTensors.cs` | `AudioTensorFactory`: PCM audio to tensors, such as speech recognition waveforms. |
| `Runtime\MediaEncoder.cs` | `MediaEncoder` and `SequenceSegmentLayout`, for multimodal prompts. |
| `Runtime\Tokenizers.cs` | `Tokenizer`, `TokenizerDecoder`, `TokenizerMetadata`, `StructuredConversationFormatter`. |
| `Runtime\Constraints.cs` | `TokenizerConstraintFactory`, `TokenConstraint`, `ConversationOutputParser`. |
| `Runtime\Conversation.cs` | Copies a `ConversationRequest` into the native request that the formatter and chat sessions take. |
| `Runtime\SessionThread.cs` | A thread that owns thread-bound objects such as text generation sessions. |
| `Runtime\Tasks\TextGeneration.cs` | The text generation Task API: the `WinMLTasks` root, configurations, options, tasks, sessions, pull streams, and results. |
| `Runtime\Tasks\TextGenerationFeatures.cs` | The optional text generation interfaces: sequence disclosure, constraints, speculative support, watermarks, segments, compositions, and `TaskRuntimeIdentity`. |
| `Runtime\Tasks\TextGenerator.cs` | `IAsyncEnumerable` token streaming with cancellation. |
| `Runtime\Tasks\ChatCompletion.cs` | The chat completion Task API: tasks, sessions, event streams, results, and event provenance. |
| `Runtime\Tasks\ChatGenerator.cs` | `IAsyncEnumerable` chat completion events with cancellation. |
| `Runtime\Tasks\SpeechRecognition.cs` | The speech recognition Task API: configurations, sessions, encoder and live input, pull streams, and results. |
| `Runtime\Tasks\SpeechRecognizer.cs` | `IAsyncEnumerable` speech recognition with cancellation. |
| `Runtime\Tasks\Decision.cs` | The typed decision Task API: tasks, sessions, and results. |
| `Runtime\Tasks\Decider.cs` | Decisions from any thread, with cancellation. |
| `NativeMethods.txt` | The Runtime, Task, DXCore, and COM APIs that the wrapper calls. |
| `NativeMethods.json` | CsWin32 options: unmanaged interface structs whose methods return their `HRESULT`. |
| `DeviceOptions.cs` | Parses `--device`, `--ep`, `--performance`, `--efficiency`, and `--verbose`. |
| `ExecutionProviders.cs` | Prepares and registers an `--ep` provider from the Windows ML execution-provider catalog and enables verbose ONNX Runtime logging. Matches `ep_catalog_utils.h`. |
| `ExecutionTargets.cs` | Creates CPU, GPU, and NPU targets from ranked DXCore adapters, and provider-pinned targets. Matches `execution_target_utils.h`. |
| `StageDiagnostics.cs` | Enables and prints a stage's execution diagnostics and ONNX Runtime provider. |
| `SampleAssets.cs` | Finds files under the catalog's `models` directory. |
| `WhisperAudio.cs` | Reads a WAV file and creates a Whisper waveform tensor. Matches `LoadWavFile` in `audio_utils.h` and `CreateWhisperWaveformTensorFromPcm`. |

The samples require CsWin32 0.3.369 or later. Earlier versions generate some
`IUnknown*` parameters, such as the adapter that
`IWinMLRuntime::CreateExecutionTargetFromAdapter` takes, as `IUnknown` values
instead of pointers
([microsoft/CsWin32#1874](https://github.com/microsoft/CsWin32/issues/1874)).
