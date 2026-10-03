// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Media Foundation NV12 helpers for Runtime vision samples.
//
// Pure sample-side helpers that build system-memory NV12 IMFSample objects; the
// Runtime Media Foundation tensor calls stay in the samples that use them.

#pragma once

#include <vector>

#include <Windows.h>
#include <mfapi.h>
#include <mfobjects.h>

#include "tensorization_image.h" // image::DecodeImageToBgra (real-pixel decode)

namespace winmlsamples
{
namespace tensors
{
namespace video
{

// Build a contiguous system-memory NV12 IMFSample from an existing NV12 byte
// buffer (the shape a software decoder / source reader emits after a CPU copy).
// Caller releases *outSample.
inline HRESULT CreateNv12SampleFromBytes(const std::vector<BYTE>& nv12,
                                         _COM_Outptr_ IMFSample** outSample) noexcept
{
    *outSample = nullptr;

    IMFMediaBuffer* buffer = nullptr;
    HRESULT hr = MFCreateMemoryBuffer(static_cast<DWORD>(nv12.size()), &buffer);
    if (FAILED(hr))
    {
        return hr;
    }

    BYTE* raw = nullptr;
    DWORD maxLen = 0;
    DWORD curLen = 0;
    hr = buffer->Lock(&raw, &maxLen, &curLen);
    if (SUCCEEDED(hr))
    {
        memcpy(raw, nv12.data(), nv12.size());
        buffer->Unlock();
        hr = buffer->SetCurrentLength(static_cast<DWORD>(nv12.size()));
    }

    IMFSample* sample = nullptr;
    if (SUCCEEDED(hr))
    {
        hr = MFCreateSample(&sample);
    }

    if (SUCCEEDED(hr))
    {
        hr = sample->AddBuffer(buffer);
    }

    buffer->Release();

    if (SUCCEEDED(hr))
    {
        *outSample = sample;
    }
    else if (sample != nullptr)
    {
        sample->Release();
    }

    return hr;
}

// Converts a tightly-packed 32bpp BGRA buffer into NV12 (BT.601 studio swing,
// 4:2:0). This is the CPU color conversion a software camera pipeline performs
// before handing frames downstream; the samples use it to turn a real decoded
// photo into the NV12 an IMFSample carries. width and height must be even.
inline void BgraToNv12(const std::vector<BYTE>& bgra, UINT32 width, UINT32 height,
                       std::vector<BYTE>& nv12) noexcept
{
    const size_t ySize = static_cast<size_t>(width) * height;
    const size_t uvSize = ySize / 2;
    nv12.assign(ySize + uvSize, 0);

    auto clampByte = [](double v) noexcept -> BYTE {
        if (v < 0.0)
        {
            v = 0.0;
        }

        if (v > 255.0)
        {
            v = 255.0;
        }

        return static_cast<BYTE>(v + 0.5);
    };

    // Luma for every pixel.
    for (UINT32 y = 0; y < height; ++y)
    {
        for (UINT32 x = 0; x < width; ++x)
        {
            const BYTE* p = bgra.data() + (static_cast<size_t>(y) * width + x) * 4;
            const double b = p[0];
            const double g = p[1];
            const double r = p[2];
            nv12[static_cast<size_t>(y) * width + x] =
                clampByte(0.257 * r + 0.504 * g + 0.098 * b + 16.0);
        }
    }

    // Chroma, averaged over each 2x2 block (4:2:0).
    BYTE* uv = nv12.data() + ySize;
    for (UINT32 y = 0; y < height; y += 2)
    {
        for (UINT32 x = 0; x < width; x += 2)
        {
            double rSum = 0.0, gSum = 0.0, bSum = 0.0;
            for (UINT32 dy = 0; dy < 2; ++dy)
            {
                for (UINT32 dx = 0; dx < 2; ++dx)
                {
                    const BYTE* p =
                        bgra.data() + (static_cast<size_t>(y + dy) * width + (x + dx)) * 4;
                    bSum += p[0];
                    gSum += p[1];
                    rSum += p[2];
                }
            }

            const double r = rSum / 4.0;
            const double g = gSum / 4.0;
            const double b = bSum / 4.0;
            const size_t uvIndex = (static_cast<size_t>(y) / 2) * width + x;
            uv[uvIndex] = clampByte(-0.148 * r - 0.291 * g + 0.439 * b + 128.0);    // U
            uv[uvIndex + 1] = clampByte(0.439 * r - 0.368 * g - 0.071 * b + 128.0); // V
        }
    }
}

// Decodes a real image file into an NV12 byte buffer scaled to width x height.
// width and height must be even (NV12 4:2:0 requirement).
inline HRESULT DecodeImageToNv12(IWICImagingFactory* factory, const wchar_t* path, UINT32 width,
                                 UINT32 height, std::vector<BYTE>& nv12) noexcept
{
    if ((width & 1u) != 0 || (height & 1u) != 0)
    {
        return E_INVALIDARG;
    }

    std::vector<BYTE> bgra;
    HRESULT hr = image::DecodeImageToBgra(factory, path, width, height, bgra);
    if (FAILED(hr))
    {
        return hr;
    }

    BgraToNv12(bgra, width, height, nv12);
    return S_OK;
}

// Decodes a real image file into a system-memory NV12 IMFSample scaled to
// width x height. Caller releases *outSample.
inline HRESULT CreateNv12SampleFromImage(IWICImagingFactory* factory, const wchar_t* path,
                                         UINT32 width, UINT32 height,
                                         _COM_Outptr_ IMFSample** outSample) noexcept
{
    if (outSample == nullptr)
    {
        return E_POINTER;
    }

    *outSample = nullptr;
    std::vector<BYTE> nv12;
    HRESULT hr = DecodeImageToNv12(factory, path, width, height, nv12);
    if (FAILED(hr))
    {
        return hr;
    }

    return CreateNv12SampleFromBytes(nv12, outSample);
}

} // namespace video
} // namespace tensors
} // namespace winmlsamples
