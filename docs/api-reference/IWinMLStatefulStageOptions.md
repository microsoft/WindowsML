<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLStatefulStageOptions

> Part of the [WinML Runtime API Reference](README.md).

Construction-time options for a model stage that may use sequence state. Query
this interface from `IWinMLStage` before `IWinMLPipelineBuilder::Build`.
Setters return `E_NOT_VALID_STATE` after Build has consumed the stage.

```
IID: 6a2c9e2b-6a1f-4c3d-9b0e-6f6d6a2f7a5c
```

## `SetSequenceCapacityHint`

```cpp
HRESULT SetSequenceCapacityHint([in] UINT64 capacity);
```

Sets the capacity hint passed to the backend during Build. Zero, the default,
means no hint. A backend that does not support the hint ignores it rather than
failing Build.

**Returns:** `S_OK` on success or `E_NOT_VALID_STATE` after Build.

---

## `GetSequenceCapacityHint`

```cpp
HRESULT GetSequenceCapacityHint([out, retval] UINT64* capacity);
```

Returns the stored capacity hint.

**Returns:** `S_OK` on success or `E_POINTER` for a null `capacity`.

---

## `SetCallerOwnsStateTensors`

```cpp
HRESULT SetCallerOwnsStateTensors([in] BOOL value);
```

Controls whether declared state tensor pairs remain caller-bound instead of being
managed by the runtime. The default value is `FALSE`.

Build fails with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` if caller-owned state
is requested and the resolved execution path cannot expose its state tensors for
caller binding. Shared storage alone does not declare persistent state; declare
each state input/output pair with `AddStateTensorPair`.

When caller-owned state is enabled, binding a state input without binding its
paired output keeps that state in place across executions. Bind the paired output
only when rotating to a different resource. If a declared state output is omitted,
the runtime uses the paired input tensor for that output when possible. If the
backend produces a different tensor, the runtime copies the result back to the
paired input and reports that input tensor as the output.

**Returns:** `S_OK` on success, `E_INVALIDARG` for an incompatible option
combination, or `E_NOT_VALID_STATE` after Build.

---

## `GetCallerOwnsStateTensors`

```cpp
HRESULT GetCallerOwnsStateTensors([out, retval] BOOL* value);
```

Returns whether caller-owned state was requested.

**Returns:** `S_OK` on success or `E_POINTER` for a null `value`.

---

## `AddStateTensorPair`

```cpp
HRESULT AddStateTensorPair([in] UINT32 inputIndex, [in] UINT32 outputIndex);
```

Declares one state input/output pair by positional index. Each input index and
each output index can appear in at most one pair. Undeclared tensors remain
regular model inputs and outputs.

**Returns:** `S_OK` on success, `E_INVALIDARG` for a duplicate pair member or an
incompatible option combination, or `E_NOT_VALID_STATE` after Build.

---

## `ClearStateTensorPairs`

```cpp
HRESULT ClearStateTensorPairs();
```

Removes all declared state pairs.

**Returns:** `S_OK` on success or `E_NOT_VALID_STATE` after Build.

---

## `GetStateTensorPairCount`

```cpp
HRESULT GetStateTensorPairCount([out, retval] UINT32* count);
```

Returns the number of declared state pairs.

**Returns:** `S_OK` on success, `E_POINTER` for a null `count`, or
`HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW)` if the stored count cannot fit in
`UINT32`.

---

## `GetStateTensorPair`

```cpp
HRESULT GetStateTensorPair(
    [in] UINT32 pairIndex,
    [out] UINT32* inputIndex,
    [out] UINT32* outputIndex);
```

Returns the declared pair at `pairIndex`.

**Returns:** `S_OK` on success, `E_POINTER` for a null output pointer, or
`E_BOUNDS` for an out-of-range `pairIndex`.

---

## `SetDraftTokenLimitHint`

```cpp
HRESULT SetDraftTokenLimitHint([in] UINT32 count);
```

Sets the most draft tokens a caller verifies after the pending token in one
`IWinMLStatefulStage::StepAllPositions` call. A backend uses the hint to size the
state it retains for rolling back rejected draft tokens, which a recurrent model
needs before its execution context is created. Zero, the default, means the
caller does not decode speculatively. A backend that needs no such sizing
ignores the hint. The llama.cpp backend uses at most 16.

**Returns:** `S_OK` on success or `E_NOT_VALID_STATE` after Build.

---

## `GetDraftTokenLimitHint`

```cpp
HRESULT GetDraftTokenLimitHint([out, retval] UINT32* count);
```

Returns the stored draft token limit hint.

**Returns:** `S_OK` on success or `E_POINTER` for a null `count`.

---

## `SetModelDraftPredictorEnabled`

```cpp
HRESULT SetModelDraftPredictorEnabled([in] BOOL enabled);
```

Loads and runs the draft predictor built into the model artifact, such as
multi-token prediction (NextN) layers in a GGUF file, so
`IWinMLStatefulStage::ProposeDraftTokens` can propose up to the draft token limit
per call. The predictor observes every token the stage consumes, so it must be
enabled before Build. An artifact without a built-in predictor builds normally
and reports a zero `maxDraftTokens` capability. The default is `FALSE`.

Build fails with `E_INVALIDARG` when this is `TRUE` and the draft token limit
hint is zero.

**Returns:** `S_OK` on success or `E_NOT_VALID_STATE` after Build.

---

## `GetModelDraftPredictorEnabled`

```cpp
HRESULT GetModelDraftPredictorEnabled([out, retval] BOOL* enabled);
```

Returns whether the model draft predictor was requested.

**Returns:** `S_OK` on success or `E_POINTER` for a null `enabled`.

---

## `SetDraftPredictorPath`

```cpp
HRESULT SetDraftPredictorPath([in, unique, string] LPCWSTR path);
```

Names a companion draft predictor that the model draft predictor runs in place
of prediction layers built into the model. The companion is a block draft model
trained for this model, such as a DFlash, DFlash2, or DSpark GGUF. It reads the
model's intermediate hidden states and proposes a block of draft tokens in one
pass, so `maxDraftTokens` reports the smaller of the draft token limit hint and
the companion's per-step capacity. The companion loads with the stage's
placement when the stage is built, and adds its own weights, a draft cache as
long as the model's context, and a host feature buffer for each layer it reads.
`nullptr` or an empty string clears the path.

Build fails with `E_INVALIDARG` when a path is set without
`SetModelDraftPredictorEnabled(TRUE)`. Build fails with
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the stage's backend does not run
companion predictors (only the llama.cpp backend does), when the artifact is not
a block draft model, when it was not trained for this model's width, vocabulary,
and layers, or when the model's architecture does not expose the hidden states
the companion reads.

**Returns:** `S_OK` on success or `E_NOT_VALID_STATE` after Build.

---

## `GetDraftPredictorPath`

```cpp
HRESULT GetDraftPredictorPath([out, string] LPCWSTR* path);
```

Returns the companion draft predictor path, or `nullptr` when none is set. The
stage owns the string, which remains valid until the path changes or the stage
is released.

**Returns:** `S_OK` on success or `E_POINTER` for a null `path`.

### Example

```cpp
wil::com_ptr<IWinMLStatefulStageOptions> options;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(options.put())));

THROW_IF_FAILED(options->AddStateTensorPair(1, 1));
THROW_IF_FAILED(options->SetCallerOwnsStateTensors(TRUE));
```

To let the stage propose draft tokens from the model's own predictor:

```cpp
THROW_IF_FAILED(options->SetDraftTokenLimitHint(3));
THROW_IF_FAILED(options->SetModelDraftPredictorEnabled(TRUE));
```
