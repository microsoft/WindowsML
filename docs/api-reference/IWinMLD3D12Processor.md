<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLD3D12Processor

> Part of the [WinML Runtime API Reference](README.md).

**D3D12 command-recording processor capability.**

Obtain this interface from `IWinMLProcessor` with `QueryInterface`.

```
IID: 7448b45c-f02d-4dc1-b9fe-4891a1bc087d
```

## `RecordCommands`

```cpp
HRESULT RecordCommands(
    [in] IUnknown* commandList,
    [in] UINT32 inputCount,
    [in, size_is(inputCount)] IWinMLTensor** inputs,
    [in] UINT32 outputCount,
    [in, out, size_is(outputCount)] IWinMLTensor** outputs);
```

Records processor work into an existing command list.

| Parameter | Description |
|---|---|
| `commandList` | Command list object to record into. |
| `inputCount` | Number of elements in `inputs`. |
| `inputs` | Input tensors supplied to the processor. Each element is borrowed for the duration of the call. |
| `outputCount` | Number of elements in `outputs`. |
| `outputs` | In/out array following the allocation and ownership rules of [`IWinMLProcessor::Execute`](IWinMLProcessor.md#execute). |

**Returns:** `S_OK` on success. Returns `E_INVALIDARG` before invoking the
processor when a caller-prebound output does not match its declared data type,
rank, or dimensions. Returns `E_UNEXPECTED` when the processor returns a
malformed output or replaces caller-bound storage.

The caller owns every non-null output reference written by the method, including
references written before a failure is returned. The referenced output storage is
written when the recorded work executes on the device timeline, not when
`RecordCommands` returns.

`commandList` must be open for compute recording and must belong to the same
canonical D3D12 device as every tensor resource. Input and output resources must
be in `D3D12_RESOURCE_STATE_UNORDERED_ACCESS` on entry and remain in that state
when the recorded processor work completes. The processor records UAV ordering
where its own writes require it; the caller owns transitions into and out of the
processor requirements.

The caller must keep the processor, command list, all input tensors, and all
returned output tensors alive until the queue has completed the recorded work.
The direct method does not submit the command list or manufacture a completion
fence.

**Remarks:** Each incoming non-null output is written in place into an unchanged
prebound tensor. Each incoming null output slot receives a tensor carrying one
reference transferred to the caller. A returned reference transfers even when
`RecordCommands` returns failure; the caller adopts and releases that reference.
The caller must not submit work from a failed recording attempt.
On success every output must be non-null and match its declared descriptor.
Implementations must follow the output ownership rules described by [`IWinMLProcessor::Execute`](IWinMLProcessor.md#execute).

## Example

```cpp
wil::com_ptr<IWinMLProcessor> processor;
wil::com_ptr<IUnknown> commandList;
if (!processor || !commandList) return S_OK;

wil::com_ptr<IWinMLD3D12Processor> d3d12Processor;
HRESULT hr = processor->QueryInterface(IID_PPV_ARGS(d3d12Processor.put()));
if (hr == E_NOINTERFACE) return S_OK;
if (FAILED(hr)) return hr;

IWinMLTensor* inputs[1] = {};
IWinMLTensor* outputs[1] = {};
return d3d12Processor->RecordCommands(commandList.get(), 1, inputs, 1, outputs);
```

---
