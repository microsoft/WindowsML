// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Audio processing utilities for Windows ML Runtime speech samples.
//
// Pure sample-side code for WAV loading, Whisper-compatible log-mel feature
// computation, and microphone capture; Runtime audio tensorization is shown in
// the Python Whisper entry point.

#pragma once

#include <vector>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <string>
#include <algorithm>

#include <windows.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

// Whisper audio constants
static constexpr uint32_t kWhisperSampleRate = 16000;
static constexpr uint32_t kWhisperMelBins = 80;
static constexpr uint32_t kWhisperTimeFrames = 3000;                    // 30 seconds @ 100 fps
static constexpr uint32_t kWhisperMaxSamples = kWhisperSampleRate * 30; // 30 sec
static constexpr uint32_t kWhisperFFTSize = 400;                        // 25ms window @ 16kHz
static constexpr uint32_t kWhisperHopLength = 160;                      // 10ms hop @ 16kHz

struct MelSpectrogramData
{
    std::vector<float> data;
    uint32_t melBins;
    uint32_t timeFrames;
};

// WAV file reader - loads 16-bit PCM .wav files and resamples to 16kHz mono.

struct WavData
{
    std::vector<float> samples; // Mono, float32, normalized to [-1, 1]
    uint32_t sampleRate = 0;
    bool truncated = false;
};

#pragma pack(push, 1)
struct WavHeader
{
    char riff[4];
    uint32_t fileSize;
    char wave[4];
};

struct WavChunkHeader
{
    char id[4];
    uint32_t size;
};

struct WavFmtChunk
{
    uint16_t audioFormat;
    uint16_t numChannels;
    uint32_t sampleRate;
    uint32_t byteRate;
    uint16_t blockAlign;
    uint16_t bitsPerSample;
};
#pragma pack(pop)

