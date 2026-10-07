<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLResourceMapReader

> Part of the [WinML Runtime API Reference](README.md).

Caller-implemented read-only resource source. Pass an implementation to
`IWinMLRuntime::LoadModelFromFile` or `LoadModelFromBuffer` when a model refers
to external resources that the caller provides.

Keys are UTF-8 byte strings. Returned key and data pointers are reader-owned and
must remain valid while the model can use them. Reader callback failures are
recorded and can fail model load, build, or compile operations that consume the
resources.

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
