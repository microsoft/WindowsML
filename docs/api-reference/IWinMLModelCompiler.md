<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLModelCompiler

> Part of the [WinML Runtime API Reference](README.md).

Target-bound model compilation interface. Query it from an
`IWinMLExecutionTarget`. A compile call writes a compiled artifact either to
files or to a caller-supplied sink. Load the compiled artifact with the standard
`IWinMLRuntime::LoadModelFromFile` or `LoadModelFromBuffer` methods.

```
IWinMLModelCompiler IID:            906a0175-188e-403f-a0ef-5caa0692db9e
IWinMLModelCompileOptions IID:      8e5f1c07-4a2b-4d16-9c33-b7a4e2d95f80
IWinMLOnnxSymbolicDimensionOverrides IID:
                                    c117047b-3388-421a-b1ef-4afa36295859
```

## Compiled model form

`WINML_COMPILED_MODEL_FORM_DEVICE_TARGETED` is the default. It asks for an
artifact targeted to the compile device. `WINML_COMPILED_MODEL_FORM_DURABLE`
asks for a portable compiled form when the target supports one.

Use `IsCompiledModelFormSupported` to query support before compiling. A compile
request for an unsupported form fails instead of changing the requested form.

---

## `CompileToFile`

```cpp
HRESULT CompileToFile(
    [in] IWinMLModel* model,
    [in, string] LPCWSTR artifactPath,
    [in, unique, string] LPCWSTR externalWeightsPath);
```

Compiles `model` for the target and writes the artifact to `artifactPath`.
`externalWeightsPath` may be null.

**Returns:** `S_OK` on success or a validation, file, target, or backend compile
failure.

---

## `CompileToSink`

```cpp
HRESULT CompileToSink(
    [in] IWinMLModel* model,
    [in] IWinMLCompileOutputSink* sink);
```

Compiles `model` and streams the artifact and any resources to `sink`.

**Returns:** `S_OK` on success. A failing sink callback aborts the compile and
its `HRESULT` is returned.

---

## `CompileToFileWithOptions`

```cpp
HRESULT CompileToFileWithOptions(
    [in] IWinMLModel* model,
    [in, string] LPCWSTR artifactPath,
    [in, unique, string] LPCWSTR externalWeightsPath,
    [in, unique] IWinMLModelCompileOptions* options);
```

Compiles to files using `options`. Passing `nullptr` uses default options.

**Returns:** `S_OK` on success, `E_INVALIDARG` for options created by another
compiler or invalid option values, or a validation, file, target, or backend
compile failure.

---

## `CompileToSinkWithOptions`

```cpp
HRESULT CompileToSinkWithOptions(
    [in] IWinMLModel* model,
    [in] IWinMLCompileOutputSink* sink,
    [in, unique] IWinMLModelCompileOptions* options);
```

Compiles to a sink using `options`. Passing `nullptr` uses default options.

**Returns:** `S_OK` on success, `E_INVALIDARG` for options created by another
compiler or invalid option values, a sink callback failure, or a backend compile
failure.

---

## `IsCompiledModelFormSupported`

```cpp
HRESULT IsCompiledModelFormSupported(
    [in] WINML_COMPILED_MODEL_FORM form,
    [out, retval] BOOL* supported);
```

Reports whether this compiler can produce `form`.

**Returns:** `S_OK` on success, `E_POINTER` for a null `supported`, or
`E_INVALIDARG` for an unknown `form`. `supported` receives `FALSE` when the
compile target cannot produce the form.

---

## `CreateCompileOptions`

```cpp
HRESULT CreateCompileOptions(
    [out, retval] IWinMLModelCompileOptions** options);
```

Creates an options object initialized to defaults. An options object created by a
compiler can be passed back only to that compiler.

**Returns:** `S_OK` on success or `E_POINTER` for a null `options`.

---

## `IWinMLModelCompileOptions`

### `SetCompiledModelForm`

```cpp
HRESULT SetCompiledModelForm(
    [in] WINML_COMPILED_MODEL_FORM form);
```

Sets the requested compiled model form.

**Returns:** `S_OK` on success or `E_INVALIDARG` for an unknown `form`.

---

### `GetCompiledModelForm`

```cpp
HRESULT GetCompiledModelForm(
    [out, retval] WINML_COMPILED_MODEL_FORM* form);
```

Returns the requested compiled model form.

**Returns:** `S_OK` on success or `E_POINTER` for a null `form`.

---

## `IWinMLOnnxSymbolicDimensionOverrides`

Query this interface from a compile-options object to set static extents for
symbolic dimensions in an ONNX source model. Overrides are copied into the
options object and applied before shape inference.

Device-targeted compilation must resolve every symbolic extent before compiling.
Models that remain resident in ONNX Runtime can keep dynamic extents; overrides
there are refinements, and an unreferenced override name is not an error.

### `SetOverride`

```cpp
HRESULT SetOverride(
    [in, string] LPCWSTR name,
    [in] INT64 extent);
```

Adds or replaces an override.

**Returns:** `S_OK` on success or `E_INVALIDARG` for a null or empty `name` or a
non-positive `extent`.

---

### `RemoveOverride`

```cpp
HRESULT RemoveOverride(
    [in, string] LPCWSTR name);
```

Removes an override.

**Returns:** `S_OK` when an override is removed, `S_FALSE` when none existed, or
`E_INVALIDARG` for a null or empty `name`.

---

### `ClearOverrides`

```cpp
HRESULT ClearOverrides();
```

Removes all overrides.

**Returns:** `S_OK` on success.

---

### `TryGetOverride`

```cpp
HRESULT TryGetOverride(
    [in, string] LPCWSTR name,
    [out] INT64* extent);
```

Reads an override.

**Returns:** `S_OK` when found, `S_FALSE` when not found, `E_POINTER` for a null
`extent`, or `E_INVALIDARG` for a null or empty `name`.

---

### `GetOverrideCount`

```cpp
HRESULT GetOverrideCount(
    [out, retval] UINT32* count);
```

Returns the number of overrides.

**Returns:** `S_OK` on success or `E_POINTER` for a null `count`.

---

### `GetOverrideAt`

```cpp
HRESULT GetOverrideAt(
    [in] UINT32 index,
    [in] UINT32 nameCapacity,
    [out, size_is(nameCapacity)] LPWSTR name,
    [out] UINT32* nameLength,
    [out] INT64* extent);
```

Copies the override at `index`. Overrides are enumerated in the order they were
first set. Pass `name == nullptr` with zero capacity, or pass an undersized
buffer, to retrieve the required character count including the terminator in
`nameLength`.

**Returns:** `S_OK` on success, `HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)`
for sizing or an undersized buffer, `E_INVALIDARG` for an out-of-range `index`,
or `E_POINTER` for a null `nameLength` or `extent`.

### Example

```cpp
wil::com_ptr<IWinMLModelCompiler> compiler;
THROW_IF_FAILED(target->QueryInterface(IID_PPV_ARGS(compiler.put())));

wil::com_ptr<IWinMLModelCompileOptions> options;
THROW_IF_FAILED(compiler->CreateCompileOptions(options.put()));
THROW_IF_FAILED(options->SetCompiledModelForm(WINML_COMPILED_MODEL_FORM_DURABLE));

wil::com_ptr<IWinMLOnnxSymbolicDimensionOverrides> overrides;
THROW_IF_FAILED(options->QueryInterface(IID_PPV_ARGS(overrides.put())));
THROW_IF_FAILED(overrides->SetOverride(L"batch", 1));
```
