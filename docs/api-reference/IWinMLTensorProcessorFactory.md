<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTensorProcessorFactory

> Part of the [WinML Runtime API Reference](README.md).

**Target-bound factory for fixed-schema tensor processors.**

Obtain this interface with `QueryInterface` from [`IWinMLExecutionTarget`](IWinMLExecutionTarget.md). The returned [`IWinMLProcessor`](IWinMLProcessor.md) objects can be added to a pipeline with `IWinMLPipelineBuilder::AddProcessorStage`.

```
IID: 63d93c2c-0899-4da8-a480-080b0bd3aef8
```

## `CreateDecodeEmbedProcessor`

```cpp
HRESULT CreateDecodeEmbedProcessor(
    [in] UINT32 vocabularySize,
    [in] UINT32 hiddenSize,
    [in] UINT64 sequenceCapacity,
    [in] IWinMLTensor* hiddenOutput,
    [out, retval] IWinMLProcessor** processor
);
```

Creates the fixed-schema embedding-gather processor used in the decode composition.

| Parameter | Description |
|---|---|
| `vocabularySize` | Vocabulary size used to shape the embedding-table input. Values must fit in a signed 32-bit token id. |
| `hiddenSize` | Hidden width used to shape the gathered embedding output. |
| `sequenceCapacity` | Fixed sequence capacity the processor's concrete descriptors are sized for. Values must be no greater than `INT32_MAX`. |
| `hiddenOutput` | Caller-supplied tensor that receives the processor's persistent hidden output. |
| `processor` | Receives the created processor. |

**Remarks:** `vocabularySize`, `hiddenSize`, and `sequenceCapacity` fix the processor input and output shapes at construction time. Host execution returns `E_BOUNDS` when token or position values are outside those bounds.

---

## `CreateDecodeSampleProcessor`

```cpp
HRESULT CreateDecodeSampleProcessor(
    [in] UINT32 vocabularySize,
    [in] BOOL logitsAreFloat32,
    [in] IWinMLTensor* tokenRing,
    [in] IWinMLTensor* position,
    [in] IWinMLTensor* sequenceLength,
    [in] IWinMLTensor* observedToken,
    [out, retval] IWinMLProcessor** processor
);
```

Creates the fixed-schema greedy selection processor used in the decode composition.

Selection is deterministic argmax over the logits input. Ties resolve to the lowest token id. The processor does not expose sampling parameters such as temperature, top-k, top-p, penalties, or seed.

| Parameter | Description |
|---|---|
| `vocabularySize` | Vocabulary size used to shape the logits input. Values must fit in a signed 32-bit token id. |
| `logitsAreFloat32` | Selects whether the logits input tensor is `FLOAT32` or `FLOAT16`. |
| `tokenRing` | Caller-supplied persistent `INT32` tensor that carries token ids. Its element count is the sequence capacity and must be no greater than `INT32_MAX`. |
| `position` | Caller-supplied persistent tensor that carries the decode position. |
| `sequenceLength` | Caller-supplied persistent tensor that carries the current sequence length. |
| `observedToken` | Caller-supplied tensor that receives the sampled token visible to the host loop. |
| `processor` | Receives the created processor. |

**Remarks:** On a valid step, the processor stores the selected token at `position + 1`, increments `position`, updates `sequenceLength`, and writes the token to `observedToken`. Host execution returns `E_BOUNDS` when the token ring has no next slot.

---

## `CreateDecodeGreedyProcessor`

```cpp
HRESULT CreateDecodeGreedyProcessor(
    [in] UINT32 vocabularySize,
    [in] BOOL logitsAreFloat32,
    [in] IWinMLTensor* nextToken,
    [in] IWinMLTensor* position,
    [in] IWinMLTensor* stepState,
    [in] IWinMLTensor* observedToken,
    [out, retval] IWinMLProcessor** processor
);
```

Creates a fixed-schema greedy processor for caller-provided decode state.
Selection is deterministic argmax with lowest-token-id tie breaking, matching
`CreateDecodeSampleProcessor`.

| Parameter | Description |
|---|---|
| `vocabularySize` | Vocabulary size used to shape the logits input. Values must fit in a signed 32-bit token id. |
| `logitsAreFloat32` | Selects whether the logits input tensor is `FLOAT32` or `FLOAT16`. |
| `nextToken` | Persistent scalar `INT32` or `INT64` tensor that receives the selected token for the next model step. |
| `position` | Persistent scalar `INT32` or `INT64` tensor containing the current decode position. |
| `stepState` | Persistent scalar `INT32`/`INT64` sequence length, or a `FLOAT16`/`FLOAT32` causal mask updated at the next position. A `FLOAT16` mask must have an even element count. |
| `observedToken` | Persistent one-element `INT32` tensor used for terminal host observation. |
| `processor` | Receives the created processor. |

**Remarks:** On a valid step, the processor selects a token, increments `position`, updates `stepState`, and writes the token to both `nextToken` and `observedToken`. Host execution returns `E_BOUNDS` when a floating-point mask does not contain the incremented position.

## Example

```cpp
wil::com_ptr<IWinMLTensorProcessorFactory> factory;
wil::com_ptr<IWinMLTensor> nextToken;
wil::com_ptr<IWinMLTensor> position;
wil::com_ptr<IWinMLTensor> stepState;
wil::com_ptr<IWinMLTensor> observedToken;
if (!factory || !nextToken || !position || !stepState || !observedToken) return S_OK;

wil::com_ptr<IWinMLProcessor> processor;
return factory->CreateDecodeGreedyProcessor(
    32000,
    TRUE,
    nextToken.get(),
    position.get(),
    stepState.get(),
    observedToken.get(),
    processor.put());
```

---
