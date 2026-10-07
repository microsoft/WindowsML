<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Chat completion

This sample holds a two-turn conversation through the Chat Completion Task. It
asks a question, then asks a follow-up that only makes sense with the first
answer in context.

The Chat Completion Task composes the model's tokenizer with a Text Generation
session. Each request is a list of role-tagged messages. The Task applies the
model's own chat template, starts generation, and streams the reply as content
events. The application owns the conversation: it appends each reply to its
message list and sends the whole list on the next turn. See
[`IWinMLChatCompletionTask`](../../../../docs/api-reference/IWinMLChatCompletionTask.md)
and [Task and Runtime lifecycle](../../../../docs/Tasks/task-lifecycle.md).

The sample uses the text-generation sample's models: the exported Qwen Task
graph in `models\llm` for ONNX Runtime, or the GGUF model for llama.cpp. Both
backends use the same chat session, events, and result.

```powershell
..\..\check_artifacts.ps1 -Sample task-text-generation
..\..\build.ps1 -Sample chat-completion -PackageOnly
..\..\run_chat_completion.ps1 -Backend ort
```

Use `-Backend llama` to run the GGUF model, and `-Prompt` and `-FollowUp` to ask
different questions. The sample decodes greedily, so each run gives the same
replies.

```text
User: Name three primary colors.
Assistant: Three primary colors are red, blue, and yellow.
  Tokens: prompt=34 (reused=0) completion=11
  Finished because: end-of-sequence token
User: Which of those is the color of the sky?
Assistant: The color of the sky is blue.
  Tokens: prompt=65 (reused=45) completion=8
  Finished because: end-of-sequence token
```

## Prompt reuse

The second request starts with the same tokens as the first request and its
reply, which the session already holds. The Task evaluates only the new part of
the prompt, and `GetPromptCacheUse` reports how many prompt tokens it reused:
45 of the 65 in the second turn. When a request does not start with the held
tokens, the Task evaluates the whole prompt.

## Python

After [setting up Python](../../../../docs/Runtime/python-samples.md#set-up-python),
run the Python entry point from `Samples\Tasks`:

```powershell
python.exe .\language\chat-completion\main.py .\models\llm\task_model.onnx
```

Pass the `.gguf` model instead to run on llama.cpp.
