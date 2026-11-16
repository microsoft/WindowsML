// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Converts PCM audio in memory to tensors,
// such as the waveform tensor that a speech recognition session transcribes.

using Microsoft.Windows.AI.MachineLearning.Runtime;

namespace WinMLSamples.Runtime;

public enum AudioSampleFormat
{
    // Signed integer PCM (WAVE_FORMAT_PCM).
    Pcm = 1,

    // 32-bit floating-point samples (WAVE_FORMAT_IEEE_FLOAT).
    IeeeFloat = 3,
}

// The PCM data that CreateTensorFromPcm reads.
public readonly record struct AudioFormat(AudioSampleFormat SampleFormat, uint SampleRate, ushort Channels, ushort BitsPerSample);

// How CreateTensorFromPcm lays out samples. A window frame count of zero keeps
// the whole signal in one row.
public readonly record struct AudioTensorDescription(
    TensorDataType DataType,
    TensorLayoutFormat Layout,
    uint SampleRate,
    uint Channels,
    uint WindowFrames = 0,
    uint HopFrames = 0);

public sealed unsafe class AudioTensorFactory : WinMLObject, IWinMLInterface<AudioTensorFactory>
{
    private AudioTensorFactory(IWinMLAudioTensorFactory* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAudioTensorFactory.IID_Guid;

    static AudioTensorFactory IWinMLInterface<AudioTensorFactory>.CreateFromOwnedPointer(nint pointer) => new((IWinMLAudioTensorFactory*)pointer);

    // Converts interleaved PCM samples in format to a tensor laid out as description says.
    public Tensor CreateTensorFromPcm(ReadOnlySpan<byte> samples, AudioFormat format, AudioTensorDescription description)
    {
        ushort blockAlign = checked((ushort)(format.Channels * format.BitsPerSample / 8));
        WaveFormat wave = new()
        {
            FormatTag = (ushort)format.SampleFormat,
            Channels = format.Channels,
            SamplesPerSecond = format.SampleRate,
            AverageBytesPerSecond = format.SampleRate * blockAlign,
            BlockAlign = blockAlign,
            BitsPerSample = format.BitsPerSample,
        };
        WINML_AUDIO_TENSOR_DESC desc = new()
        {
            dataType = (WINML_TENSOR_DATA_TYPE)description.DataType,
            layout = (WINML_TENSOR_LAYOUT_FORMAT)description.Layout,
            sampleRate = description.SampleRate,
            channels = description.Channels,
            windowFrames = description.WindowFrames,
            hopFrames = description.HopFrames,
        };
        IWinMLTensor* tensor = null;
        fixed (byte* data = samples)
        {
            Check(((IWinMLAudioTensorFactory*)ThisPtr)->CreateTensorFromPcm(
                data, (ulong)samples.Length, (byte*)&wave, (uint)sizeof(WaveFormat), &desc, &tensor));
        }

        return new Tensor(Required(tensor));
    }

    // WAVEFORMATEX.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }
}

public sealed unsafe partial class ExecutionTarget
{
    private AudioTensorFactory? _audioTensorFactory;

    public AudioTensorFactory AudioTensorFactory => GetCachedInterface(ref _audioTensorFactory);
}
