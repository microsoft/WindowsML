// Copyright (C) Microsoft Corporation. All rights reserved.
//
// WAV loading and Whisper waveform tensors for the C# speech samples. Matches
// LoadWavFile in ..\audio_utils.h and CreateWhisperWaveformTensorFromPcm in
// the package's winml\tasks\WinMLTasks.hpp, including the HRESULTs they
// report, so the C# and C++ samples fail alike.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace WinMLSamples;

// Mono samples normalized to [-1, 1]. Truncated is true when the file held
// more than WhisperAudio.MaxSamples of audio and the rest was dropped.
internal sealed record WavData(float[] Samples, uint SampleRate, bool Truncated);

internal static class WhisperAudio
{
    public const uint SampleRate = 16000;

    // 30 seconds at 16 kHz.
    public const uint MaxSamples = SampleRate * 30;

    private const long MaximumWavDataBytes = 64L * 1024 * 1024;

    // HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE).
    private const int FileTooLarge = unchecked((int)0x800700DF);

    // Loads a 16-bit PCM or 32-bit float WAV file, averages its channels to
    // mono, keeps at most 30 seconds, and resamples it to 16 kHz with linear
    // interpolation. Throws ArgumentException (E_INVALIDARG) for a file that
    // is not a WAV file in a supported format, InvalidDataException
    // (HRESULT_FROM_WIN32(ERROR_INVALID_DATA)) for a malformed one, and the
    // file system's exception when the file cannot be opened.
    public static WavData LoadWavFile(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
        long fileSize = file.Length;
        Span<byte> header = stackalloc byte[12];
        if (fileSize < header.Length || file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
        {
            throw WinMLException.InvalidData("The WAV file is too short.");
        }

        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
        {
            throw new ArgumentException("The file is not a RIFF WAVE file.");
        }

        // Find the fmt chunk and the first data chunk.
        Span<byte> fmt = stackalloc byte[16];
        fmt.Clear();
        bool foundFmt = false;
        bool foundData = false;
        long dataOffset = 0;
        uint dataSize = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (file.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false) == chunk.Length)
        {
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (chunkSize > fileSize - file.Position)
                {
                    throw WinMLException.InvalidData("The WAV fmt chunk is truncated.");
                }

                int fmtReadSize = (int)Math.Min(chunkSize, (uint)fmt.Length);
                if (file.ReadAtLeast(fmt[..fmtReadSize], fmtReadSize, throwOnEndOfStream: false) < fmtReadSize)
                {
                    throw WinMLException.InvalidData("The WAV fmt chunk is truncated.");
                }

                if (chunkSize > fmt.Length)
                {
                    SeekForward(file, fileSize, chunkSize - (uint)fmt.Length);
                }

                foundFmt = true;
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (chunkSize == 0 || chunkSize > fileSize - file.Position)
                {
                    throw WinMLException.InvalidData("The WAV data chunk is empty or truncated.");
                }

                if (!foundData)
                {
                    foundData = true;
                    dataOffset = file.Position;
                    dataSize = chunkSize;
                }

                SeekForward(file, fileSize, chunkSize);
            }
            else
            {
                SeekForward(file, fileSize, chunkSize);
            }

            // Chunks are padded to an even size.
            if ((chunkSize & 1) != 0)
            {
                SeekForward(file, fileSize, 1);
            }
        }

        if (!foundFmt || !foundData)
        {
            throw new ArgumentException("The WAV file has no fmt or data chunk.");
        }

        ushort audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

        // Accept 16-bit PCM and 32-bit IEEE float. 32-bit integer PCM would be
        // garbage if read as float.
        bool is16BitPcm = audioFormat == 1 && bitsPerSample == 16;
        bool is32BitFloat = audioFormat == 3 && bitsPerSample == 32;
        if ((!is16BitPcm && !is32BitFloat) || channels == 0 || sampleRate == 0)
        {
            throw new ArgumentException("The WAV file is not 16-bit PCM or 32-bit float audio.");
        }

