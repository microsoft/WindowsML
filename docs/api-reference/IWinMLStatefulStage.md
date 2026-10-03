<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLStatefulStage

> Part of the [WinML Runtime API Reference](README.md).

Sequence-state operations for a stage. Query this interface from `IWinMLStage`.
The query result can be cached; method calls report availability. Before Build,
these methods return `E_NOT_VALID_STATE`. When the built stage has no sequence
state support, they return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.

Use [`IWinMLStatefulStageOptions`](IWinMLStatefulStageOptions.md) before Build to
declare tensor state pairs, caller-owned state policy, and draft prediction.

```
IID: d058a861-844c-435f-b820-2f3dd06754e4
```

## `GetSequenceCapacity`

```cpp
HRESULT GetSequenceCapacity([out, retval] UINT64* capacity);
```

Returns the effective sequence capacity for the stage.

**Returns:** `S_OK` on success, `E_POINTER` for a null `capacity`,
`E_NOT_VALID_STATE` before Build, or a backend capability failure.

---

## `GetDeclaredSequenceCapacity`

```cpp
HRESULT GetDeclaredSequenceCapacity(
    [out] WINML_SEQUENCE_CAPACITY_DISCLOSURE* disclosure,
    [out] UINT64* capacity);
```

Returns the capacity value declared by the artifact, not necessarily the
effective capacity in use. If the stateful backend has no declared value to
report, the method succeeds with `WINML_SEQUENCE_CAPACITY_DISCLOSURE_UNKNOWN` and
`capacity == 0`.

**Returns:** `S_OK` on success, `E_POINTER` for a null output pointer,
`E_NOT_VALID_STATE` before Build, or `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`
when the built stage has no stateful backend.

---

## `GetSequencePosition`

```cpp
HRESULT GetSequencePosition([out, retval] UINT64* position);
```

Returns the current sequence position for the stage.

**Returns:** `S_OK` on success, `E_POINTER` for a null `position`,
`E_NOT_VALID_STATE` before Build, or a backend capability failure.

---

## `RewindTo`

```cpp
HRESULT RewindTo([in] UINT64 position);
```

Asks the backend to rewind sequence state to `position`. If the rewind is
refused, state is left at its previous position.

**Returns:** `S_OK` on success, `E_NOT_VALID_STATE` before Build,
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the backend has no sequence state,
or a backend rewind failure such as `E_INVALIDARG` for an unsupported position.

---

## `SetSequenceCapacity`

```cpp
HRESULT SetSequenceCapacity([in] UINT64 capacity);
```

Requests a new sequence capacity after Build. Set capacity before first execution
to size the sequence without discarding state. Changing capacity after execution
has begun resets runtime-managed sequence state: the position returns to zero and
previously decoded tokens are dropped.

**Returns:** `S_OK` on success, `E_INVALIDARG` for invalid capacity values on a
backend path that validates them, `E_NOT_VALID_STATE` before Build, or
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the backend does not support
capacity changes.

---

## `GetSpeculativeCapabilities`

```cpp
HRESULT GetSpeculativeCapabilities(
    [out, retval] WINML_SPECULATIVE_CAPABILITIES* capabilities);
```

Returns speculative decoding capabilities for the built stage. Zero widths mean
multi-position verification is unavailable.

**Returns:** `S_OK` on success, `E_POINTER` for a null `capabilities`,
`E_NOT_VALID_STATE` before Build, or `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`
when the backend does not expose speculative operations.

---

## `StepAllPositions`

```cpp
HRESULT StepAllPositions(
    [in, size_is(tokenCount)] const INT32* tokens,
    [in] UINT32 tokenCount,
    [out, size_is(logitCapacityElements)] FLOAT* logits,
    [in] UINT64 logitCapacityElements,
    [out] UINT32* rowsWritten,
    [out] UINT32* rowStride);
```

Appends `tokenCount` tokens and writes one logits row per token, when the backend
supports this operation. On success, `rowsWritten == tokenCount`, `rowStride` is
the per-row element count, and row `i` contains the distribution after consuming
token `i`. Any failure leaves the sequence position unchanged.

**Returns:** `S_OK` on success, `E_POINTER` for a null `rowsWritten` or
`rowStride`, `E_NOT_VALID_STATE` before Build,
`HRESULT_FROM_WIN32(ERROR_BUSY)` if the pipeline is already executing,
`HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)` when the logits buffer is too
small, or a backend speculative-step failure.

---

## `ProposeDraftTokens`

```cpp
HRESULT ProposeDraftTokens(
    [in] INT32 pendingToken,
    [in] UINT32 maxTokenCount,
    [out, size_is(maxTokenCount), length_is(*tokenCount)] INT32* tokens,
    [out] UINT32* tokenCount);
```

Proposes up to `maxTokenCount` tokens expected to follow `pendingToken`, the
sampled token the stage has not yet consumed, using the stage's draft predictor.
The call does not advance the sequence. Pass `pendingToken` and the proposals to
`StepAllPositions`, then keep the accepted prefix with `CommitAcceptedPrefix`.
Returning fewer tokens than requested, including zero, is valid; for example,
the predictor may have nothing to propose after a `RewindTo` until the sequence
is rebuilt.

**Returns:** `S_OK` on success, `E_POINTER` for a null `tokenCount` or a null
`tokens` with a nonzero `maxTokenCount`, `E_NOT_VALID_STATE` before Build,
`HRESULT_FROM_WIN32(ERROR_BUSY)` if the pipeline is already executing, or
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the stage has no draft predictor
(`maxDraftTokens` is zero).

---

## `CommitAcceptedPrefix`

```cpp
HRESULT CommitAcceptedPrefix(
    [in] UINT32 acceptedCount,
    [out, retval] UINT64* position);
```

Commits the first `acceptedCount` tokens from the immediately preceding
`StepAllPositions` call, discards the rest, and returns the resulting sequence
position. `acceptedCount` can be zero or the full step width. A larger count is
`E_INVALIDARG`.

The pending step is invalidated by any intervening token execution, embedding
segment execution, explicit rewind, reset, or capacity change. If commit is
refused, the sequence position is unchanged.

**Returns:** `S_OK` on success, `E_POINTER` for a null `position`,
`E_INVALIDARG` for an accepted count larger than the pending step width,
`E_NOT_VALID_STATE` before Build or when no valid pending step exists,
`HRESULT_FROM_WIN32(ERROR_BUSY)` if the pipeline is already executing, or a
backend commit failure.

## Types

`WINML_SPECULATIVE_CAPABILITIES` contains `maxProposedStepWidth`,
`maxLogitRowsPerStep`, `rollbackBound`, `maxRollbackDepth`, `residency`, and
`maxDraftTokens`. `maxDraftTokens` is the most tokens one `ProposeDraftTokens`
call returns; zero means the stage has no draft predictor.

`WINML_SPECULATIVE_ROLLBACK_BOUND` values are `UNKNOWN`, `UNSUPPORTED`,
`UNBOUNDED`, and `BOUNDED`.

`WINML_SPECULATIVE_RESIDENCY` values are `UNKNOWN`, `HOST`, and
`DEVICE_WITH_HOST_READBACK`.

### Example

```cpp
wil::com_ptr<IWinMLStatefulStage> stateful;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(stateful.put())));

UINT64 position = 0;
THROW_IF_FAILED(stateful->GetSequencePosition(&position));

WINML_SPECULATIVE_CAPABILITIES capabilities{};
THROW_IF_FAILED(stateful->GetSpeculativeCapabilities(&capabilities));
```
