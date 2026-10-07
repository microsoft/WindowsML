<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLAudioTensorFactory

> Part of the [WinML Runtime API Reference](README.md).

**Target-bound factory for PCM audio tensorization.**

Obtain this interface with `QueryInterface` from [`IWinMLExecutionTarget`](IWinMLExecutionTarget.md).

`WINML_AUDIO_TENSOR_DESC` describes the audio tensor format: `dataType`, `layout`, `sampleRate`, `channels`, `windowFrames`, and `hopFrames`. Audio conversion supports `FLOAT32` and `FLOAT16` tensor data. See [Structures.md#winml_audio_tensor_desc](Structures.md#winml_audio_tensor_desc).

```
IID: f4b903e0-c0d8-410b-88ce-daef8054e582
```

## `CreateTensorFromPcm`

```cpp
HRESULT CreateTensorFromPcm(
    [in, size_is(byteCount)] const BYTE* samples,
    [in] UINT64 byteCount,
    [in, size_is(formatSize)] const BYTE* format,
    [in] UINT32 formatSize,
    [in] const WINML_AUDIO_TENSOR_DESC* desc,
    [out, retval] IWinMLTensor** tensor
);
```

Creates a tensor from PCM or IEEE-float audio bytes.

| Parameter | Description |
|---|---|
| `samples` | Source audio bytes. |
| `byteCount` | Size of `samples` in bytes. |
| `format` | WAVEFORMATEX or WAVEFORMATEXTENSIBLE byte blob that describes `samples`. |
| `formatSize` | Size of `format` in bytes. |
| `desc` | Audio tensor descriptor that defines the target tensor layout. |
| `tensor` | Receives the created tensor. |

**Remarks:** Supports PCM and IEEE-float `WAVEFORMATEX`/`WAVEFORMATEXTENSIBLE` data. This method is not a resampler; when `desc->sampleRate` is nonzero it must match the source sample rate. `WINML_TENSOR_LAYOUT_FORMAT_NT` is mono-only.

---

## `WriteTensorToPcm`

```cpp
HRESULT WriteTensorToPcm(
    [in] IWinMLTensor* tensor,
    [in, size_is(formatSize)] const BYTE* format,
    [in] UINT32 formatSize,
    [out, size_is(byteCount)] BYTE* samples,
    [in] UINT64 byteCount
);
```

Writes a tensor into a caller-supplied PCM or IEEE-float audio buffer.

| Parameter | Description |
|---|---|
| `tensor` | Source tensor to convert from. |
| `format` | WAVEFORMATEX or WAVEFORMATEXTENSIBLE byte blob that describes the destination buffer. |
| `formatSize` | Size of `format` in bytes. |
| `samples` | Destination audio buffer. |
| `byteCount` | Size of `samples` in bytes. |

**Remarks:** The tensor must be `FLOAT32` or `FLOAT16` and compatible with the destination channel count and sample representation described by `format`.

## Example

```cpp
wil::com_ptr<IWinMLAudioTensorFactory> factory;
if (!factory) return S_OK;

const INT16 samples[] = {0, 1000, -1000, 0};
WAVEFORMATEX format{};
format.wFormatTag = WAVE_FORMAT_PCM;
format.nChannels = 1;
format.nSamplesPerSec = 16000;
format.wBitsPerSample = 16;
format.nBlockAlign = format.nChannels * format.wBitsPerSample / 8;
format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

WINML_AUDIO_TENSOR_DESC desc{};
desc.dataType = WINML_TENSOR_DATA_TYPE_FLOAT32;
desc.layout = WINML_TENSOR_LAYOUT_FORMAT_NT;
desc.sampleRate = 16000;
desc.channels = 1;

wil::com_ptr<IWinMLTensor> tensor;
return factory->CreateTensorFromPcm(
    reinterpret_cast<const BYTE*>(samples),
    sizeof(samples),
    reinterpret_cast<const BYTE*>(&format),
    sizeof(format),
    &desc,
    tensor.put());
```

---
