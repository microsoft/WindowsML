<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLPipelineBuilder

> Part of the [WinML Runtime API Reference](README.md).

Creates a pipeline graph. Add stages, connect stage outputs to later inputs as
needed, then call `Build` once.

A successful `Build` consumes the builder. After that, stage-add, connect, and
build calls on the same builder return `E_NOT_VALID_STATE`.

```
IID: 3bcfcf5a-8b40-4931-9812-d84679123006
```

## `AddModelStage`

```cpp
HRESULT AddModelStage(
    [in] IWinMLModel* model,
    [in, unique] IWinMLExecutionTarget* target,
    [in, unique, string] LPCWSTR debugName,
    [out, retval] IWinMLStage** stage);
```

Adds a model-backed stage.

| Parameter | Description |
|---|---|
| `model` | Model to add. |
| `target` | Execution target, or `nullptr` for automatic placement. |
| `debugName` | Optional diagnostic name. |
| `stage` | Receives the stage handle. |

**Returns:** `S_OK` on success, `E_POINTER` for a null `model` or `stage`,
`E_INVALIDARG` for an execution target that the runtime cannot use, or
`E_NOT_VALID_STATE` after the builder has been consumed.

---

## `AddProcessorStage`

```cpp
HRESULT AddProcessorStage(
    [in] IWinMLProcessor* processor,
    [in, unique] IWinMLExecutionTarget* target,
    [in, unique, string] LPCWSTR debugName,
    [out, retval] IWinMLStage** stage);
```

Adds a processor-backed stage.

| Parameter | Description |
|---|---|
| `processor` | Processor to add. |
| `target` | Execution target, or `nullptr` for automatic placement. |
| `debugName` | Optional diagnostic name. |
| `stage` | Receives the stage handle. |

**Returns:** `S_OK` on success, `E_POINTER` for a null `processor` or `stage`,
`E_INVALIDARG` for an execution target that the runtime cannot use, or
`E_NOT_VALID_STATE` after the builder has been consumed.

---

## `Connect`

```cpp
HRESULT Connect(
    [in] IWinMLStage* sourceStage,
    [in] UINT32 sourceOutputIndex,
    [in] IWinMLStage* targetStage,
    [in] UINT32 targetInputIndex);
```

Connects `sourceStage[sourceOutputIndex]` to
`targetStage[targetInputIndex]` for the same execution.

**Returns:** `S_OK` on success, `E_POINTER` for a null stage, `E_INVALIDARG` for
foreign stages or known out-of-range indices, or `E_NOT_VALID_STATE` after the
builder has been consumed.

---

## `ConnectNextIteration`

```cpp
HRESULT ConnectNextIteration(
    [in] IWinMLStage* sourceStage,
    [in] UINT32 sourceOutputIndex,
    [in] IWinMLStage* targetStage,
    [in] UINT32 targetInputIndex);
```

Declares a one-iteration feedback edge: the output produced by iteration `N` is
used as the target input for iteration `N+1`. Bind `targetStage[targetInputIndex]`
directly to seed iteration zero. That target input cannot also be the target of
`Connect`.

This method defines graph topology only. It does not start execution or set an
iteration count. Call `IWinMLPipeline::ResetExecutionState` to discard retained
iteration values.

**Returns:** `S_OK` on success, `E_POINTER` for a null stage, `E_INVALIDARG` for
foreign stages or known out-of-range indices, or `E_NOT_VALID_STATE` after the
builder has been consumed.

---

## `Build`

```cpp
HRESULT Build(
    [out, retval] IWinMLPipeline** pipeline);
```

Builds the graph into an executable pipeline.

| Parameter | Description |
|---|---|
| `pipeline` | Receives the built pipeline. |

**Returns:** `S_OK` on success, `E_POINTER` for a null `pipeline`,
`E_INVALIDARG` when the graph has no stages or invalid bindings, or
`E_NOT_VALID_STATE` if `Build` was already called. Backend creation and graph
validation errors are returned from this call.

### Example

```cpp
wil::com_ptr<IWinMLPipelineBuilder> builder;
THROW_IF_FAILED(runtime->CreatePipelineBuilder(builder.put()));

wil::com_ptr<IWinMLStage> stage;
THROW_IF_FAILED(builder->AddModelStage(model.get(), nullptr, L"model", stage.put()));
THROW_IF_FAILED(stage->RequestOutput(0));

wil::com_ptr<IWinMLPipeline> pipeline;
THROW_IF_FAILED(builder->Build(pipeline.put()));
```
