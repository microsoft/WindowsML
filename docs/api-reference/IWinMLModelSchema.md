<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLModelSchema

> Part of the [WinML Runtime API Reference](README.md).

Declared model tensor schema. Query this interface from `IWinMLModel` when the
loaded artifact exposes input and output metadata. The indices reported here are
the positional indices used by `IWinMLStage` binding methods.

For formats whose concrete tensor shape is resolved during pipeline build, use
[`IWinMLStageSchema`](IWinMLStageSchema.md) after `Build`.
ORT-specific name lookup is exposed by [`IWinMLOrtModelSchema`](IWinMLOrtModelSchema.md).

```
IID: 7696a11b-6f39-478a-b33d-c69850dbc2dc
```

## `GetInputCount`

```cpp
HRESULT GetInputCount([out, retval] UINT32* count);
```

Returns the number of declared inputs.

**Returns:** `S_OK` on success, `E_POINTER` for a null `count`, or
`HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when declared metadata is unavailable.

---

## `GetInputTensorDesc`

```cpp
HRESULT GetInputTensorDesc(
    [in] UINT32 index,
    [out, retval] WINML_TENSOR_SCHEMA_DESC* desc);
```

Returns the declared descriptor for input `index`. `desc->dimensions` points to
model-owned memory valid for the lifetime of the model object.

**Returns:** `S_OK` on success, `E_POINTER` for a null `desc`, `E_BOUNDS` for an
out-of-range `index`, or `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when declared
metadata is unavailable.

---

## `GetOutputCount`

```cpp
HRESULT GetOutputCount([out, retval] UINT32* count);
```

Returns the number of declared outputs.

**Returns:** `S_OK` on success, `E_POINTER` for a null `count`, or
`HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when declared metadata is unavailable.

---

## `GetOutputTensorDesc`

```cpp
HRESULT GetOutputTensorDesc(
    [in] UINT32 index,
    [out, retval] WINML_TENSOR_SCHEMA_DESC* desc);
```

Returns the declared descriptor for output `index`. `desc->dimensions` points to
model-owned memory valid for the lifetime of the model object.

**Returns:** `S_OK` on success, `E_POINTER` for a null `desc`, `E_BOUNDS` for an
out-of-range `index`, or `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when declared
metadata is unavailable.

### Example

```cpp
wil::com_ptr<IWinMLModelSchema> schema;
THROW_IF_FAILED(model->QueryInterface(IID_PPV_ARGS(schema.put())));

UINT32 inputCount = 0;
THROW_IF_FAILED(schema->GetInputCount(&inputCount));
```
