<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Task and Runtime lifecycle

Windows ML Tasks are typed layers over application-owned Runtime objects. A Task
does not hide model placement: the application creates the Runtime, execution
targets, models, and tokenizers, and either gives the Task a loaded model or
builds the stages and pipelines that the Task runs. The Task adds
model-specific configuration and a streaming session API.

## Construction

Every Task starts the same way:

1. Create one `IWinMLRuntime`.
2. Create `IWinMLTasks` from the same Runtime.
3. Create the execution targets the application wants to use.

Then give the Task its model in one of three ways.

| Pattern | Steps | Tasks |
|---|---|---|
| Bring your own weights | Load a published model with `LoadModelFromPath` (a checkpoint folder) or `LoadModelFromFile` (a GGUF file), then call `SetModel` on the configuration, or `CreateDecisionTaskFromModel`. Windows ML plans it for the CGC backend, runs it on the GPU target the application picked and creates the model's tokenizer from its files when none is given. | Text generation, speech recognition, typed decisions |
| Bring your own model | Load the models the application exported, call `SetTokenizer`, and describe them with `SetComposition` and a `UNIFIED`, `EMBEDDING_DECODER`, or `PREFILL_DECODE` description. | Text generation |
| llama.cpp, an explicit choice | Load a GGUF file with `IWinMLLlamaModelLoader::LoadModelFromFile`, then call `SetModel`. A draft model loaded the same way enables speculative decoding. | Text generation |

For full control, build the pipelines yourself and configure the Task with the
pipelines, stages, tokenizer, tensor target, and model-specific bindings. Then
validate the configuration and create its session. The language, speech, and
composition samples use this path.

All objects supplied to one Task must belong to the same Runtime. The samples
query `IWinMLTaskRuntimeIdentity` to make this ownership rule visible.

## Model artifacts

The Runtime uses one model-loading API for source and backend-native artifacts.
How a model runs follows from the call that loaded it and how the Task gets it:

- In a pipeline that the application builds, `.onnx` and `.ort` models run on
  ONNX Runtime, and `.gguf` models run on llama.cpp when the llama.cpp payload is
  deployed. The sample scripts validate that the requested backend matches the
  model path they pass to the Runtime.
- A model given to `SetModel` or `CreateDecisionTaskFromModel` after
  `LoadModelFromPath` or `LoadModelFromFile` is run by Windows ML on the
  application's target. A model Windows ML does not support fails with
  `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`; Windows ML never switches it to
  another engine.
- A GGUF model loaded with `IWinMLLlamaModelLoader` runs on llama.cpp. This is
  the only way a Models-backed Task uses llama.cpp.

After an ONNX model is converted to ORT format, the text-generation sample still
builds a Runtime pipeline and supplies the same logical token input, logits
output, and state tensor pair to the Task.

## Sessions, streams, and cancellation

A Task session starts one operation and returns a pull stream. Reading the
stream advances work and yields incremental text. Close the stream when the
operation is finished. Sessions from a model are ordinary sessions, so the same
streaming, cancellation, and result code works for every pattern.

Text generation starts from prompt text (`GenerateText`) or from prompt token
IDs (`GenerateTokens`). The samples use token IDs for chat prompts: the
tokenizer's
[`IWinMLStructuredConversationFormatter`](../api-reference/IWinMLStructuredConversationFormatter.md)
applies the model's chat template, so an instruct model answers and then ends
its turn.

After the stream reports completion, read its result. The result contains the
complete output, finish reason, token counts or transcript metadata, and any
operation error. The samples compare streamed fragments with the result text.

A decision session has no stream: `Decide` evaluates the questions and returns
an immutable result with probabilities, confidence, and a recommendation.

Cancellation sources are created by `IWinMLTasks` and passed to the operation.
Cancellation is cooperative and is reported through the operation result; it is
not a substitute for closing the stream.

## Composition

The composition sample runs ASR to completion, combines an instruction with the
transcript, and sends that text as a chat turn to a separate text-generation
Task. The application owns that boundary and can validate, redact, persist, or
transform the transcript before invoking the next Task.

## API reference

- [Runtime API reference](../api-reference/README.md)
- [`IWinMLTasks`](../api-reference/IWinMLTasks.md)
- [`IWinMLTextGenerationTask`](../api-reference/IWinMLTextGenerationTask.md), including [sessions from a model](../api-reference/IWinMLTextGenerationTask.md#sessions-from-a-model)
- [`IWinMLAutomaticSpeechRecognitionTask`](../api-reference/IWinMLAutomaticSpeechRecognitionTask.md)
- [`IWinMLDecisionTask`](../api-reference/IWinMLDecisionTask.md)
- [`IWinMLLlamaModelLoader`](../api-reference/IWinMLLlamaModelLoader.md)
- [`IWinMLModelCompiler`](../api-reference/IWinMLModelCompiler.md)
- [`IWinMLTensor`](../api-reference/IWinMLTensor.md)