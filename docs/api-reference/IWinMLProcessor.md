<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLProcessor

> Part of the [WinML Runtime API Reference](README.md).

**Fixed-schema processor stage interface.**

Processors are fixed-schema stages added to a pipeline through `IWinMLPipelineBuilder::AddProcessorStage`. Query [`IWinMLD3D12Processor`](IWinMLD3D12Processor.md) when a processor can record D3D12 commands.

Processor input and output descriptors are concrete [`WINML_TENSOR_DESC`](Structures.md) values. Outputs are an in/out array: an incoming compatible prebound tensor is written in place and preserved; an incoming null slot receives a tensor returned to the caller.

```
IID: d4a3e8c1-5b72-4f90-a1d3-8c6e2f4a9b05
```

## `GetInputCount`

```cpp
HRESULT GetInputCount([out, retval] UINT32* count);
```

Returns the number of processor inputs.

| Parameter | Description |
|---|---|
| `count` | Receives the input count. |


---

## `GetInputTensorDesc`

```cpp
HRESULT GetInputTensorDesc(
    [in] UINT32 index,
    [out, retval] WINML_TENSOR_DESC* desc);
```

Returns the concrete, fully resolved descriptor for input `index`.

| Parameter | Description |
|---|---|
| `index` | Zero-based input ordinal. |
| `desc` | Receives the concrete input descriptor. Every dimension is resolved; the processor never reports a free/dynamic dimension. |


---

## `GetOutputCount`

```cpp
HRESULT GetOutputCount([out, retval] UINT32* count);
```

Returns the number of processor outputs.

| Parameter | Description |
|---|---|
| `count` | Receives the output count. |


---

## `GetOutputTensorDesc`

```cpp
HRESULT GetOutputTensorDesc(
    [in] UINT32 index,
    [out, retval] WINML_TENSOR_DESC* desc);
```

Returns the concrete, fully resolved descriptor for output `index`.

| Parameter | Description |
|---|---|
| `index` | Zero-based output ordinal. |
| `desc` | Receives the concrete output descriptor. Every dimension is resolved; the processor never reports a free/dynamic dimension. |


---

## `Execute`

```cpp
HRESULT Execute(
    [in] UINT32 inputCount,
    [in, size_is(inputCount)] IWinMLTensor** inputs,
    [in] UINT32 outputCount,
    [in, out, size_is(outputCount)] IWinMLTensor** outputs);
```

Executes the processor.

| Parameter | Description |
|---|---|
| `inputCount` | Number of elements in `inputs`. |
| `inputs` | Input tensors supplied to the processor. Each element is borrowed for the duration of the call. |
| `outputCount` | Number of elements in `outputs`. |
| `outputs` | In/out array of output tensors. Each incoming non-null element is borrowed and must not be released or replaced by the processor. |

**Returns:** `S_OK` on success. Returns `E_INVALIDARG` before invoking the
processor when a caller-prebound output does not match its declared data type,
rank, or dimensions. Returns `E_UNEXPECTED` when the processor returns a
malformed output or replaces caller-bound storage.

The caller owns every non-null output reference written by the method, including
references written before a failure is returned.

**Remarks:** For each incoming non-null output, the processor writes into a compatible
prebound tensor and leaves the array element unchanged. For an incoming null
output slot, the processor writes a tensor carrying one reference transferred to
the caller. On success every output element must be non-null and match the
descriptor returned by `GetOutputTensorDesc`. A returned reference transfers even
when `Execute` returns failure; the caller adopts and releases that reference.

A prebound tensor is compatible when its data type, rank, and every dimension
match the descriptor returned by `GetOutputTensorDesc`.

### Processor implementation requirements

Implementations must accept compatible prebound output tensors. Leave an output
element unchanged when writing into its prebound tensor; do not add a reference
to an unchanged element. For an incoming null output slot, return one owned reference for the new tensor. Output
descriptors must be fully concrete. The runtime captures them when the processor
stage is added to a pipeline, and the processor must not subsequently change
them.

For D3D12 command recording, use [`IWinMLD3D12Processor::RecordCommands`](IWinMLD3D12Processor.md#recordcommands).

## Example

```cpp
wil::com_ptr<IWinMLProcessor> processor;
if (!processor) return S_OK;

UINT32 inputCount = 0;
HRESULT hr = processor->GetInputCount(&inputCount);
if (FAILED(hr)) return hr;

for (UINT32 i = 0; i < inputCount; ++i)
{
    WINML_TENSOR_DESC desc{};
    hr = processor->GetInputTensorDesc(i, &desc);
    if (FAILED(hr)) return hr;
}
return S_OK;
```

---