inline HRESULT LoadWavFile(const wchar_t* path, WavData& output)
{
    constexpr uint64_t kMaximumWavDataBytes = 64ull * 1024ull * 1024ull;

    HANDLE hFile = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE)
        return HRESULT_FROM_WIN32(GetLastError());

    LARGE_INTEGER fileSize = {};
    if (!GetFileSizeEx(hFile, &fileSize))
    {
        const HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
        CloseHandle(hFile);
        return hr;
    }

    if (fileSize.QuadPart < static_cast<LONGLONG>(sizeof(WavHeader)))
    {
        CloseHandle(hFile);
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    const auto seekForward = [&](uint64_t byteCount) -> HRESULT {
        LARGE_INTEGER zero = {};
        LARGE_INTEGER position = {};
        if (!SetFilePointerEx(hFile, zero, &position, FILE_CURRENT))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        if (position.QuadPart < 0 ||
            byteCount > static_cast<uint64_t>(fileSize.QuadPart - position.QuadPart))
        {
            return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        }

        LARGE_INTEGER distance = {};
        distance.QuadPart = static_cast<LONGLONG>(byteCount);
        return SetFilePointerEx(hFile, distance, nullptr, FILE_CURRENT)
                   ? S_OK
                   : HRESULT_FROM_WIN32(GetLastError());
    };

    const auto remainingBytes = [&]() -> uint64_t {
        LARGE_INTEGER zero = {};
        LARGE_INTEGER position = {};
        if (!SetFilePointerEx(hFile, zero, &position, FILE_CURRENT) || position.QuadPart < 0 ||
            position.QuadPart > fileSize.QuadPart)
        {
            return 0;
        }

        return static_cast<uint64_t>(fileSize.QuadPart - position.QuadPart);
    };

    // Read the RIFF/WAVE header.
    WavHeader header = {};
    DWORD bytesRead = 0;
    if (!ReadFile(hFile, &header, sizeof(header), &bytesRead, nullptr) ||
        bytesRead < sizeof(WavHeader))
    {
        CloseHandle(hFile);
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    if (memcmp(header.riff, "RIFF", 4) != 0 || memcmp(header.wave, "WAVE", 4) != 0)
    {
        CloseHandle(hFile);
        return E_INVALIDARG;
    }

    // Find the fmt and data chunks.
    WavFmtChunk fmt = {};
    bool foundFmt = false;
    bool foundData = false;
    uint64_t dataOffset = 0;
    uint32_t dataSize = 0;
    std::vector<uint8_t> rawData;

    while (true)
    {
        WavChunkHeader chunk = {};
        if (!ReadFile(hFile, &chunk, sizeof(chunk), &bytesRead, nullptr) || bytesRead < 8)
            break;

        if (memcmp(chunk.id, "fmt ", 4) == 0)
        {
            if (chunk.size > remainingBytes())
            {
                CloseHandle(hFile);
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            }

            DWORD fmtReadSize = std::min(chunk.size, (uint32_t)sizeof(fmt));
            if (!ReadFile(hFile, &fmt, fmtReadSize, &bytesRead, nullptr) || bytesRead < fmtReadSize)
            {
                CloseHandle(hFile);
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            }

            if (chunk.size > sizeof(fmt))
            {
                const HRESULT hr = seekForward(chunk.size - sizeof(fmt));
                if (FAILED(hr))
                {
                    CloseHandle(hFile);
                    return hr;
                }
            }

            foundFmt = true;
        }
        else if (memcmp(chunk.id, "data", 4) == 0)
        {
            if (chunk.size == 0 || chunk.size > remainingBytes())
            {
                CloseHandle(hFile);
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            }

            LARGE_INTEGER zero = {};
            LARGE_INTEGER position = {};
            if (!SetFilePointerEx(hFile, zero, &position, FILE_CURRENT) || position.QuadPart < 0)
            {
                CloseHandle(hFile);
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            }

            if (!foundData)
            {
                foundData = true;
                dataOffset = static_cast<uint64_t>(position.QuadPart);
                dataSize = chunk.size;
            }

            const HRESULT hr = seekForward(chunk.size);
            if (FAILED(hr))
            {
                CloseHandle(hFile);
                return hr;
            }
        }
        else
        {
            const HRESULT hr = seekForward(chunk.size);
            if (FAILED(hr))
            {
                CloseHandle(hFile);
                return hr;
            }
        }

        if ((chunk.size & 1u) != 0)
        {
            const HRESULT hr = seekForward(1);
            if (FAILED(hr))
            {
                CloseHandle(hFile);
                return hr;
            }
        }
    }

    if (!foundFmt || !foundData)
    {
        CloseHandle(hFile);
        return E_INVALIDARG;
    }

    // Accept 16-bit PCM (audioFormat==1) and 32-bit IEEE float (audioFormat==3).
    // Reject 32-bit integer PCM (audioFormat==1, bps==32) which would produce
    // garbage if reinterpreted as float.
    const bool is16BitPcm = (fmt.audioFormat == 1 && fmt.bitsPerSample == 16);
    const bool is32BitFloat = (fmt.audioFormat == 3 && fmt.bitsPerSample == 32);
    if (!is16BitPcm && !is32BitFloat)
    {
        CloseHandle(hFile);
        return E_INVALIDARG;
    }

    if (fmt.numChannels == 0 || fmt.sampleRate == 0)
    {
        CloseHandle(hFile);
        return E_INVALIDARG;
    }

    const uint32_t expectedBlockAlign = fmt.numChannels * (fmt.bitsPerSample / 8);
    if (fmt.blockAlign != expectedBlockAlign || dataSize % expectedBlockAlign != 0)
    {
        CloseHandle(hFile);
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    const uint64_t availableFrames = dataSize / expectedBlockAlign;
    const uint64_t maximumSourceFrames =
        (static_cast<uint64_t>(kWhisperMaxSamples) * fmt.sampleRate + kWhisperSampleRate - 1) /
        kWhisperSampleRate;
    const uint64_t frameCount = std::min(availableFrames, maximumSourceFrames);
    const uint64_t byteCount = frameCount * expectedBlockAlign;
    if (frameCount == 0 || byteCount > kMaximumWavDataBytes || byteCount > MAXDWORD)
    {
        CloseHandle(hFile);
        return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
    }

    LARGE_INTEGER dataPosition = {};
    dataPosition.QuadPart = static_cast<LONGLONG>(dataOffset);
    if (!SetFilePointerEx(hFile, dataPosition, nullptr, FILE_BEGIN))
    {
        const HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
        CloseHandle(hFile);
        return hr;
    }

    rawData.resize(static_cast<size_t>(byteCount));
    if (!ReadFile(hFile, rawData.data(), static_cast<DWORD>(byteCount), &bytesRead, nullptr) ||
        bytesRead != byteCount)
    {
        CloseHandle(hFile);
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    CloseHandle(hFile);

    // Convert source PCM to normalized float32 mono.
    size_t numSamples = rawData.size() / (fmt.bitsPerSample / 8) / fmt.numChannels;
    output.samples.resize(numSamples);
    output.sampleRate = fmt.sampleRate;
    output.truncated = availableFrames > frameCount;

    if (fmt.bitsPerSample == 16)
    {
        for (size_t i = 0; i < numSamples; ++i)
        {
            float sum = 0.0f;
            for (uint16_t ch = 0; ch < fmt.numChannels; ++ch)
            {
                int16_t value = 0;
                const size_t offset = (i * fmt.numChannels + ch) * sizeof(value);
                std::memcpy(&value, rawData.data() + offset, sizeof(value));
                sum += static_cast<float>(value) / 32768.0f;
            }

            output.samples[i] = sum / fmt.numChannels;
        }
    }
    else if (fmt.bitsPerSample == 32)
    {
        for (size_t i = 0; i < numSamples; ++i)
        {
            float sum = 0.0f;
            for (uint16_t ch = 0; ch < fmt.numChannels; ++ch)
            {
                float value = 0.0f;
                const size_t offset = (i * fmt.numChannels + ch) * sizeof(value);
                std::memcpy(&value, rawData.data() + offset, sizeof(value));
                sum += value;
            }

            output.samples[i] = sum / fmt.numChannels;
        }
    }

    // Resample to 16 kHz if needed with linear interpolation.
    if (fmt.sampleRate != kWhisperSampleRate)
    {
        double ratio = static_cast<double>(kWhisperSampleRate) / fmt.sampleRate;
        size_t newLen = static_cast<size_t>(numSamples * ratio);
        output.truncated = output.truncated || newLen > kWhisperMaxSamples;
        newLen = std::min<size_t>(newLen, kWhisperMaxSamples);
        std::vector<float> resampled(newLen);
        for (size_t i = 0; i < newLen; ++i)
        {
            double srcIdx = i / ratio;
            size_t idx0 = static_cast<size_t>(srcIdx);
            size_t idx1 = std::min(idx0 + 1, numSamples - 1);
            float frac = static_cast<float>(srcIdx - idx0);
            resampled[i] = output.samples[idx0] * (1.0f - frac) + output.samples[idx1] * frac;
        }

        output.samples = std::move(resampled);
        output.sampleRate = kWhisperSampleRate;
    }

    return S_OK;
}

// Mel spectrogram computation (Whisper-compatible).
//
// Computes log-mel spectrogram from PCM audio:
//   PCM float32 -> Hann-windowed STFT -> power spectrum -> mel filterbank -> log

// Compute the power spectrum of a windowed frame via a direct real-input DFT.
//
// Whisper's STFT uses n_fft = 400, which is not a power of two. A radix-2 FFT would
// have to zero-pad to 512 bins, shifting every frequency bin and breaking alignment
// with the precomputed 400-point mel filterbank. Computing the DFT over exactly
// fftSize points keeps bin k at frequency k * sampleRate / fftSize, which is what the
// filterbank expects. Only the first fftSize/2 + 1 bins are needed for real input.
// The twiddle factors are cached on first use, so the per-frame cost is a plain
// multiply-accumulate over the cached cosine/sine tables.
inline void ComputePowerSpectrum(const float* windowed, uint32_t fftSize, std::vector<float>& power)
{
    const uint32_t halfN = fftSize / 2 + 1;

    // Not thread-safe: static twiddle tables assume single-threaded mel computation
    static uint32_t cachedSize = 0;
    static std::vector<float> cosTable; // [halfN * fftSize]
    static std::vector<float> sinTable; // [halfN * fftSize]
    if (cachedSize != fftSize)
    {
        cosTable.assign(static_cast<size_t>(halfN) * fftSize, 0.0f);
        sinTable.assign(static_cast<size_t>(halfN) * fftSize, 0.0f);
        const double twoPiOverN = -2.0 * M_PI / static_cast<double>(fftSize);
        for (uint32_t k = 0; k < halfN; ++k)
        {
            for (uint32_t i = 0; i < fftSize; ++i)
            {
                const double angle = twoPiOverN * static_cast<double>(k) * static_cast<double>(i);
                cosTable[static_cast<size_t>(k) * fftSize + i] =
                    static_cast<float>(std::cos(angle));
                sinTable[static_cast<size_t>(k) * fftSize + i] =
                    static_cast<float>(std::sin(angle));
            }
        }

        cachedSize = fftSize;
    }

    power.resize(halfN);
    for (uint32_t k = 0; k < halfN; ++k)
    {
        const float* cosRow = &cosTable[static_cast<size_t>(k) * fftSize];
        const float* sinRow = &sinTable[static_cast<size_t>(k) * fftSize];
        float sumRe = 0.0f;
        float sumIm = 0.0f;
        for (uint32_t i = 0; i < fftSize; ++i)
        {
            sumRe += windowed[i] * cosRow[i];
            sumIm += windowed[i] * sinRow[i];
        }

        power[k] = sumRe * sumRe + sumIm * sumIm;
    }
}

// Whisper's precomputed mel filterbank (sparse representation).
#include "../speech/whisper/whisper_mel_filterbank.h"

// Apply Whisper's mel filterbank using precomputed sparse entries.
inline void ApplyWhisperMelFilterbank(const std::vector<float>& power, uint32_t timeFrame,
                                      uint32_t timeFrames, float* melOutput)
{
    for (uint32_t i = 0; i < kWhisperMelFilterEntryCount; ++i)
    {
        const auto& entry = kWhisperMelFilters[i];
        if (entry.fftBin < power.size())
        {
            melOutput[static_cast<size_t>(entry.mel) * timeFrames + timeFrame] +=
                entry.weight * power[entry.fftBin];
        }
    }
}

inline size_t ReflectWhisperSampleIndex(int64_t index, size_t sampleCount)
{
    if (index < 0)
    {
        index = -index;
    }
    else if (index >= static_cast<int64_t>(sampleCount))
    {
        index = static_cast<int64_t>(sampleCount) * 2 - index - 2;
    }

    return static_cast<size_t>(index);
}

// Compute Whisper-compatible log-mel spectrogram from PCM audio.
// Uses the precomputed sparse mel filterbank for matching feature layout.
//   centered STFT (n_fft=400, hop=160) -> power -> mel -> log10 -> normalize
inline MelSpectrogramData ComputeMelSpectrogram(const float* pcmSamples, size_t numSamples,
                                                uint32_t /*sampleRate*/ = kWhisperSampleRate)
{
    // Pad or trim to Whisper's 30-second input window.
    std::vector<float> audio(kWhisperMaxSamples, 0.0f);
    size_t copyLen = std::min(numSamples, static_cast<size_t>(kWhisperMaxSamples));
    std::copy(pcmSamples, pcmSamples + copyLen, audio.begin());

    // Build the periodic Hann window used by the Whisper feature extractor.
    std::vector<float> hannWindow(kWhisperFFTSize);
    for (uint32_t i = 0; i < kWhisperFFTSize; ++i)
    {
        hannWindow[i] =
            0.5f * (1.0f - std::cos(2.0f * static_cast<float>(M_PI) * i / kWhisperFFTSize));
    }

    MelSpectrogramData result;
    result.melBins = kWhisperMelBins;
    result.timeFrames = kWhisperTimeFrames;
    result.data.resize(static_cast<size_t>(kWhisperMelBins) * kWhisperTimeFrames, 0.0f);

    std::vector<float> windowed(kWhisperFFTSize);
    std::vector<float> power;

    for (uint32_t t = 0; t < kWhisperTimeFrames; ++t)
    {
        const int64_t offset =
            static_cast<int64_t>(t) * kWhisperHopLength - static_cast<int64_t>(kWhisperFFTSize / 2);

        for (uint32_t i = 0; i < kWhisperFFTSize; ++i)
        {
            const size_t index =
                ReflectWhisperSampleIndex(offset + static_cast<int64_t>(i), audio.size());
            windowed[i] = audio[index] * hannWindow[i];
        }

        ComputePowerSpectrum(windowed.data(), kWhisperFFTSize, power);
        ApplyWhisperMelFilterbank(power, t, kWhisperTimeFrames, result.data.data());
    }

    // Log10 plus Whisper normalization.
    for (float& v : result.data)
    {
        v = std::log10(std::max(v, 1e-10f));
    }

    float maxVal = -1e10f;
    for (float v : result.data)
    {
        if (v > maxVal)
        {
            maxVal = v;
        }
    }

    for (float& v : result.data)
    {
        if (v < maxVal - 8.0f)
        {
            v = maxVal - 8.0f;
        }

        v = (v + 4.0f) / 4.0f;
    }

    return result;
}

// Microphone capture via Windows Audio Session API (WASAPI).
// Captures until *stopSignal becomes true, or maxSeconds is reached.
// Pass stopSignal=nullptr for fixed-duration capture.

#include <mmdeviceapi.h>
#include <audioclient.h>
#include <objbase.h>
#include <memory>
#include <wrl/client.h>

#pragma comment(lib, "ole32.lib")

inline HRESULT CaptureFromMicrophone(float maxSeconds, WavData& output,
                                     const volatile bool* stopSignal = nullptr)
{
    struct WasapiComInit
    {
        WasapiComInit()
        {
            hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            if (hr == RPC_E_CHANGED_MODE)
            {
                hr = S_OK;
                return;
            }

            initialized = SUCCEEDED(hr);
        }

        ~WasapiComInit()
        {
            if (initialized)
            {
                CoUninitialize();
            }
        }

        HRESULT hr = S_OK;
        bool initialized = false;
    } comInit;

    HRESULT hr = comInit.hr;
    if (FAILED(hr))
        return hr;

    using Microsoft::WRL::ComPtr;

    ComPtr<IMMDeviceEnumerator> enumerator;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                          IID_PPV_ARGS(enumerator.GetAddressOf()));
    if (FAILED(hr))
        return hr;

    ComPtr<IMMDevice> device;
    hr = enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, device.GetAddressOf());
    if (FAILED(hr))
        return hr;

    ComPtr<IAudioClient> audioClient;
    hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                          reinterpret_cast<void**>(audioClient.GetAddressOf()));
    if (FAILED(hr))
        return hr;

    WAVEFORMATEX* mixFormatRaw = nullptr;
    hr = audioClient->GetMixFormat(&mixFormatRaw);
    if (FAILED(hr))
        return hr;
    std::unique_ptr<WAVEFORMATEX, decltype(&CoTaskMemFree)> mixFormat(mixFormatRaw, CoTaskMemFree);
    const WAVEFORMATEX* format = mixFormat.get();

    REFERENCE_TIME bufferDuration = static_cast<REFERENCE_TIME>(maxSeconds * 10000000);
    // Cap the buffer request at the 30-second WASAPI maximum.
    if (bufferDuration > 300000000LL)
        bufferDuration = 300000000LL;

    hr = audioClient->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, bufferDuration, 0, format, nullptr);
    if (FAILED(hr))
        return hr;

    ComPtr<IAudioCaptureClient> captureClient;
    hr = audioClient->GetService(__uuidof(IAudioCaptureClient),
                                 reinterpret_cast<void**>(captureClient.GetAddressOf()));
    if (FAILED(hr))
        return hr;

    const size_t maxSamples = static_cast<size_t>(maxSeconds * format->nSamplesPerSec);
    output.samples.clear();
    output.samples.reserve(std::min(maxSamples, static_cast<size_t>(format->nSamplesPerSec * 30)));
    output.sampleRate = format->nSamplesPerSec;

    hr = audioClient->Start();
    if (FAILED(hr))
        return hr;

    const ULONGLONG startTick = GetTickCount64();
    const ULONGLONG maxMs = static_cast<ULONGLONG>(maxSeconds * 1000.0f);

    while (output.samples.size() < maxSamples)
    {
        if (stopSignal && *stopSignal)
            break;
        // Wall-clock bound so a silent or stalled device cannot capture forever.
        if (GetTickCount64() - startTick >= maxMs)
            break;

        Sleep(10);
        UINT32 packetLength = 0;
        if (FAILED(captureClient->GetNextPacketSize(&packetLength)))
            break;

        while (packetLength > 0)
        {
            BYTE* pData = nullptr;
            UINT32 numFrames = 0;
            DWORD flags = 0;
            hr = captureClient->GetBuffer(&pData, &numFrames, &flags, nullptr, nullptr);
            if (FAILED(hr))
                break;

            if (flags & AUDCLNT_BUFFERFLAGS_SILENT)
            {
                for (UINT32 i = 0; i < numFrames; ++i)
                    output.samples.push_back(0.0f);
            }
            else if (format->wBitsPerSample == 32)
            {
                const float* src = reinterpret_cast<const float*>(pData);
                for (UINT32 i = 0; i < numFrames; ++i)
                {
                    float sum = 0.0f;
                    for (uint16_t ch = 0; ch < format->nChannels; ++ch)
                        sum += src[i * format->nChannels + ch];
                    output.samples.push_back(sum / format->nChannels);
                }
            }
            else if (format->wBitsPerSample == 16)
            {
                const int16_t* src = reinterpret_cast<const int16_t*>(pData);
                for (UINT32 i = 0; i < numFrames; ++i)
                {
                    float sum = 0.0f;
                    for (uint16_t ch = 0; ch < format->nChannels; ++ch)
                        sum += static_cast<float>(src[i * format->nChannels + ch]) / 32768.0f;
                    output.samples.push_back(sum / format->nChannels);
                }
            }

            captureClient->ReleaseBuffer(numFrames);
            if (FAILED(captureClient->GetNextPacketSize(&packetLength)))
                break;
        }
    }

    audioClient->Stop();

    // Resample to 16 kHz if needed.
    if (output.sampleRate != kWhisperSampleRate)
    {
        double ratio = static_cast<double>(kWhisperSampleRate) / output.sampleRate;
        size_t newLen = static_cast<size_t>(output.samples.size() * ratio);
        std::vector<float> resampled(newLen);
        for (size_t i = 0; i < newLen; ++i)
        {
            double srcIdx = i / ratio;
            size_t idx0 = static_cast<size_t>(srcIdx);
            size_t idx1 = std::min(idx0 + 1, output.samples.size() - 1);
            float frac = static_cast<float>(srcIdx - idx0);
            resampled[i] = output.samples[idx0] * (1.0f - frac) + output.samples[idx1] * frac;
        }

        output.samples = std::move(resampled);
        output.sampleRate = kWhisperSampleRate;
    }

    return S_OK;
}
