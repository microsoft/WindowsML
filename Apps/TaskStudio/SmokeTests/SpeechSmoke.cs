// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Threading.Channels;
using NAudio.Wave;
using TaskStudio.Audio;
using TaskStudio.Runtime;

internal static class SpeechSmoke
{
    public static async Task<int> CheckAudioAsync(bool microphone)
    {
        string path = Path.Combine(Path.GetTempPath(), $"task-studio-audio-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2)))
            {
                for (int i = 0; i < 48000; i++)
                {
                    short sample = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 48000));
                    writer.WriteSample(sample / 32768f);
                    writer.WriteSample(sample / 32768f);
                }
            }
            var audio = SpeechAudio.Read(path);
            if (audio.SampleRate != 16000 || Math.Abs(audio.Samples.Length - 16000) > 2 ||
                audio.Samples.Any(sample => !float.IsFinite(sample) || Math.Abs(sample) > 1) ||
                audio.Samples.Max(Math.Abs) < 0.1)
                throw new InvalidOperationException("Stereo downmix/resampling did not produce normalized 16 kHz mono.");
            audio.Save(path);
            var roundTrip = SpeechAudio.Read(path);
            if (Math.Abs(roundTrip.DurationSeconds - 1) > 0.01)
                throw new InvalidOperationException("Saved WAV duration did not round-trip.");
            Console.WriteLine("PASS: file decoding, stereo downmix, 48-to-16 kHz resampling, normalization and WAV export.");
            if (microphone)
            {
                int chunks = 0;
                using var capture = new MicrophoneCapture(_ => Interlocked.Increment(ref chunks));
                capture.Start();
                await Task.Delay(1500);
                capture.Stop();
                SpeechAudio recorded = await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                if (recorded.Samples.Length == 0 || chunks == 0) throw new InvalidOperationException("Microphone produced no audio.");
                Console.WriteLine($"PASS: microphone capture, {chunks} chunks, {recorded.DurationSeconds:F2}s, orderly stop.");
            }
            return 0;
        }
        finally { File.Delete(path); }
    }

    public static async Task<int> CheckRecognitionAsync(string model, string tokenizer, string path)
    {
        SpeechAudio audio = SpeechAudio.Read(path);
        await using var runtime = new TaskRuntime();
        var devices = await runtime.GetGpuDevicesAsync();
        SpeechCapabilities capabilities = await runtime.LoadSpeechAsync(model, tokenizer, "", devices.First());
        Console.WriteLine($"ASR GPU: {capabilities.Device.Name} ({capabilities.Device.Identity}), CGC backend.");
        Console.WriteLine($"Speech session created. Native live input: {capabilities.LiveInput}.");
        var progress = new TranscriptProgress();
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var canceled = await runtime.TranscribeAsync(audio.Samples, progress, cancellation.Token);
            if (!canceled.Canceled) throw new InvalidOperationException("Recorded ASR ignored pre-cancellation.");
            if (capabilities.LiveInput)
            {
                var input = Channel.CreateBounded<float[]>(1);
                try
                {
                    await runtime.TranscribeLiveAsync(input.Reader, progress, cancellation.Token);
                    throw new InvalidOperationException("Live ASR ignored pre-cancellation.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            Console.WriteLine("PASS: native speech cancellation and ordered stream/input cleanup.");
        }
        SpeechResult result = await runtime.TranscribeAsync(audio.Samples, progress, CancellationToken.None);
        if (result.Canceled || string.IsNullOrWhiteSpace(result.Text)) throw new InvalidOperationException("Recorded ASR returned no successful transcript.");
        Console.WriteLine($"\nPASS: recorded ASR: {result.Text}");
        if (capabilities.LiveInput)
        {
            var chunks = Channel.CreateBounded<float[]>(50);
            Task producer = Task.Run(async () =>
            {
                for (int offset = 0; offset < audio.Samples.Length; offset += 1600)
                    await chunks.Writer.WriteAsync(audio.Samples[offset..Math.Min(offset + 1600, audio.Samples.Length)]);
                chunks.Writer.Complete();
            });
            result = await runtime.TranscribeLiveAsync(chunks.Reader, progress, CancellationToken.None);
            await producer;
            if (result.Canceled || string.IsNullOrWhiteSpace(result.Text)) throw new InvalidOperationException("Native live ASR returned no successful transcript.");
            Console.WriteLine($"\nPASS: native live ASR: {result.Text}");
        }
        else Console.WriteLine("Native live input unavailable: capability correctly reported as false.");
        await runtime.UnloadSpeechAsync();
        return 0;
    }

    private sealed class TranscriptProgress : IProgress<string>
    {
        public void Report(string value) => Console.Write(value);
    }
}
