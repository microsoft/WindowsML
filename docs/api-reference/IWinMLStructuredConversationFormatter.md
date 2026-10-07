<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLStructuredConversationFormatter

Obtain this interface from an `IWinMLTokenizer` with `QueryInterface`.
`QueryInterface` returns `E_NOINTERFACE` when the tokenizer has no chat
template.

```
IID: 83625e5f-1f96-4a7e-beb2-57ec481474cc
Header: WinMLConversation.h
```

## `FormatConversation`

```cpp
HRESULT FormatConversation(
    [in] const WINML_CONVERSATION_REQUEST* request,
    [out, retval] IWinMLConversationFormatResult** result
);
```

Formats a structured conversation into an immutable result object that contains
token IDs, generation-prompt metadata, additional stops, parser metadata, and an
optional constraint request. The method returns `E_POINTER` for null required
pointers and `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when structured
formatting is unavailable for the tokenizer.

`WINML_CONVERSATION_REQUEST` supports roles, typed content, tool calls, response
schemas, template values, thinking preferences, continuation, and caller-supplied
time fields. Unsupported request fields or template capabilities fail the
operation rather than being ignored. The request remains caller-owned and is not
retained after `FormatConversation` returns; the result owns any data needed
afterward.

### Result ownership

`IWinMLConversationFormatResult` owns the formatted data and can create an
`IWinMLConversationOutputParser`. Result objects also expose
`IWinMLConversationFormatResultTokenizerIdentity`; constraint creation rejects
results from another tokenizer.

Strings returned by result and event getters are borrowed for the owning
object's lifetime and must not be modified or freed. Buffers returned by result
getters remain caller-owned as documented by the method signature.
