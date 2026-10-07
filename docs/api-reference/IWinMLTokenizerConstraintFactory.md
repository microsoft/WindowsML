<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTokenizerConstraintFactory

Obtain `IWinMLTokenizerConstraintFactory` from an `IWinMLTokenizer` with
`QueryInterface`. The interface is available when the tokenizer exposes the
vocabulary needed for constrained decoding.

Constraint creation requires an `IWinMLConversationFormatResult` produced by the
same tokenizer identity. A result from another tokenizer returns `E_INVALIDARG`.

## Vocabulary identity

`GetVocabularyIdentity` returns `WINML_CONSTRAINT_VOCABULARY_IDENTITY`, including
the token count and SHA-256 vocabulary identity used to validate constraint
compatibility.

## CreateConstraint

```cpp
HRESULT CreateConstraint(
    [in] IWinMLConversationFormatResult* formatResult,
    [out, retval] IWinMLTokenConstraint** constraint
);
```

Creates mutable constraint state for one generation. The method returns
`E_POINTER` for null required pointers. If the format result has no constraint,
it returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.

## IWinMLTokenConstraint

Constraint methods are bound to their creation thread and return
`RPC_E_WRONG_THREAD` from another thread.

| Method | Description |
|---|---|
| `GetVocabularyIdentity` | Returns the vocabulary identity used by the constraint. |
| `FilterCandidates` | Filters a candidate buffer in place to the candidates allowed by the current state. |
| `AcceptToken` | Advances the constraint with one selected token. |
| `IsStopToken` | Reports whether a token terminates constrained generation. |
| `IsPreservedToken` | Reports whether a stop token remains in generated output. |
| `GetState` | Returns active, completed, or poisoned state and state flags. |
| `Reset` | Restores the initial generation state. |

`FilterCandidates` compacts legal candidates in place without changing their
relative order. `candidateCapacity` and `candidates` remain caller-owned and are
not changed. On success, `candidateCount` receives the retained count. After a
structurally valid buffer is accepted for processing, failure sets
`candidateCount` to zero and the candidate contents are unspecified.

## IWinMLTokenConstraintGreedySelector

A constraint may expose `IWinMLTokenConstraintGreedySelector` through
`QueryInterface`.

```cpp
HRESULT SelectHighestAllowedCandidate(
    [in] UINT32 candidateCount,
    [in, size_is(candidateCount)]
        const WINML_TOKEN_CONSTRAINT_CANDIDATE* candidates,
    [out] UINT32* selectedTokenId
);
```

Selects the allowed candidate with the highest logit without advancing
constraint state. NaN and negative infinity are not selectable, positive
infinity is selectable, and equal logits select the lower token ID. Candidates
must be ordered by strictly increasing token ID and remain caller-owned and
unmodified. Invalid ordering or IDs return `E_INVALIDARG`; a null
`selectedTokenId` returns `E_POINTER`. If no candidate is allowed, the method
returns the same no-legal-token HRESULT as `FilterCandidates`.

For using a constraint with a generation session, see
[IWinMLTextGenerationTask](IWinMLTextGenerationTask.md#task-and-session).
