<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLStreamingResourceMapReader

> Part of the [WinML Runtime API Reference](README.md).

An optional side interface of a caller-implemented
[`IWinMLResourceMapReader`](IWinMLResourceMapReader.md). Through it the runtime
identifies each resource by a digest the reader supplies instead of hashing the
bytes, and reads each resource through its own acquisition instead of requiring
every `GetData` view to remain valid for the whole load, compilation, or
`Build`. A reader that does not implement it returns `E_NOINTERFACE`, and the
runtime reads it through `IWinMLResourceMapReader` alone.

```
IID: 35c88c22-4eb8-4f20-a9f2-f235d5aa8343
```

The interface and `WINML_RESOURCE_CONTENT_DIGEST` are declared in packages that
include the CGC backend. A stage whose backend cannot read resources
one at a time reads the same reader through `GetData`, so a streaming reader
still serves `GetData`.

The runtime calls these methods only while `LoadModelFromFile` or
`LoadModelFromBuffer` loads a model over the reader, while an
`IWinMLModelCompiler` method compiles it, and while `Build` builds a stage over
it, never afterward. Recovery that would need the bytes again, such as after
device removal, fails, and the caller loads the model again. The methods are
free-threaded and may be called concurrently, including for the same key.

## `GetContentDigest`

```cpp
HRESULT GetContentDigest(
    [in, string] const CHAR* key,
    [out] WINML_RESOURCE_CONTENT_DIGEST* digest);
```

Writes a 32-byte digest of the resource. Equal digests mean equal bytes: the
runtime may treat two resources with equal digests and sizes as interchangeable,
so the digest must change whenever the bytes can change. The runtime does not
verify supplied digests, keeps them apart from digests it computes, uses them
only to share device copies and preprocessed constants within the process, and
does not persist them. The recommended derivation is SHA-256 over the full
content of the source the resource is produced from, together with the version
of the transform that produces it.

Returns `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` for a key the reader does not
serve. For a key the reader enumerates, `ERROR_NOT_FOUND` means it supplies no
digest, and the runtime hashes the bytes instead. Any other failure fails the
operation that asked for the digest.

## `AcquireData`

```cpp
HRESULT AcquireData(
    [in, string] const CHAR* key,
    [out] const BYTE** data,
    [out] UINT64* byteSize,
    [out] IUnknown** lifetimeKeepAlive);
```

Returns a view of the resource's bytes, their size, and `lifetimeKeepAlive`, an
object that owns this acquisition. The view stays valid until the final
`Release` of `lifetimeKeepAlive`, which can occur on any thread and after the
runtime call returns; concurrent acquisitions of one key end independently. On
failure no acquisition is made and `lifetimeKeepAlive` receives null.

The runtime checks that `byteSize` equals the resource descriptor's size, and
otherwise fails the load, compilation, or `Build` with
`HRESULT_FROM_WIN32(ERROR_INVALID_DATA)`. It does not hash or sample the bytes.
A failure, including `ERROR_NOT_FOUND` for a key the reader enumerates, fails
the operation that needed the bytes; the runtime does not fall back to
`GetData`.
