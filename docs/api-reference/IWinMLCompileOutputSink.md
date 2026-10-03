<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLCompileOutputSink

> Part of the [WinML Runtime API Reference](README.md).

Caller-implemented destination for `IWinMLModelCompiler::CompileToSink` and
`CompileToSinkWithOptions`.

The runtime calls the sink during the compile call. `data` and `key` pointers are
valid only for the callback that supplies them; copy or consume them before
returning. Return a failing `HRESULT` from any callback to stop compilation.

```
IID: 97c93853-b0d8-4afa-974f-518a3c21ade2
```

## `WriteArtifactBytes`

```cpp
HRESULT WriteArtifactBytes(
    [in, size_is(byteCount)] const BYTE* data,
    [in] UINT64 byteCount
);
```

Receives a chunk of compiled graph artifact bytes. Multiple chunks may be sent.

---

## `BeginResource`

```cpp
HRESULT BeginResource(
    [in, string] const CHAR* key,
    [in] const WINML_RESOURCE_DESCRIPTOR* descriptor
);
```

Starts one external resource. `descriptor` supplies `byteSize`, `alignment`, and
`offset`.

If `BeginResource` succeeds, the runtime calls `EndResource` for the same key on
both success and failed-write paths.

---

## `WriteResourceBytes`

```cpp
HRESULT WriteResourceBytes(
    [in, string] const CHAR* key,
    [in, size_is(byteCount)] const BYTE* data,
    [in] UINT64 byteCount
);
```

Receives bytes for the open resource named by `key`. Multiple chunks may be
sent.

---

## `EndResource`

```cpp
HRESULT EndResource([in, string] const CHAR* key);
```

Closes the resource opened by `BeginResource`.
