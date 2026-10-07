<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Task and Runtime lifecycle

Windows ML Tasks are typed layers over application-owned Runtime objects. A Task
does not hide model placement: the application still creates the Runtime,
execution targets, models, stages, pipelines, tokenizers, and tensors. The Task
adds model-specific configuration and a streaming session API.

## Construction

1. Create one `IWinMLRuntime`.
2. Load source models or compiled artifacts with the Runtime.
3. Create execution targets and build the required pipelines.
4. Create `IWinMLTasks` from the same Runtime.
5. Configure a typed Task with the pipelines, stages, tokenizer, tensor target,
   and model-specific bindings.
6. Validate the configuration and create or use its session.

All objects supplied to one Task must belong to the same Runtime. The samples
query `IWinMLTaskRuntimeIdentity` to make this ownership rule visible.

## Model artifacts

The Runtime uses one model-loading API for source and backend-native artifacts.
The Runtime selects the backend from the loaded artifact type: `.onnx` and
`.ort` use ONNX Runtime, while `.gguf` uses the llama.cpp backend. The sample
scripts also validate that the requested backend matches the model path they
pass to the Runtime.

After an ONNX model is converted to ORT format, the text-generation sample still
builds a Runtime pipeline and supplies the same logical token input, logits
output, and state tensor pair to the Task.

## Sessions, streams, and cancellation

A Task session starts one operation and returns a pull stream. Reading the
stream advances work and yields incremental text. Close the stream when the
operation is finished.

Text generation starts from prompt text (`GenerateText`) or from prompt token
IDs (`GenerateTokens`). The samples use token IDs for chat prompts: the
tokenizer's
[`IWinMLStructuredConversationFormatter`](../api-reference/IWinMLStructuredConversationFormatter.md)
applies the model's chat template, so an instruct model answers and then ends
its turn.

After the stream reports completion, read its result. The result contains the
complete output, finish reason, token counts or transcript metadata, and any
operation error. The samples compare streamed fragments with the result text.

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
- [`IWinMLTextGenerationTask`](../api-reference/IWinMLTextGenerationTask.md)
- [`IWinMLAutomaticSpeechRecognitionTask`](../api-reference/IWinMLAutomaticSpeechRecognitionTask.md)
- [`IWinMLModelCompiler`](../api-reference/IWinMLModelCompiler.md)
- [`IWinMLTensor`](../api-reference/IWinMLTensor.md)
