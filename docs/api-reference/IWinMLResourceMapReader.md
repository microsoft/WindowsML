<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLResourceMapReader

> Part of the [WinML Runtime API Reference](README.md).

Caller-implemented read-only resource source. Pass an implementation to
`IWinMLRuntime::LoadModelFromFile` or `LoadModelFromBuffer` when a model refers
to external resources that the caller provides.

Keys are UTF-8 byte strings. Returned key pointers are reader-owned and stable
for the reader's lifetime; data views follow the rule under [`GetData`](#getdata).
The runtime holds the reader for at least the model's lifetime, because a later
`Build` or compilation of the model may read its resources again. Reader
callback failures are recorded and can fail model load, build, or compile
operations that consume the resources.

For ORT-backed models, buffer loads bind the supplied map. File loads resolve
model-relative external files; a resource map supplied with a file model does not
replace those files during ORT pipeline construction.

```
IID: b188cc3d-b051-453b-8651-ddeef3512a47
```

## `GetCount`

```cpp
HRESULT GetCount([out, retval] UINT32* count);
```

Returns the number of keys in the map.

---

## `GetKey`

```cpp
HRESULT GetKey(
    [in] UINT32 index,
    [out] const CHAR** key,
    [out] UINT32* keyLength
);
```

Returns the key bytes at `index` and the length in bytes.

---

## `GetDescriptor`

```cpp
HRESULT GetDescriptor(
    [in, string] const CHAR* key,
    [out, retval] WINML_RESOURCE_DESCRIPTOR* descriptor
);
```

Returns `byteSize`, `alignment`, and `offset` for `key`.

---

## `GetData`

```cpp
HRESULT GetData(
    [in, string] const CHAR* key,
    [out] const BYTE** data,
    [out] UINT64* byteSize
);
```

Returns a pointer to the resource bytes and the payload size.

The runtime calls `GetData` only within an enclosing call: a
`LoadModelFromFile` or `LoadModelFromBuffer` that loads a model over the reader,
an `IWinMLModelCompiler` method that compiles that model, or an
`IWinMLPipelineBuilder::Build` that builds a stage over it. Each view must remain
valid until that enclosing call returns. The runtime does not use a view after
the call returns, including while a built pipeline runs; a later load,
compilation, or `Build` that needs the bytes calls `GetData` again, and the
reader must return the same bytes.

---

## Streaming side interface

A reader can also implement
[`IWinMLStreamingResourceMapReader`](IWinMLStreamingResourceMapReader.md). The
runtime then identifies each resource by a digest the reader supplies and reads
each resource through one acquisition at a time, instead of requiring every
`GetData` view to remain valid for the whole enclosing call.
