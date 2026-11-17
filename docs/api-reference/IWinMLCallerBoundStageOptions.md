<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLCallerBoundStageOptions

> Part of the [WinML Runtime API Reference](README.md).

A construction-time option of a model stage. Query the
[`IWinMLStage`](IWinMLStage.md) that `IWinMLPipelineBuilder::AddModelStage`
returns. A caller that supplies its own device storage for every input and
output of the stage declares it here, so the runtime does not also allocate
storage for them. Methods may be called from any thread.

```
IID: 02016154-17f3-4562-8331-b95973a922ba
```

## `SetAllTensorsCallerBound`

```cpp
HRESULT SetAllTensorsCallerBound([in] BOOL value);
```

Declares whether the caller binds every input and output of the stage. The
default is `FALSE`. Call it after `AddModelStage` and before `Build`, which reads
the value once.

When the value is `TRUE`:

- `Build` allocates no backing or staging storage for the stage's inputs and
  outputs.
- Before each `IWinMLPipeline::Run` or `IWinMLPipelineExecution::Submit`, the
  caller binds every input that no pipeline connection feeds, and every output,
  to a caller-owned tensor on the device of the stage's execution target, with
  `BindInput` and `BindOutput`. Bindings persist, so binding once suffices while
  the tensors stay the same.
- `Run` and `Submit` return `E_INVALIDARG` when such an input is bound to host
  memory, because the stage has no storage to copy it into.
- The stage's state tensors are caller-owned:
  `IWinMLStatefulStageOptions::GetCallerOwnsStateTensors` reports `TRUE`,
  `AddStateTensorPair` and `SetCallerOwnsStateTensors(TRUE)` return
  `E_INVALIDARG`, and `SetCallerOwnsStateTensors(FALSE)` clears the declaration.

A stage whose backend allocates no per-argument storage at `Build` is unaffected.

| HRESULT | Condition |
|---|---|
| `S_OK` | The declaration was stored. |
| `E_INVALIDARG` | `value` is `TRUE` and the stage has state tensor pairs. |
| `E_NOT_VALID_STATE` | The stage's pipeline is built. |
| `HRESULT_FROM_WIN32(ERROR_BUSY)` | `Build` is in progress. |

A failed call leaves the declaration unchanged.

```cpp
wil::com_ptr<IWinMLStage> stage;
THROW_IF_FAILED(builder->AddModelStage(model.get(), gpuTarget.get(), L"decoder", stage.put()));
wil::com_ptr<IWinMLCallerBoundStageOptions> options;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(options.put())));
THROW_IF_FAILED(options->SetAllTensorsCallerBound(TRUE));
```
