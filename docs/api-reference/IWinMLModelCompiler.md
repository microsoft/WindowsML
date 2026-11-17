<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLModelCompiler

> Part of the [WinML Runtime API Reference](README.md).

Target-bound model compilation interface. Query it from an
`IWinMLExecutionTarget`. A compile call writes a compiled artifact either to
files or to a caller-supplied sink. Load the compiled artifact with the standard
`IWinMLRuntime::LoadModelFromFile` or `LoadModelFromBuffer` methods.

The target decides the artifact. A target pinned to an ONNX Runtime execution
provider writes an EPContext ONNX model. A GPU target that isn't pinned to a
provider compiles for the [CGC backend](../Runtime/backends.md#the-cgc-backend)
and writes a CGC artifact: name it `.oir` for the device-targeted form and
`.mlirbc` for the durable form so that `LoadModelFromFile` recognizes it.
Compilation commits an artifact and its resources, not an executable pipeline;
`Build` establishes that the reloaded artifact runs on its target.

```
IWinMLModelCompiler IID:            906a0175-188e-403f-a0ef-5caa0692db9e
IWinMLModelCompileOptions IID:      8e5f1c07-4a2b-4d16-9c33-b7a4e2d95f80
IWinMLOnnxSymbolicDimensionOverrides IID:
                                    c117047b-3388-421a-b1ef-4afa36295859
```

## Compiled model form

`WINML_COMPILED_MODEL_FORM` selects what kind of compiled artifact to produce:
one built for the exact machine that compiled it, or a portable one that still
loads on other devices and after driver or runtime updates.

| Value | Behavior |
|---|---|
| `WINML_COMPILED_MODEL_FORM_DEVICE_TARGETED` | Default. Built for the hardware and software stack that compiled it. Loads fastest, but a different device, driver, or runtime version rejects it. |
| `WINML_COMPILED_MODEL_FORM_DURABLE` | Hardware-independent. Keeps loading across devices and software updates; the hardware-specific work is finished at load, so loading costs more. |

Configure the form on an [options object](#iwinmlmodelcompileoptions). The
`CompileToFile` and `CompileToSink` methods always use the defaults; the
`WithOptions` methods take a caller-supplied options object.

Use `IsCompiledModelFormSupported` to query support before compiling. A compile
request for a form the target cannot produce returns
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` instead of producing a different
form.

## CGC compilation

Windows ML chooses how a device-targeted CGC compile lowers the model for the
device; the API has no lowering option. Two parts of the package take part,
and an application can see what happens without each of them.

- The ONNX-to-CGC compiler, `FoundryToolbox.dll`, compiles an ONNX source
  model. An application can leave it out with the `WinMLCopyFoundryToolbox`
  MSBuild property set to `false`. Without it, `IsCompiledModelFormSupported`
  reports the durable form unsupported, a durable request returns
  `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`, and a device-targeted compile of an
  ONNX source returns `HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)`. Compiled
  artifacts load and run without it.
- The CGC runtime, `cgc_ir.dll` and `dxcgc.dll`, finishes a device-targeted
  compile. Windows ML loads the copies next to the runtime, or else the copies
  in the application directory, always by full path. ARM64EC packages don't
  include it, so `IsCompiledModelFormSupported` reports the device-targeted
  form unsupported for a CGC target, and a device-targeted compile returns
  `HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)`. The durable form doesn't use it.

Device-targeted CGC compilation requires every tensor extent to be known ahead
of time. Fix the symbolic dimensions of an ONNX source with
[`IWinMLOnnxSymbolicDimensionOverrides`](#iwinmlonnxsymbolicdimensionoverrides).

---

## `CompileToFile`

```cpp
HRESULT CompileToFile(
    [in] IWinMLModel* model,
    [in, string] LPCWSTR artifactPath,
    [in, unique, string] LPCWSTR externalWeightsPath);
```

Compiles `model` for the target and writes the artifact to `artifactPath`.
`externalWeightsPath` may be null, which selects the backend default. A CGC
compile that externalizes resources names them `winml-<hash>.safetensors` by
default.

**Returns:** `S_OK` on success or a validation, file, target, or backend compile
failure. `D3D12_ERROR_DRIVER_VERSION_MISMATCH` when `model` is a CGC artifact
in text form stamped with a CGC IR version that this version cannot
re-serialize; compile it again from the source model.

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

A durable artifact embeds its resources, so no sidecar is written beside it.
Supplying `externalWeightsPath` together with
`WINML_COMPILED_MODEL_FORM_DURABLE` returns
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` rather than ignoring the path.

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

In Python, `ExecutionTarget.model_compiler()` returns a `ModelCompiler`.
`ModelCompiler.compile_to_file` takes `form` and `options` keyword arguments,
`supports_form` reports support for a form, and `create_options` returns a
`ModelCompileOptions` with `form`, `supports_symbolic_dimensions`, and
`symbolic_dimensions`.

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
