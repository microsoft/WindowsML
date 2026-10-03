<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLImageTensorFactory

> Part of the [WinML Runtime API Reference](README.md).

**Target-bound factory for image and NV12 video tensorization.**

Obtain this interface with `QueryInterface` from [`IWinMLExecutionTarget`](IWinMLExecutionTarget.md).

`WINML_IMAGE_TENSOR_DESC` describes the image layout and conversion format: `dataType`, `layout`, `channelOrder`, `batchSize`, `channels`, `width`, `height`, `resizeMode`, `letterboxValue`, `normalization`, and optional `quantization`. Image conversion supports batch size 1, one to four channels, `FLOAT32`, `FLOAT16`, `INT8`, and `UINT8` tensor data, and `NCHW` or `NHWC` layout. See [Structures.md#winml_image_tensor_desc](Structures.md#winml_image_tensor_desc).

```
IID: 58c7f0e1-0c74-4fa5-a949-9bbca2f72164
```

## `CreateTensorFromImageMemory`

```cpp
HRESULT CreateTensorFromImageMemory(
    [in, size_is(byteCount)] const BYTE* pixels,
    [in] UINT64 byteCount,
    [in] UINT32 width,
    [in] UINT32 height,
    [in] UINT32 rowPitch,
    [in] UINT32 sourceFormat,
    [in] const WINML_IMAGE_TENSOR_DESC* desc,
    [out, retval] IWinMLTensor** tensor
);
```

Creates a tensor from a CPU image buffer.

| Parameter | Description |
|---|---|
| `pixels` | Source pixel buffer. |
| `byteCount` | Size of `pixels` in bytes. |
| `width` | Source image width, in pixels. |
| `height` | Source image height, in pixels. |
| `rowPitch` | Source row stride in bytes. Pass `0` for tightly packed rows. |
| `sourceFormat` | `DXGI_FORMAT` value for the source buffer. |
| `desc` | Image tensor descriptor that controls layout, normalization, resize behavior, and optional quantization. |
| `tensor` | Receives the created tensor. |

**Remarks:** This is the fused image repack path. The runtime performs resize, channel reorder, normalization, and tensor layout conversion in one call. Supported source formats are `DXGI_FORMAT_R8G8B8A8_UNORM`, `DXGI_FORMAT_R8G8B8A8_UNORM_SRGB`, `DXGI_FORMAT_B8G8R8A8_UNORM`, `DXGI_FORMAT_B8G8R8A8_UNORM_SRGB`, and `DXGI_FORMAT_R8_UNORM`.

---

## `WriteTensorFromImageMemory`

```cpp
HRESULT WriteTensorFromImageMemory(
    [in, size_is(byteCount)] const BYTE* pixels,
    [in] UINT64 byteCount,
    [in] UINT32 width,
    [in] UINT32 height,
    [in] UINT32 rowPitch,
    [in] UINT32 sourceFormat,
    [in] const WINML_IMAGE_TENSOR_DESC* desc,
    [in] IWinMLTensor* destination
);
```

Writes image memory into a caller-allocated tensor.

| Parameter | Description |
|---|---|
| `pixels` | Source pixel buffer. |
| `byteCount` | Size of `pixels` in bytes. |
| `width` | Source image width, in pixels. |
| `height` | Source image height, in pixels. |
| `rowPitch` | Source row stride in bytes. Pass `0` for tightly packed rows. |
| `sourceFormat` | `DXGI_FORMAT` value for the source buffer. |
| `desc` | Image tensor descriptor that defines the conversion. |
| `destination` | Destination tensor to populate. |

**Remarks:** This is the hot-loop variant of `CreateTensorFromImageMemory`. `destination` must already match the resolved tensor shape and data type. Supported source formats match `CreateTensorFromImageMemory`.

---

## `WriteImageMemoryFromTensor`

```cpp
HRESULT WriteImageMemoryFromTensor(
    [in] IWinMLTensor* tensor,
    [in] UINT32 width,
    [in] UINT32 height,
    [in] UINT32 rowPitch,
    [in] UINT32 destinationFormat,
    [in] const WINML_IMAGE_TENSOR_DESC* desc,
    [out, size_is(byteCount)] BYTE* pixels,
    [in] UINT64 byteCount
);
```

Writes a tensor back into a caller-supplied image buffer.

| Parameter | Description |
|---|---|
| `tensor` | Source tensor to convert from. |
| `width` | Destination image width, in pixels. |
| `height` | Destination image height, in pixels. |
| `rowPitch` | Destination row stride in bytes. |
| `destinationFormat` | `DXGI_FORMAT` value for the destination buffer. |
| `desc` | Image tensor descriptor used for denormalization, dequantization, and channel-order interpretation. |
| `pixels` | Destination pixel buffer. |
| `byteCount` | Size of `pixels` in bytes. |

**Remarks:** This is the reverse-direction pack path for raw image buffers. Supported destination formats match `CreateTensorFromImageMemory`.

---

## `CreateTensorFromMFSampleNV12`

```cpp
HRESULT CreateTensorFromMFSampleNV12(
    [in] IUnknown* sample,
    [in] const WINML_IMAGE_TENSOR_DESC* desc,
    [in] const WINML_VIDEO_FRAME_METADATA* metadata,
    [out, retval] IWinMLTensor** tensor
);
```

Creates a tensor from an NV12 Media Foundation sample.

| Parameter | Description |
|---|---|
| `sample` | Source object, queryable for `IMFSample`. |
| `desc` | Image tensor descriptor that defines the output tensor. |
| `metadata` | Source frame geometry and timing metadata. |
| `tensor` | Receives the created tensor. |

**Remarks:** `metadata` must describe an even-width, even-height NV12 frame. The conversion uses BT.709 studio-range NV12.

---

## `WriteTensorFromMFSampleNV12`

```cpp
HRESULT WriteTensorFromMFSampleNV12(
    [in] IUnknown* sample,
    [in] const WINML_IMAGE_TENSOR_DESC* desc,
    [in] const WINML_VIDEO_FRAME_METADATA* metadata,
    [in] IWinMLTensor* destination
);
```

Writes an NV12 Media Foundation sample into a caller-allocated tensor.

| Parameter | Description |
|---|---|
| `sample` | Source object, queryable for `IMFSample`. |
| `desc` | Image tensor descriptor that defines the conversion. |
| `metadata` | Source frame geometry and timing metadata. |
| `destination` | Destination tensor to populate. |

**Remarks:** `destination` must match the resolved tensor shape and data type. `metadata` must describe an even-width, even-height NV12 frame.

---

## `CreateMFSampleFromTensorNV12`

```cpp
HRESULT CreateMFSampleFromTensorNV12(
    [in] IWinMLTensor* tensor,
    [in] const WINML_IMAGE_TENSOR_DESC* desc,
    [in, unique] const WINML_VIDEO_FRAME_METADATA* metadata,
    [in, unique] IUnknown* mediaType,
    [out, retval] IUnknown** sample
);
```

Creates an NV12 Media Foundation sample from a tensor.

| Parameter | Description |
|---|---|
| `tensor` | Source tensor to pack into NV12. |
| `desc` | Image tensor descriptor that defines denormalization, dequantization, and channel-order interpretation. |
| `metadata` | Optional timestamp and duration metadata to copy onto the sample. |
| `mediaType` | Optional object queryable for `IMFMediaType`. When supplied, the allocator associated with that media type chooses the output buffer stride and alignment. |
| `sample` | Receives the created `IMFSample` as `IUnknown*`. |

**Remarks:** The source tensor must be rank 4 with batch size 1, even width and height, and three or four channels. If `mediaType` is supplied, it must describe NV12 video.

## Example

```cpp
wil::com_ptr<IWinMLImageTensorFactory> factory;
if (!factory) return S_OK;

const BYTE rgba[] = {
    255, 0, 0, 255,
    0, 255, 0, 255,
    0, 0, 255, 255,
    255, 255, 255, 255,
};
WINML_IMAGE_TENSOR_DESC desc{};
desc.dataType = WINML_TENSOR_DATA_TYPE_FLOAT32;
desc.layout = WINML_TENSOR_LAYOUT_FORMAT_NCHW;
desc.channelOrder = WINML_TENSOR_CHANNEL_ORDER_RGB;
desc.batchSize = 1;
desc.channels = 3;
desc.width = 2;
desc.height = 2;

wil::com_ptr<IWinMLTensor> tensor;
return factory->CreateTensorFromImageMemory(
    rgba,
    sizeof(rgba),
    2,
    2,
    2 * 4,
    DXGI_FORMAT_R8G8B8A8_UNORM,
    &desc,
    tensor.put());
```

---
