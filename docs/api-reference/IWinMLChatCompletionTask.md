<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLChatCompletionTask

`IWinMLChatCompletionTask` composes a tokenizer that exposes
`IWinMLStructuredConversationFormatter` with an `IWinMLTextGenerationSession`.
The caller owns conversation history and passes a complete
`WINML_CONVERSATION_REQUEST` for each generation.

Obtain `IWinMLChatCompletionTaskFactory` with
`winml::tasks::GetChatCompletionFactory`, or call
`winml::tasks::CreateChatCompletionTask` to create a task from the matching
tokenizer. `CreateSession` requires a text-generation session created with the
same Runtime and tokenizer identity; mismatches return `E_INVALIDARG` before a
chat session is created.

`Generate` formats the request, creates the output parser, creates a tokenizer
constraint when the formatted request contains one, and starts text generation.
Formatter, constraint, and text-generation failures are returned to the caller.
This does not promise unchanged text-generation state for failures after
generation has started.

## Typed image bindings

`IWinMLChatCompletionSession` owns `SetMediaEncoder`, `GetMediaEncoder`, and
`GenerateWithMedia` directly; no media-session query is required.
`SetMediaEncoder` retains the encoder for subsequent requests; null clears it.
`GetMediaEncoder` returns `S_OK` and null when no encoder is associated. A closed
session returns `E_NOT_VALID_STATE`.

The encoder must target the decoder used by the text-generation session:

```cpp
wil::com_ptr<IWinMLMediaEncoder> encoder;
THROW_IF_FAILED(textSession->CreateMediaEncoderFromFile(
    projectorPath, target.get(), encoder.put()));
THROW_IF_FAILED(chatSession->SetMediaEncoder(encoder.get()));
```

`WINML_CHAT_MEDIA_BINDING` contains `messageIndex`, `contentPartIndex`, and
`tensor` fields.

```cpp
HRESULT GenerateWithMedia(
    [in] const WINML_CONVERSATION_REQUEST* request,
    [in] UINT32 bindingCount,
    [in, size_is(bindingCount)] const WINML_CHAT_MEDIA_BINDING* bindings,
    [in] IWinMLTextGenerationOptions* options,
    [in] IWinMLCancellationSource* cancellation,
    [out, retval] IWinMLChatCompletionPullStream** stream);
```

Each binding identifies a content part by zero-based message and content-part
indices. That content part must have `type = L"image"` and null `text` and
`jsonValue`; pixels are supplied by the binding tensor. Duplicate locations,
out-of-range indices, null tensors, missing bindings, and non-image or malformed
image parts fail before generation.

A zero binding count uses the same path as `Generate` and does not require a
media encoder. Nonzero binding count requires an associated encoder; otherwise
`GenerateWithMedia` returns `E_NOT_VALID_STATE`.

Python exposes the same location identity as `ChatMediaBinding`:

```python
from windowsml.tasks import (
    ChatCompletionContentPart, ChatCompletionMessage, ChatCompletionRequest,
    ChatMediaBinding,
)

request = ChatCompletionRequest(messages=(
    ChatCompletionMessage("user", content_parts=(
        ChatCompletionContentPart("text", text="Describe this image."),
        ChatCompletionContentPart("image"),
    )),
))
encoder = session.text_generation_session.create_media_encoder_from_file(
    projector_path, target,
)
session.media_encoder = encoder
with session.stream_with_media(request, (
    ChatMediaBinding(message_index=0, content_part_index=1, tensor=image_tensor),
)) as stream:
    for event in stream:
        pass
    result = stream.result
```

Both task and session `stream_with_media` / `generate_with_media` accept an
iterable of bindings, not an ordinal tensor array.

## Sequence reuse and token counts

The chat session queries the text-generation session's sequence-disclosure view.
When the formatted prompt starts with the retained token sequence and has a
non-empty suffix, generation continues with that suffix. Otherwise, chat starts a
replacement generation for the full formatted prompt. The result content is the
same; only the amount of prompt work can differ.

`GetTokenCounts` reports the full formatted prompt and completion token counts.
`IWinMLChatCompletionResult::GetPromptCacheUse` returns
`WINML_CHAT_COMPLETION_PROMPT_CACHE`, including cache mode, full prompt count,
retained-prefix count, and prompt tokens evaluated for the completion.

For media prompts, `promptTokenCount` is the sequence-cell cost used by the text
generation session, not just the visible text-token count.

## Streaming and results

`IWinMLChatCompletionPullStream::ReadNext` returns
`IWinMLConversationOutputEvent` objects for content, reasoning, tool-call,
completion, and error events. Query a stream event for
`IWinMLChatCompletionEventProvenance` to retrieve source completion-token range
and token IDs. Event strings are borrowed for the event lifetime and must not be
modified or freed.

Pass an `IWinMLCancellationSource` to `Generate` or `GenerateWithMedia` and call
`Cancel` from any thread to request cancellation. Chat-initiated termination
does not cancel the caller-owned source.

`IWinMLChatCompletionResult` reports final content, reasoning, tool calls,
prompt and completion token counts, finish reason, terminal HRESULT, prompt-cache
use, and the underlying text-generation result.

## Threading and teardown

The factory and task impose no creation-thread check. `CreateSession` must run
on the supplied text-generation session's creation thread; the new chat session
belongs to that thread. Session and stream methods use their creation thread and
return `RPC_E_WRONG_THREAD` from another thread.

Session and stream `Close` run on their creation thread. Repeating a successful
`Close` returns `S_OK`. Session `Close` does not close outstanding streams or the
caller-owned text-generation session. After stream closure, `ReadNext` and
`GetResult` return `E_NOT_VALID_STATE`; retrieve the terminal result before
closing the stream if it is needed.

Callers may bypass chat composition and use the tokenizer and Text Generation
Task APIs directly. The chat task does not load models, discover artifacts,
select targets or backends, execute tools, retry requests, retain history, or
persist results.
