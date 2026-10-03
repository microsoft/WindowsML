<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLStageSchema

> Part of the [WinML Runtime API Reference](README.md).

Tensor schema for a stage. Query this interface from `IWinMLStage`.

Input and output counts can be readable before Build for models that publish
their own schema. For models whose schema is resolved by the selected backend,
counts can be zero until Build. Treat a zero count before Build as "schema
unavailable" rather than "no inputs".

Tensor descriptors require Build because backend construction can specialize
shapes and data types. Query descriptors after Build to stay backend-neutral.
`dimensions` points to stage-owned memory valid for the lifetime of the stage.

```
IID: 41f17eb8-1b18-4eb6-b628-dd93d669f401
```

## `GetInputCount`

```cpp
HRESULT GetInputCount([out, retval] UINT32* count);
```

Returns the stage input count.

**Returns:** `S_OK` on success or `E_POINTER` for a null `count`.

---

## `GetInputTensorDesc`

```cpp
HRESULT GetInputTensorDesc(
    [in] UINT32 index,
    [out, retval] WINML_TENSOR_DESC* desc);
```

Returns the concrete descriptor for input `index`.

**Returns:** `S_OK` on success, `E_POINTER` for a null `desc`, `E_INVALIDARG`
for an out-of-range `index`, or `E_NOT_VALID_STATE` before the stage is attached
to a built pipeline.

---

## `GetOutputCount`

```cpp
HRESULT GetOutputCount([out, retval] UINT32* count);
```

Returns the stage output count.

**Returns:** `S_OK` on success or `E_POINTER` for a null `count`.

---

## `GetOutputTensorDesc`

```cpp
HRESULT GetOutputTensorDesc(
    [in] UINT32 index,
    [out, retval] WINML_TENSOR_DESC* desc);
```

Returns the concrete descriptor for output `index`.

**Returns:** `S_OK` on success, `E_POINTER` for a null `desc`, `E_INVALIDARG`
for an out-of-range `index`, or `E_NOT_VALID_STATE` before the stage is attached
to a built pipeline.

### Example

```cpp
wil::com_ptr<IWinMLStageSchema> schema;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(schema.put())));

WINML_TENSOR_DESC outputDesc{};
THROW_IF_FAILED(schema->GetOutputTensorDesc(0, &outputDesc));
```