        uint expectedBlockAlign = (uint)channels * (uint)(bitsPerSample / 8);
        if (blockAlign != expectedBlockAlign || dataSize % expectedBlockAlign != 0)
        {
            throw WinMLException.InvalidData("The WAV block alignment does not match its format.");
        }

        ulong availableFrames = dataSize / expectedBlockAlign;
        ulong maximumSourceFrames = (((ulong)MaxSamples * sampleRate) + SampleRate - 1) / SampleRate;
        ulong frameCount = Math.Min(availableFrames, maximumSourceFrames);
        ulong byteCount = frameCount * expectedBlockAlign;
        if (frameCount == 0 || byteCount > MaximumWavDataBytes)
        {
            throw new IOException("The WAV file holds too much audio.", FileTooLarge);
        }

        byte[] raw = new byte[byteCount];
        file.Position = dataOffset;
        if (file.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false) != raw.Length)
        {
            throw WinMLException.InvalidData("The WAV data chunk is truncated.");
        }

        // Convert the source samples to normalized float mono.
        int sampleCount = (int)frameCount;
        float[] samples = new float[sampleCount];
        int bytesPerSample = bitsPerSample / 8;
        for (int i = 0; i < sampleCount; ++i)
        {
            float sum = 0.0f;
            for (int ch = 0; ch < channels; ++ch)
            {
                ReadOnlySpan<byte> value = raw.AsSpan(((i * channels) + ch) * bytesPerSample, bytesPerSample);
                sum += bitsPerSample == 16
                    ? BinaryPrimitives.ReadInt16LittleEndian(value) / 32768.0f
                    : BinaryPrimitives.ReadSingleLittleEndian(value);
            }

            samples[i] = sum / channels;
        }

        bool truncated = availableFrames > frameCount;
        if (sampleRate == SampleRate)
        {
            return new WavData(samples, sampleRate, truncated);
        }

        // Resample to 16 kHz with linear interpolation.
        double ratio = (double)SampleRate / sampleRate;
        ulong newLength = (ulong)(sampleCount * ratio);
        truncated = truncated || newLength > MaxSamples;
        newLength = Math.Min(newLength, MaxSamples);
        float[] resampled = new float[newLength];
        for (int i = 0; i < resampled.Length; ++i)
        {
            double sourceIndex = i / ratio;
            int index0 = (int)sourceIndex;
            int index1 = Math.Min(index0 + 1, sampleCount - 1);
            float fraction = (float)(sourceIndex - index0);
            resampled[i] = (samples[index0] * (1.0f - fraction)) + (samples[index1] * fraction);
        }

        return new WavData(resampled, SampleRate, truncated);
    }

    // Converts 16 kHz mono float samples into the FLOAT32 waveform tensor a
    // speech recognition session transcribes, and returns the metadata that
    // describes them. Throws ArgumentException
    // (E_INVALIDARG) for no samples or more than 30 seconds of them.
    public static Tensor CreateWaveformTensor(
        AudioTensorFactory factory,
        ReadOnlySpan<float> samples,
        out SpeechRecognitionWaveformMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(factory);
        metadata = default;
        if (samples.IsEmpty || (ulong)samples.Length > MaxSamples)
        {
            throw new ArgumentException("A Whisper waveform holds 1 to 480000 samples.", nameof(samples));
        }

        Tensor tensor = factory.CreateTensorFromPcm(
            MemoryMarshal.AsBytes(samples),
            new AudioFormat(AudioSampleFormat.IeeeFloat, SampleRate, Channels: 1, BitsPerSample: 32),
            new AudioTensorDescription(TensorDataType.Float32, TensorLayoutFormat.Nt, SampleRate, Channels: 1));
        metadata = new SpeechRecognitionWaveformMetadata(SampleRate, ChannelCount: 1, (ulong)samples.Length);
        return tensor;
    }

    private static void SeekForward(FileStream file, long fileSize, long byteCount)
    {
        if (byteCount > fileSize - file.Position)
        {
            throw WinMLException.InvalidData("A WAV chunk is truncated.");
        }

        file.Position += byteCount;
    }
}
