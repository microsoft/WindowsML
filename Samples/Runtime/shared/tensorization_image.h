// Copyright (C) Microsoft Corporation. All rights reserved.
//
// tensorization_image.h - WIC image decode helpers for vision samples.
//
// Pure sample-side file decode helpers. The winml::tensors adapter calls (wic::
// and mf::) stay in the samples so readers can see where image data becomes a
// Runtime tensor.

#pragma once

#include <cerrno>
#include <cstdio>
#include <cwchar>
#include <vector>

#include <Windows.h>
#include <wincodec.h>

#include "common.h" // ComPtr
#include "tensorization_common.h"

namespace winmlsamples
{
namespace tensors
{
namespace image
{

constexpr UINT32 kMaxImageDimension = 4096;

// Creates the process-wide WIC imaging factory.
inline HRESULT CreateWicFactory(_COM_Outptr_ IWICImagingFactory** factory) noexcept
{
    if (factory == nullptr)
    {
        return E_POINTER;
    }

    *factory = nullptr;
    return CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                            IID_PPV_ARGS(factory));
}

// Decodes a real image file (JPEG/PNG/...) into a 32bpp BGRA WIC bitmap scaled
// to width x height with a high-quality scaling filter.
inline HRESULT DecodeImageToBitmap(IWICImagingFactory* factory, const wchar_t* path, UINT32 width,
                                   UINT32 height, _COM_Outptr_ IWICBitmap** bitmap) noexcept
{
    if (factory == nullptr || path == nullptr || bitmap == nullptr)
    {
        return E_POINTER;
    }

    *bitmap = nullptr;
    if (width == 0 || height == 0 || width > kMaxImageDimension || height > kMaxImageDimension)
    {
        return E_INVALIDARG;
    }

    ComPtr<IWICBitmapDecoder> decoder;
    HRESULT hr = factory->CreateDecoderFromFilename(
        path, nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, decoder.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<IWICBitmapFrameDecode> frame;
    hr = decoder->GetFrame(0, frame.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<IWICFormatConverter> converter;
    hr = factory->CreateFormatConverter(converter.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }

    hr = converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone,
                               nullptr, 0.0, WICBitmapPaletteTypeCustom);
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<IWICBitmapScaler> scaler;
    hr = factory->CreateBitmapScaler(scaler.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }

    hr = scaler->Initialize(converter.Get(), width, height, WICBitmapInterpolationModeFant);
    if (FAILED(hr))
    {
        return hr;
    }

    // Materialize into an in-memory bitmap so the caller can Lock/CopyPixels it.
    return factory->CreateBitmapFromSource(scaler.Get(), WICBitmapCacheOnLoad, bitmap);
}

// Decodes a real image file into a tightly-packed 32bpp BGRA byte buffer
// (width * height * 4 bytes), scaled to width x height.
inline HRESULT DecodeImageToBgra(IWICImagingFactory* factory, const wchar_t* path, UINT32 width,
                                 UINT32 height, std::vector<BYTE>& bgra) noexcept
{
    ComPtr<IWICBitmap> bitmap;
    HRESULT hr = DecodeImageToBitmap(factory, path, width, height, bitmap.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }

    bgra.assign(static_cast<size_t>(width) * height * 4, 0);
    WICRect rect{0, 0, static_cast<INT>(width), static_cast<INT>(height)};
    return bitmap->CopyPixels(&rect, width * 4, static_cast<UINT>(bgra.size()), bgra.data());
}

} // namespace image
} // namespace tensors
} // namespace winmlsamples
