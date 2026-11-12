// Copyright (C) Microsoft Corporation. All rights reserved.

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TaskStudio.Audio;

internal sealed record SpeechAudio(float[] Samples, int SampleRate = 16000)
{
    public const int MaximumSeconds = 120;
    public double DurationSeconds => (double)Samples.Length / SampleRate;

    public static SpeechAudio Read(string path)
    {
        using var reader = new MediaFoundationReader(path);
        if (reader.TotalTime.TotalSeconds > MaximumSeconds)
            throw new ArgumentException($"Choose an audio clip no longer than {MaximumSeconds} seconds.");
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2)
            samples = new StereoToMonoSampleProvider(samples);
        if (samples.WaveFormat.Channels != 1)
            throw new NotSupportedException("Choose mono or stereo audio.");
        if (samples.WaveFormat.SampleRate != 16000)
            samples = new WdlResamplingSampleProvider(samples, 16000);
        var result = new List<float>();
        var buffer = new float[16000];
        int count;
        while ((count = samples.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (result.Count + count > 16000 * MaximumSeconds)
                throw new ArgumentException($"Choose an audio clip no longer than {MaximumSeconds} seconds.");
            for (int i = 0; i < count; i++)
            {
                if (!float.IsFinite(buffer[i])) throw new InvalidDataException("Audio contains a non-finite sample.");
                result.Add(Math.Clamp(buffer[i], -1, 1));
            }
        }
        if (result.Count == 0) throw new InvalidDataException("The selected file contains no audio samples.");
        return new(result.ToArray());
    }

    public void Save(string path)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
        writer.WriteSamples(Samples, 0, Samples.Length);
    }
}

internal sealed class MicrophoneCapture : IDisposable
{
    private readonly WaveInEvent input = new()
    {
        WaveFormat = new WaveFormat(16000, 16, 1),
        BufferMilliseconds = 100,
        NumberOfBuffers = 4
    };
    private readonly List<float> recorded = new();
    private readonly TaskCompletionSource<SpeechAudio> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<float[]> onChunk;
    private Exception? failure;
    private int stopping;

    public MicrophoneCapture(Action<float[]> onChunk)
    {
        this.onChunk = onChunk;
        input.DataAvailable += OnData;
        input.RecordingStopped += (_, args) =>
        {
            Exception? error = failure ?? args.Exception;
            if (error is not null) completion.TrySetException(error);
            else if (recorded.Count == 0) completion.TrySetException(new InvalidOperationException("The microphone returned no audio. Check the input device and Windows microphone privacy settings."));
            else completion.TrySetResult(new(recorded.ToArray()));
        };
    }

    public Task<SpeechAudio> Completion => completion.Task;

    public void Start()
    {
        if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("No microphone is available. Connect an input device and check Windows microphone privacy settings.");
        input.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        try
        {
            int count = Math.Min(args.BytesRecorded / 2, 16000 * SpeechAudio.MaximumSeconds - recorded.Count);
            if (count > 0)
            {
                var samples = new float[count];
                for (int i = 0; i < count; i++)
                    samples[i] = BitConverter.ToInt16(args.Buffer, i * 2) / 32768f;
                recorded.AddRange(samples);
                onChunk(samples);
            }
            if (recorded.Count >= 16000 * SpeechAudio.MaximumSeconds) Stop();
        }
        catch (Exception error)
        {
            failure = error;
            Stop();
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0) input.StopRecording();
    }

    public void Dispose() => input.Dispose();
}
