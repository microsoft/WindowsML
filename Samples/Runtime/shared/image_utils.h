// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Image file decode, PNG writing, and label utilities for Runtime vision samples.
// Pure sample-side helpers; Runtime image tensor factories are shown elsewhere.

#pragma once

#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <cmath>
#include <fstream>
#include <limits>
#include <string>
#include <vector>

#include <windows.h>

struct ImageTensorData
{
    std::vector<float> data;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t channels = 0;
};

struct NormalizationParams
{
    float mean[3]; // Per-channel mean (RGB order)
    float std[3];  // Per-channel std  (RGB order)
};

constexpr NormalizationParams kImageNetNorm = {{0.485f, 0.456f, 0.406f}, {0.229f, 0.224f, 0.225f}};

constexpr NormalizationParams kUnitNorm = {{0.0f, 0.0f, 0.0f}, {1.0f, 1.0f, 1.0f}};

// Loads newline-delimited class labels where the line index is the class id.
inline std::vector<std::wstring> LoadClassLabels(const std::wstring& path)
{
    std::vector<std::wstring> labels;
    std::ifstream file(path, std::ios::binary);
    if (!file.is_open())
    {
        return labels;
    }

    std::string line;
    while (std::getline(file, line))
    {
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }

        int needed = MultiByteToWideChar(CP_UTF8, 0, line.c_str(), -1, nullptr, 0);
        std::wstring wide;
        if (needed > 1)
        {
            wide.resize(static_cast<size_t>(needed) - 1);
            MultiByteToWideChar(CP_UTF8, 0, line.c_str(), -1, wide.data(), needed);
        }

        labels.push_back(std::move(wide));
    }

    return labels;
}

inline const wchar_t* ClassLabel(const std::vector<std::wstring>& labels, int index)
{
    if (index < 0 || static_cast<size_t>(index) >= labels.size())
    {
        return L"(unknown class)";
    }

    return labels[static_cast<size_t>(index)].c_str();
}

// Minimal, dependency-free PNG writer (8-bit RGB). Uses stored (uncompressed)
// DEFLATE blocks so it needs no zlib -- enough to persist a generated image
// without pulling WIC/COM into the sample. `rgb` is tightly packed HWC (row-major
// R,G,B per pixel). Returns S_OK on success.
namespace winml_png_detail
{
inline uint32_t Crc32(const uint8_t* data, size_t length, uint32_t crc = 0xFFFFFFFFu)
{
    for (size_t i = 0; i < length; ++i)
    {
        crc ^= data[i];
        for (int bit = 0; bit < 8; ++bit)
        {
            crc = (crc >> 1) ^ (0xEDB88320u & (~((crc & 1u) - 1u)));
        }
    }

    return crc;
}

inline void PushBigEndian32(std::vector<uint8_t>& out, uint32_t value)
{
    out.push_back(static_cast<uint8_t>((value >> 24) & 0xFF));
    out.push_back(static_cast<uint8_t>((value >> 16) & 0xFF));
    out.push_back(static_cast<uint8_t>((value >> 8) & 0xFF));
    out.push_back(static_cast<uint8_t>(value & 0xFF));
}

inline void WriteChunk(std::vector<uint8_t>& out, const char tag[4],
                       const std::vector<uint8_t>& payload)
{
    PushBigEndian32(out, static_cast<uint32_t>(payload.size()));
    const size_t crcStart = out.size();
    out.insert(out.end(), tag, tag + 4);
    out.insert(out.end(), payload.begin(), payload.end());
    const uint32_t crc = Crc32(out.data() + crcStart, out.size() - crcStart) ^ 0xFFFFFFFFu;
    PushBigEndian32(out, crc);
}
} // namespace winml_png_detail

inline HRESULT SaveRgbToPng(const std::wstring& path, const std::vector<uint8_t>& rgb,
                            uint32_t width, uint32_t height)
{
    if (width == 0 || height == 0 || rgb.size() != static_cast<size_t>(width) * height * 3)
    {
        return E_INVALIDARG;
    }

    using namespace winml_png_detail;

    // Raw scanlines: each row is prefixed with a filter byte (0 = none).
    std::vector<uint8_t> raw;
    raw.reserve(static_cast<size_t>(height) * (1 + static_cast<size_t>(width) * 3));
    for (uint32_t y = 0; y < height; ++y)
    {
        raw.push_back(0);
        const size_t rowStart = static_cast<size_t>(y) * width * 3;
        raw.insert(raw.end(), rgb.begin() + rowStart,
                   rgb.begin() + rowStart + static_cast<size_t>(width) * 3);
    }

    // zlib stream wrapping `raw` in stored DEFLATE blocks.
    std::vector<uint8_t> zlib;
    zlib.push_back(0x78); // CMF
    zlib.push_back(0x01); // FLG (no preset dict, fastest)
    size_t offset = 0;
    while (offset < raw.size())
    {
        const size_t blockLen = (std::min)(raw.size() - offset, static_cast<size_t>(0xFFFF));
        const bool finalBlock = (offset + blockLen) >= raw.size();
        zlib.push_back(finalBlock ? 1 : 0); // BFINAL, BTYPE=00 (stored)
        zlib.push_back(static_cast<uint8_t>(blockLen & 0xFF));
        zlib.push_back(static_cast<uint8_t>((blockLen >> 8) & 0xFF));
        const uint16_t nlen = static_cast<uint16_t>(~blockLen);
        zlib.push_back(static_cast<uint8_t>(nlen & 0xFF));
        zlib.push_back(static_cast<uint8_t>((nlen >> 8) & 0xFF));
        zlib.insert(zlib.end(), raw.begin() + offset, raw.begin() + offset + blockLen);
        offset += blockLen;
    }

    // Adler-32 of the raw data.
    uint32_t a = 1, b = 0;
    for (uint8_t byte : raw)
    {
        a = (a + byte) % 65521;
        b = (b + a) % 65521;
    }

    PushBigEndian32(zlib, (b << 16) | a);

    std::vector<uint8_t> png;
    const uint8_t signature[8] = {0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A};
    png.insert(png.end(), signature, signature + 8);

    std::vector<uint8_t> ihdr;
    PushBigEndian32(ihdr, width);
    PushBigEndian32(ihdr, height);
    ihdr.push_back(8); // bit depth
    ihdr.push_back(2); // color type: RGB
    ihdr.push_back(0); // compression
    ihdr.push_back(0); // filter
    ihdr.push_back(0); // interlace
    WriteChunk(png, "IHDR", ihdr);
    WriteChunk(png, "IDAT", zlib);
    WriteChunk(png, "IEND", {});

    std::ofstream file(path, std::ios::binary);
    if (!file)
    {
        return HRESULT_FROM_WIN32(ERROR_OPEN_FAILED);
    }

    file.write(reinterpret_cast<const char*>(png.data()), static_cast<std::streamsize>(png.size()));
    return file.good() ? S_OK : E_FAIL;
}

