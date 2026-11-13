<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# API Reference -- WinML Runtime

This reference covers the Runtime, Tokenizer, Task, and Server interfaces. Start with
[Factory Functions](FactoryFunctions.md) for activation,
[Common Patterns](CommonPatterns.md) for usage examples, and
[Error Codes](ErrorCodes.md) for shared failure behavior.

## Overview

Interfaces derive from `IUnknown`, use COM reference counting, and return
`HRESULT`. Use `ComPtr<T>` or manual `AddRef` and `Release` for lifetime
management.

Create objects through the supplied in-process factory functions, not registered
COM activation. `WinMLCreateRuntime` does not require COM initialization.
The native NuGet integration and the `WindowsML::Runtime` and
`WindowsML::Tokenizer` CMake targets link the generated interface IID definitions
for C and C++ consumers. C++ callers may also use `__uuidof`; applications do not
need to redeclare IID values or register the Runtime DLLs. The Server interfaces
are linked when the project sets `WinMLCopyServerRuntime` or links
`WindowsML::Server`; see [IWinMLServer](IWinMLServer.md#activation). C#
applications generate bindings for the Runtime and Task APIs from the metadata
in the package with CsWin32; see
[C# bindings with CsWin32](../Runtime/overview.md#c-bindings-with-cswin32).

D3D12 types (`ID3D12Resource`, `ID3D12Device`, `ID3D12CommandQueue`) are
returned as `IUnknown*` in the IDL. Callers
`QueryInterface` for the concrete D3D12 interfaces after including the
Agility SDK `d3d12.h`.

**Interface IIDs:**

| Interface | IID |
|---|---|
| `IWinMLRuntime` | `6954707d-3987-491a-ada9-2bea9b0e13f9` |
| `IWinMLLlamaModelLoader` | `b9725a87-e502-4be6-a9ae-1185fd9b77cc` |
| `IWinMLModel` | `2453f1b4-81ce-4fd4-99ce-b83136883ff4` |
| `IWinMLModelSchema` | `7696a11b-6f39-478a-b33d-c69850dbc2dc` |
| `IWinMLModelWeightFormats` | `56066f5a-079a-4890-8b2f-24c0cddd549c` |
| `IWinMLModelWeightFormat` | `f2bde5d5-80b2-4268-9e99-ba5fd1d87195` |
| `IWinMLExecutionTarget` | `21a4ed3a-0ab3-441c-b9d8-f7d962001774` |
| `IWinMLD3D12ExecutionTarget` | `b243d097-07d0-4f3c-8519-370df6321e43` |
| `IWinMLModelCompiler` | `906a0175-188e-403f-a0ef-5caa0692db9e` |
| `IWinMLModelCompileOptions` | `8e5f1c07-4a2b-4d16-9c33-b7a4e2d95f80` |
| `IWinMLOnnxSymbolicDimensionOverrides` | `c117047b-3388-421a-b1ef-4afa36295859` |
| `IWinMLPipelineBuilder` | `3bcfcf5a-8b40-4931-9812-d84679123006` |
| `IWinMLStage` | `88596a2b-24fe-476e-bc90-c37161edb477` |
| `IWinMLStageHostBuffers` | `a53a28b2-b597-4773-9c5f-aadfb1945ad6` |
| `IWinMLStageExecutionDiagnostics` | `5fd646ac-5f56-42fb-9ea5-cee1c39feb22` |
| `IWinMLStageWorkShare` | `d6b538b8-2cd8-4875-8647-5ca583f4bf0c` |
| `IWinMLCallerBoundStageOptions` | `02016154-17f3-4562-8331-b95973a922ba` |
| `IWinMLLlamaStageOptions` | `516a4e40-9d1a-4ef3-9d4e-fd6de35c5354` |
| `IWinMLStageSchema` | `41f17eb8-1b18-4eb6-b628-dd93d669f401` |
| `IWinMLStatefulStage` | `d058a861-844c-435f-b820-2f3dd06754e4` |
| `IWinMLStatefulStageOptions` | `6a2c9e2b-6a1f-4c3d-9b0e-6f6d6a2f7a5c` |
| `IWinMLSegmentedSequenceStage` | `0a7f6b3c-91d4-4258-8e0b-5c2d3f7a9e16` |
| `IWinMLMediaEncoder` | `8b1e0a47-3d92-4f6c-b5a8-1c9f2e6d4073` |
| `IWinMLSequenceSegmentLayout` | `2f5c4d18-7a63-4b9e-8c21-0d7e6a3f5b84` |
| `IWinMLPipeline` | `c22e40c2-4e8a-4403-bf98-a0d50ac099c9` |
| `IWinMLPipelineExecution` | `7de077a2-60ac-4748-babd-e09b0fd008d6` |
| `IWinMLPipelineIterationExecution` | `11a68236-b74b-47f7-a7c9-faf551792686` |
| `IWinMLFence` | `045746c4-bc82-4390-8886-aa5cf6ad1e0e` |
| `IWinMLProcessor` | `d4a3e8c1-5b72-4f90-a1d3-8c6e2f4a9b05` |
| `IWinMLD3D12Processor` | `7448b45c-f02d-4dc1-b9fe-4891a1bc087d` |
| `IWinMLResourceMapReader` | `b188cc3d-b051-453b-8651-ddeef3512a47` |
| `IWinMLStreamingResourceMapReader` | `35c88c22-4eb8-4f20-a9f2-f235d5aa8343` |
| `IWinMLCompileOutputSink` | `97c93853-b0d8-4afa-974f-518a3c21ade2` |
| `IWinMLTensor` | `22f5cf71-27be-4a1b-9087-1a36f7a230ad` |
| `IWinMLD3D12Tensor` | `37ac4145-5c22-4aa4-bdb2-d70f050849fb` |
| `IWinMLTensorSynchronization` | `f457998f-22d4-48bf-aba0-a75ae59ab666` |
| `IWinMLTensorHostReadback` | `63bc927b-9368-4095-b000-5a094fc23151` |
| `IWinMLTensorDataLock` | `7d2780ed-66b4-4fa5-a935-bae6d28082bd` |
| `IWinMLTensorSynchronizedDataLock` | `c1754906-62ce-46a2-a2fb-cc13cd10ffcd` |
| `IWinMLMutableTensor` | `a7c3e1d4-6f82-4b59-9e1a-3d8c5f2a7b94` |
| `IWinMLRawTensorFactory` | `b1c61714-e327-445e-b524-33e46e986fc0` |
| `IWinMLImageTensorFactory` | `58c7f0e1-0c74-4fa5-a949-9bbca2f72164` |
| `IWinMLAudioTensorFactory` | `f4b903e0-c0d8-410b-88ce-daef8054e582` |
| `IWinMLTextTensorFactory` | `4c69327b-62ed-40f7-84d4-dfdf8df2a3ba` |
| `IWinMLTensorProcessorFactory` | `63d93c2c-0899-4da8-a480-080b0bd3aef8` |
| `IWinMLTokenizer` | `a7c1d5e3-9f48-4b62-8d1a-3e5c7f2b0a94` |
| `IWinMLTokenizerDecoder` | `d9479bde-aca5-4d03-83e2-03eee893bb7d` |
| `IWinMLStructuredConversationFormatter` | `83625e5f-1f96-4a7e-beb2-57ec481474cc` |
| `IWinMLConversationFormatResult` | `e91e07c1-f82c-4474-902a-f025838ab01e` |
| `IWinMLConversationFormatResultTokenizerIdentity` | `81bf9afd-abf2-417b-9c89-9d7ddc26719a` |
| `IWinMLConversationOutputParser` | `c1b63305-af7d-49e2-9c24-c58ca98c2503` |
| `IWinMLConversationOutputEvent` | `f2826328-b0b7-489b-8c90-2ff83b8f6e15` |
| `IWinMLTokenizerConstraintFactory` | `843bd8ba-1797-4f47-917a-320bd1fb4ddc` |
| `IWinMLTokenConstraint` | `4fff037c-1d7f-40df-9026-b6ed7c71c395` |
| `IWinMLTokenConstraintGreedySelector` | `a0358905-2a3c-42dc-9e0b-5382b90a6ac4` |
| `IWinMLTokenizerMetadata` | `c3987705-286b-4396-b0a0-49640385dd91` |
| `IWinMLTaskRuntimeIdentity` | `d666f197-8e5f-4815-a768-a637499b49e5` |
| `IWinMLTasks` | `ff52923a-26c1-4c72-8e02-97a8b340eb15` |
| `IWinMLCancellationSource` | `73d2a066-e0df-44e2-b6d4-176fda0bd38f` |
| `IWinMLTextGenerationTaskFactory` | `4692aed4-6ba5-4462-abca-23606474ea5f` |
| `IWinMLTextGenerationOptions` | `839c9965-9f7b-4ae8-9eb0-3aba860ae4a4` |
| `IWinMLTextGenerationConfiguration` | `bccfa37d-bdbf-45dd-a05e-bee588569883` |
| `IWinMLModelTextGenerationConfiguration` | `984e9699-2339-4c7c-88ea-c1b770945ad0` |
| `IWinMLTextGenerationTask` | `a27d1316-84a7-4c75-9f66-08f0f2a73a04` |
| `IWinMLTextGenerationSession` | `d7757415-badb-448f-832d-1dcd7aed2e68` |
| `IWinMLTextGenerationSequenceDisclosure` | `0c1b7e58-5a2d-4f96-8a34-2d6f9e1c4b70` |
| `IWinMLTextGenerationConstraintSession` | `4929bad7-6223-41f5-8ef7-217d5b811749` |
| `IWinMLTextGenerationConstraintContinuation` | `bb0f6a13-9c47-4e8a-b52e-71d3f0a86c19` |
| `IWinMLTextGenerationPullStream` | `c3ae34bb-44e4-4262-bf8f-db434def63ff` |
| `IWinMLTextGenerationResult` | `8b73a588-f47f-46f7-9a17-d9600f2bff83` |
| `IWinMLTextGenerationSpeculativeConfiguration` | `5d0e7c4a-2f1b-4a86-9c3e-7b4d8e1f6a52` |
| `IWinMLTextGenerationSpeculativeSupport` | `37825096-9132-47a6-8cc4-63a9d7d31909` |
| `IWinMLTextGenerationSpeculativeResult` | `9a4f1c6e-3b7d-4e25-8f0a-2c6d5e9b1f47` |
| `IWinMLTextGenerationWatermarkConfiguration` | `3c6e9d2a-71b4-4f0e-a5d8-2e9b6f1c4a73` |
| `IWinMLTextGenerationWatermarkResult` | `b84f2e17-5d9a-4c63-8e1b-6a0f3d7c2e95` |
| `IWinMLTextGenerationWatermarkDetector` | `bb763a50-51f2-4a7b-b241-ccee47e3a17a` |
| `IWinMLChatCompletionTaskFactory` | `a9862625-4169-44fa-a753-eb8fb375ac82` |
| `IWinMLChatCompletionTask` | `d316e3c4-f994-4923-a0b7-a7ae051fbdf2` |
| `IWinMLChatCompletionSession` | `5220796d-7ee9-4999-bcde-a4d5b2fc5585` |
| `IWinMLChatCompletionPullStream` | `1dde0af6-e1e4-4d06-ac96-687b8d47b39e` |
| `IWinMLChatCompletionResult` | `db097042-aba4-49a6-8776-41674eea2ebd` |
| `IWinMLChatCompletionEventProvenance` | `d51fb276-e76a-4ae7-bffd-eb1281fa2627` |
| `IWinMLAutomaticSpeechRecognitionTaskFactory` | `249c4554-2f03-4625-9147-601b271bee71` |
| `IWinMLAutomaticSpeechRecognitionConfiguration` | `672e7f18-75de-4cbd-8194-33122f55b795` |
| `IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration` | `ca2d4314-63e0-4b6a-a9aa-f928addb4d38` |
| `IWinMLModelAutomaticSpeechRecognitionConfiguration` | `3b6932a2-a570-4b23-9781-78b6ae4c7071` |
| `IWinMLAutomaticSpeechRecognitionTask` | `7bab82df-7026-45e0-898d-53c40e3962a8` |
| `IWinMLAutomaticSpeechRecognitionSession` | `b0ce1ef5-1778-4448-87b4-b800ea45dbcd` |
| `IWinMLAutomaticSpeechRecognitionEncoderInput` | `db6df926-1c60-45da-91ea-a92c81d13095` |
| `IWinMLAutomaticSpeechRecognitionLiveSession` | `3cc89611-57fb-46bf-8d48-e4092763a7cc` |
| `IWinMLAutomaticSpeechRecognitionLiveInput` | `eef894c7-7861-43bf-92e4-5899e5439f9a` |
| `IWinMLAutomaticSpeechRecognitionPullStream` | `9ed7fe5e-0730-4890-a956-7b9c2f5c697b` |
| `IWinMLAutomaticSpeechRecognitionResult` | `4f8b347d-8c8d-4d18-a50c-8b4615773208` |
| `IWinMLDecisionTaskFactory` | `a87f6ca1-2a69-43ac-baa2-56c6508d8edd` |
| `IWinMLDecisionTask` | `18856433-4413-4ae1-8291-ab62cd3908ea` |
| `IWinMLDecisionSession` | `21f65408-39b3-4244-85f1-ad0707fbafcb` |
| `IWinMLDecisionResult` | `e3ef04d2-61e7-47a2-a41b-fce6f9f59bc8` |
| `IWinMLOrtModelSchema` | `9451e992-e86b-411a-a4c0-f83d2a0e2a22` |
| `IWinMLOrtNamedBindings` | `dc39b5f1-ba7a-48f1-b6d3-a3eebbf4b352` |
| `IWinMLOrtCompatibility` | `a7a04319-3b8a-496c-af93-96de30311f82` |
| `IWinMLOrtStageDiagnostics` | `9a146b25-52ae-4e21-b62b-005e5798e8eb` |
| `IWinMLOrtStageOptions` | `dd8497cb-a415-49cd-89bc-75aa61516e8a` |
| `IWinMLServer` | `868a81c0-6c75-4ad6-a3b2-433a45a62892` |
| `IWinMLServerModelContext` | `4a1c8e07-93b6-4f52-8d21-6e5c07b4a938` |
| `IWinMLServerModelFormatting` | `cd628963-af51-4579-8001-c82851db6bfc` |
| `IWinMLServerAccessKey` | `9d3f6b2a-5c47-4e18-b0a9-2f7c8e61d4b5` |
| `IWinMLServerFactory` | `eee7d40f-ff12-41a1-8116-9388942f1a8c` |

---

## Reference Pages

Select an interface or shared reference page below.

### Shared Reference Pages

| Page | Description |
|---|---|
| [Factory Functions](FactoryFunctions.md) | DLL-exported entry points. |
| [Enumerations](Enumerations.md) | Runtime enums and flag values. |
| [Structures](Structures.md) | ABI structs used across runtime, pipeline, and tensor APIs. |
| [Error Codes](ErrorCodes.md) | Common HRESULTs surfaced by the runtime and backends. |
| [Common Patterns](CommonPatterns.md) | Focused usage examples and labeled application outlines. |

### Interface Reference Pages

Each public interface from the headers is listed exactly once. Some related interfaces share a family reference page.

| Interface | Page |
|---|---|
| `IWinMLRuntime` | [IWinMLRuntime](IWinMLRuntime.md) |
| `IWinMLLlamaModelLoader` | [IWinMLLlamaModelLoader](IWinMLLlamaModelLoader.md) |
| `IWinMLModel` | [IWinMLModel](IWinMLModel.md) |
| `IWinMLModelSchema` | [IWinMLModelSchema](IWinMLModelSchema.md) |
| `IWinMLModelWeightFormats` | [IWinMLModelWeightFormats](IWinMLModelWeightFormats.md) |
| `IWinMLModelWeightFormat` | [IWinMLModelWeightFormats](IWinMLModelWeightFormats.md) |
| `IWinMLPipelineBuilder` | [IWinMLPipelineBuilder](IWinMLPipelineBuilder.md) |
| `IWinMLStage` | [IWinMLStage](IWinMLStage.md) |
| `IWinMLStageHostBuffers` | [IWinMLStageHostBuffers](IWinMLStageHostBuffers.md) |
| `IWinMLStageExecutionDiagnostics` | [IWinMLStageExecutionDiagnostics](IWinMLStageExecutionDiagnostics.md) |
| `IWinMLStageWorkShare` | [IWinMLStageExecutionDiagnostics](IWinMLStageExecutionDiagnostics.md) |
| `IWinMLCallerBoundStageOptions` | [IWinMLCallerBoundStageOptions](IWinMLCallerBoundStageOptions.md) |
| `IWinMLLlamaStageOptions` | [IWinMLLlamaModelLoader](IWinMLLlamaModelLoader.md#iwinmlllamastageoptions) |
| `IWinMLStageSchema` | [IWinMLStageSchema](IWinMLStageSchema.md) |
| `IWinMLStatefulStage` | [IWinMLStatefulStage](IWinMLStatefulStage.md) |
| `IWinMLStatefulStageOptions` | [IWinMLStatefulStageOptions](IWinMLStatefulStageOptions.md) |
| `IWinMLPipeline` | [IWinMLPipeline](IWinMLPipeline.md) |
| `IWinMLPipelineExecution` | [IWinMLPipelineExecution](IWinMLPipelineExecution.md) |
| `IWinMLPipelineIterationExecution` | [IWinMLPipelineIterationExecution](IWinMLPipelineIterationExecution.md) |
| `IWinMLFence` | [IWinMLFence](IWinMLFence.md) |
| `IWinMLExecutionTarget` | [IWinMLExecutionTarget](IWinMLExecutionTarget.md) |
| `IWinMLD3D12ExecutionTarget` | [IWinMLD3D12ExecutionTarget](IWinMLD3D12ExecutionTarget.md) |
| `IWinMLModelCompileOptions` | [IWinMLModelCompiler](IWinMLModelCompiler.md) |
| `IWinMLOnnxSymbolicDimensionOverrides` | [IWinMLModelCompiler](IWinMLModelCompiler.md) |
| `IWinMLModelCompiler` | [IWinMLModelCompiler](IWinMLModelCompiler.md) |
| `IWinMLResourceMapReader` | [IWinMLResourceMapReader](IWinMLResourceMapReader.md) |
| `IWinMLStreamingResourceMapReader` | [IWinMLStreamingResourceMapReader](IWinMLStreamingResourceMapReader.md) |
| `IWinMLCompileOutputSink` | [IWinMLCompileOutputSink](IWinMLCompileOutputSink.md) |
| `IWinMLProcessor` | [IWinMLProcessor](IWinMLProcessor.md) |
| `IWinMLD3D12Processor` | [IWinMLD3D12Processor](IWinMLD3D12Processor.md) |
| `IWinMLTensorProcessorFactory` | [IWinMLTensorProcessorFactory](IWinMLTensorProcessorFactory.md) |
| `IWinMLSequenceSegmentLayout` | [IWinMLSequenceSegmentLayout](IWinMLSequenceSegmentLayout.md) |
| `IWinMLMediaEncoder` | [IWinMLMediaEncoder](IWinMLMediaEncoder.md) |
| `IWinMLSegmentedSequenceStage` | [IWinMLSegmentedSequenceStage](IWinMLSegmentedSequenceStage.md) |
| `IWinMLTensorDataLock` | [IWinMLTensorDataLock](IWinMLTensorDataLock.md) |
| `IWinMLTensorSynchronizedDataLock` | [IWinMLTensorSynchronizedDataLock](IWinMLTensorSynchronizedDataLock.md) |
| `IWinMLTensor` | [IWinMLTensor](IWinMLTensor.md) |
| `IWinMLD3D12Tensor` | [IWinMLD3D12Tensor](IWinMLD3D12Tensor.md) |
| `IWinMLTensorSynchronization` | [IWinMLTensorSynchronization](IWinMLTensorSynchronization.md) |
| `IWinMLTensorHostReadback` | [IWinMLTensorHostReadback](IWinMLTensorHostReadback.md) |
| `IWinMLMutableTensor` | [IWinMLMutableTensor](IWinMLMutableTensor.md) |
| `IWinMLRawTensorFactory` | [IWinMLRawTensorFactory](IWinMLRawTensorFactory.md) |
| `IWinMLImageTensorFactory` | [IWinMLImageTensorFactory](IWinMLImageTensorFactory.md) |
| `IWinMLAudioTensorFactory` | [IWinMLAudioTensorFactory](IWinMLAudioTensorFactory.md) |
| `IWinMLTextTensorFactory` | [IWinMLTextTensorFactory](IWinMLTextTensorFactory.md) |
| `IWinMLTokenizer` | [IWinMLTokenizer](IWinMLTokenizer.md) |
| `IWinMLTokenizerDecoder` | [IWinMLTokenizerDecoder](IWinMLTokenizerDecoder.md) |
| `IWinMLTokenizerMetadata` | [IWinMLTokenizerMetadata](IWinMLTokenizerMetadata.md) |
| `IWinMLStructuredConversationFormatter` | [IWinMLStructuredConversationFormatter](IWinMLStructuredConversationFormatter.md) |
| `IWinMLConversationFormatResult` | [IWinMLStructuredConversationFormatter](IWinMLStructuredConversationFormatter.md) |
| `IWinMLConversationFormatResultTokenizerIdentity` | [IWinMLStructuredConversationFormatter](IWinMLStructuredConversationFormatter.md) |
| `IWinMLTokenizerConstraintFactory` | [IWinMLTokenizerConstraintFactory](IWinMLTokenizerConstraintFactory.md) |
| `IWinMLTokenConstraint` | [IWinMLTokenizerConstraintFactory](IWinMLTokenizerConstraintFactory.md) |
| `IWinMLTokenConstraintGreedySelector` | [IWinMLTokenizerConstraintFactory](IWinMLTokenizerConstraintFactory.md) |
| `IWinMLConversationOutputParser` | [IWinMLStructuredConversationFormatter](IWinMLStructuredConversationFormatter.md) |
| `IWinMLConversationOutputEvent` | [IWinMLStructuredConversationFormatter](IWinMLStructuredConversationFormatter.md) |
| `IWinMLCancellationSource` | [IWinMLTasks](IWinMLTasks.md) |
| `IWinMLTaskRuntimeIdentity` | [IWinMLTasks](IWinMLTasks.md) |
| `IWinMLTasks` | [IWinMLTasks](IWinMLTasks.md) |
| `IWinMLTextGenerationTaskFactory` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationOptions` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationConfiguration` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLModelTextGenerationConfiguration` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md#sessions-from-a-model) |
| `IWinMLTextGenerationTask` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationSession` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationSequenceDisclosure` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationConstraintSession` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationConstraintContinuation` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationPullStream` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationResult` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationSpeculativeConfiguration` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationSpeculativeSupport` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md#speculative-method-support) |
| `IWinMLTextGenerationSpeculativeResult` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) |
| `IWinMLTextGenerationWatermarkConfiguration` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md#watermarking) |
| `IWinMLTextGenerationWatermarkResult` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md#watermarking) |
| `IWinMLTextGenerationWatermarkDetector` | [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md#watermarking) |
| `IWinMLChatCompletionTaskFactory` | [IWinMLChatCompletionTask](IWinMLChatCompletionTask.md) |
| `IWinMLChatCompletionTask` | [IWinMLChatCompletionTask](IWinMLChatCompletionTask.md) |
| `IWinMLChatCompletionSession` | [IWinMLChatCompletionTask](IWinMLChatCompletionTask.md) |
| `IWinMLChatCompletionPullStream` | [IWinMLChatCompletionTask](IWinMLChatCompletionTask.md) |
| `IWinMLChatCompletionResult` | [IWinMLChatCompletionTask](IWinMLChatCompletionTask.md) |
| `IWinMLChatCompletionEventProvenance` | [IWinMLChatCompletionTask](IWinMLChatCompletionTask.md) |
| `IWinMLAutomaticSpeechRecognitionTaskFactory` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLAutomaticSpeechRecognitionConfiguration` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md#encoder-decoder-configuration) |
| `IWinMLModelAutomaticSpeechRecognitionConfiguration` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md#models-backed-configuration) |
| `IWinMLAutomaticSpeechRecognitionTask` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLAutomaticSpeechRecognitionSession` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLAutomaticSpeechRecognitionEncoderInput` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLAutomaticSpeechRecognitionLiveSession` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLAutomaticSpeechRecognitionLiveInput` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLAutomaticSpeechRecognitionPullStream` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLAutomaticSpeechRecognitionResult` | [IWinMLAutomaticSpeechRecognitionTask](IWinMLAutomaticSpeechRecognitionTask.md) |
| `IWinMLDecisionTaskFactory` | [IWinMLDecisionTask](IWinMLDecisionTask.md) |
| `IWinMLDecisionTask` | [IWinMLDecisionTask](IWinMLDecisionTask.md) |
| `IWinMLDecisionSession` | [IWinMLDecisionTask](IWinMLDecisionTask.md) |
| `IWinMLDecisionResult` | [IWinMLDecisionTask](IWinMLDecisionTask.md) |
| `IWinMLOrtModelSchema` | [IWinMLOrtModelSchema](IWinMLOrtModelSchema.md) |
| `IWinMLOrtNamedBindings` | [IWinMLOrtNamedBindings](IWinMLOrtNamedBindings.md) |
| `IWinMLOrtCompatibility` | [IWinMLOrtCompatibility](IWinMLOrtCompatibility.md) |
| `IWinMLOrtStageOptions` | [IWinMLOrtStageOptions](IWinMLOrtStageOptions.md) |
| `IWinMLOrtStageDiagnostics` | [IWinMLOrtStageDiagnostics](IWinMLOrtStageDiagnostics.md) |
| `IWinMLServer` | [IWinMLServer](IWinMLServer.md) |
| `IWinMLServerModelContext` | [IWinMLServer](IWinMLServer.md) |
| `IWinMLServerModelFormatting` | [IWinMLServer](IWinMLServer.md) |
| `IWinMLServerAccessKey` | [IWinMLServer](IWinMLServer.md) |
| `IWinMLServerFactory` | [IWinMLServer](IWinMLServer.md) |