inline HRESULT LoadImageToNCHW(const wchar_t* imagePath, uint32_t targetWidth,
                               uint32_t targetHeight, const NormalizationParams& norm,
                               ImageTensorData& output)
{
    if (!imagePath || targetWidth == 0 || targetHeight == 0)
    {
        return E_INVALIDARG;
    }

    std::ifstream file(imagePath, std::ios::binary);
    if (!file)
    {
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }

    auto readToken = [&file](std::string& token) -> bool {
        token.clear();

        char ch = 0;
        while (file.get(ch))
        {
            if (ch == '#')
            {
                file.ignore(std::numeric_limits<std::streamsize>::max(), '\n');
                continue;
            }

            if (!std::isspace(static_cast<unsigned char>(ch)))
            {
                token.push_back(ch);
                break;
            }
        }

        while (file.get(ch))
        {
            if (std::isspace(static_cast<unsigned char>(ch)))
            {
                break;
            }

            token.push_back(ch);
        }

        return !token.empty();
    };

    auto parseUInt32 = [](const std::string& token, uint32_t& value) -> bool {
        char* end = nullptr;
        unsigned long parsed = std::strtoul(token.c_str(), &end, 10);
        if (!end || *end != '\0' || parsed > UINT32_MAX)
        {
            return false;
        }

        value = static_cast<uint32_t>(parsed);
        return true;
    };

    std::string token;
    if (!readToken(token) || token != "P6")
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    if (!readToken(token))
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    uint32_t sourceWidth = 0;
    if (!parseUInt32(token, sourceWidth))
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    if (!readToken(token))
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    uint32_t sourceHeight = 0;
    if (!parseUInt32(token, sourceHeight))
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    if (!readToken(token) || token != "255" || sourceWidth == 0 || sourceHeight == 0)
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    constexpr uint32_t kMaxPpmDimension = 8192;
    constexpr size_t kMaxPpmPixels = 64ull * 1024ull * 1024ull;
    if (sourceWidth > kMaxPpmDimension || sourceHeight > kMaxPpmDimension)
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    const size_t sourcePixelCount = static_cast<size_t>(sourceWidth) * sourceHeight;
    const size_t targetPixelCount = static_cast<size_t>(targetWidth) * targetHeight;
    if (sourcePixelCount > kMaxPpmPixels ||
        sourcePixelCount > (std::numeric_limits<size_t>::max() / 3) ||
        targetPixelCount > (std::numeric_limits<size_t>::max() / 3))
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    const size_t pixelBytes = sourcePixelCount * 3;
    if (pixelBytes > static_cast<size_t>(std::numeric_limits<std::streamsize>::max()))
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    std::vector<uint8_t> pixels(pixelBytes);
    file.read(reinterpret_cast<char*>(pixels.data()), static_cast<std::streamsize>(pixelBytes));
    if (file.gcount() != static_cast<std::streamsize>(pixelBytes))
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    output.width = targetWidth;
    output.height = targetHeight;
    output.channels = 3;
    output.data.resize(targetPixelCount * 3);

    const size_t planeSize = static_cast<size_t>(targetWidth) * targetHeight;
    for (uint32_t y = 0; y < targetHeight; ++y)
    {
        const uint32_t sourceY = (static_cast<uint64_t>(y) * sourceHeight) / targetHeight;
        for (uint32_t x = 0; x < targetWidth; ++x)
        {
            const uint32_t sourceX = (static_cast<uint64_t>(x) * sourceWidth) / targetWidth;
            const size_t hwcIdx = (static_cast<size_t>(sourceY) * sourceWidth + sourceX) * 3;
            for (uint32_t c = 0; c < 3; ++c)
            {
                float pixel = static_cast<float>(pixels[hwcIdx + c]) / 255.0f;
                float normalized = (pixel - norm.mean[c]) / norm.std[c];
                output.data[c * planeSize + y * targetWidth + x] = normalized;
            }
        }
    }

    return S_OK;
}
