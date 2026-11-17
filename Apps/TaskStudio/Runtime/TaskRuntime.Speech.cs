// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Threading.Channels;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace TaskStudio.Runtime;

internal sealed record SpeechCapabilities(bool LiveInput, GpuDevice Device);
internal sealed record SpeechResult(string Text, bool Canceled);

internal sealed partial class TaskRuntime
{
    private WinMLRuntime? speechRuntime;
    private WinMLTasks? speechTasks;
    private Tokenizer? speechTokenizer;
    private ExecutionTarget? speechTarget;
    // Waveforms are host-memory tensors from this CPU target's tensor factory; the model still runs on speechTarget.
    private ExecutionTarget? speechHost;
    private SpeechRecognitionSession? speechSession;
    private SpeechRecognitionLiveSession? speechLive;

    public Task<SpeechCapabilities> LoadSpeechAsync(string modelPath, string tokenizerPath, string language, GpuDevice device) => Run(() =>
    {
        if (!Directory.Exists(modelPath) || !File.Exists(Path.Combine(modelPath, "config.json")))
            throw new ArgumentException("Choose a speech model directory containing config.json and safetensors weights.");
        if (!File.Exists(tokenizerPath))
            throw new ArgumentException("Choose the speech model's matching tokenizer_config.json.");
        if (speechRuntime != null) throw new InvalidOperationException("Release the speech session before loading another model.");
        AutomaticSpeechRecognitionTask? task = null;
        SpeechRecognitionConfiguration? configuration = null;
        ModelSpeechRecognitionConfiguration? modelConfiguration = null;
        Model? model = null;
        try
        {
            speechRuntime = WinMLRuntime.Create();
            speechTasks = WinMLTasks.Create(speechRuntime);
            speechTokenizer = Tokenizer.CreateFromFile(tokenizerPath);
            speechTarget = CreateGpuTarget(speechRuntime, device);
            GpuDevice actualDevice = DescribeGpuTarget(speechTarget);
            speechHost = speechRuntime.CreateCpuExecutionTarget();
            task = speechTasks.AutomaticSpeechRecognition.CreateAutomaticSpeechRecognitionTask(speechTokenizer);
            configuration = task.CreateConfiguration();
            modelConfiguration = configuration.As<ModelSpeechRecognitionConfiguration>();
            model = speechRuntime.LoadModelFromPath(modelPath);
            try
            {
                modelConfiguration.SetModel(speechTarget, model, new SpeechRecognitionModelOptions
                {
                    CompiledProgramDirectory = CompiledProgramDirectory,
                    CompiledProgramStorage = CompiledProgramStorage.Programs
                });
                SpeechProgramCacheUsed = true;
            }
            catch (Exception error) when (error.HResult == NotSupportedHResult)
            {
                // Some speech models (such as Whisper) cannot keep compiled programs; compile per session instead.
                modelConfiguration.SetModel(speechTarget, model);
                SpeechProgramCacheUsed = false;
            }
            configuration.Language = language;
            configuration.Validate();
            speechSession = task.CreateSession(configuration);
            speechSession.TryAs(out speechLive);
            return new SpeechCapabilities(speechLive != null, actualDevice);
        }
        catch
        {
            UnloadSpeech();
            throw;
        }
        finally
        {
            model?.Dispose(); modelConfiguration?.Dispose(); configuration?.Dispose(); task?.Dispose();
        }
    });

    public Task<SpeechResult> TranscribeAsync(float[] samples, IProgress<string> progress, CancellationToken cancellation) => Run(() =>
    {
        RequireSpeechSession();
        if (samples.Length == 0) throw new ArgumentException("Provide a nonempty audio clip.");
        using CancellationSource source = speechTasks!.CreateCancellationSource();
        using var registration = cancellation.Register(source.Cancel);
        speechSession!.Reset();
        using Tensor tensor = CreateSpeechTensor(samples);
        using SpeechRecognitionPullStream stream = speechSession.TranscribeWaveform(tensor, WaveformMetadata((ulong)samples.Length), source);
        if (DrainSpeech(stream, progress) != SpeechRecognitionReadStatus.Completed)
            throw new InvalidOperationException("The ASR backend requested additional input for a complete waveform.");
        SpeechResult result = GetSpeechResult(stream);
        stream.Close();
        return result;
    });

    public Task<SpeechResult> TranscribeLiveAsync(ChannelReader<float[]> chunks,
        IProgress<string> progress, CancellationToken cancellation) => Run(() =>
    {
        RequireSpeechSession();
        if (speechLive == null) throw new NotSupportedException("This speech session does not expose native live input. Record a clip, then transcribe it instead.");
        SpeechRecognitionPullStream? stream = null;
        SpeechRecognitionLiveInput? input = null;
        using CancellationSource source = speechTasks!.CreateCancellationSource();
        var retained = new Dictionary<ulong, Tensor>();
        Tensor? pending = null;
        try
        {
            using var registration = cancellation.Register(source.Cancel);
            speechSession!.Reset();
            stream = speechLive.BeginLiveInput(WaveformMetadata(1600), 8, source, out input);
            ulong chunkId = 0;
            while (chunks.WaitToReadAsync(cancellation).AsTask().GetAwaiter().GetResult())
            {
                while (chunks.TryRead(out var samples))
                {
                    cancellation.ThrowIfCancellationRequested();
                    Tensor chunkTensor = CreateSpeechTensor(samples);
                    pending = chunkTensor;
                    bool accepted;
                    do
                    {
                        var chunk = input.WriteChunk(chunkId, chunkTensor, (ulong)samples.Length);
                        accepted = chunk.Accepted;
                        if (accepted)
                        {
                            retained.Add(chunkId++, chunkTensor);
                            pending = null;
                        }
                        if (chunk.LifetimeWatermark is ulong retiredThrough)
                        {
                            foreach (ulong retired in retained.Keys.Where(id => id <= retiredThrough).ToArray())
                            {
                                retained[retired].Dispose();
                                retained.Remove(retired);
                            }
                        }
                        var status = DrainSpeech(stream, progress);
                        if (status == SpeechRecognitionReadStatus.Completed)
                            return GetSpeechResult(stream);
                        if (!accepted && status == SpeechRecognitionReadStatus.NeedInput)
                            throw new InvalidOperationException("The live ASR backend rejected a chunk but could not advance its input queue.");
                    } while (!accepted);
                }
            }
            input.CompleteInput();
            if (DrainSpeech(stream, progress) != SpeechRecognitionReadStatus.Completed)
                throw new InvalidOperationException("The live ASR stream requested more input after CompleteInput.");
            return GetSpeechResult(stream);
        }
        finally
        {
            // Early stream Close makes input Close legal and retires all borrowed tensors.
            try { stream?.Close(); }
            finally
            {
                try { input?.Close(); }
                finally
                {
                    input?.Dispose(); stream?.Dispose(); pending?.Dispose();
                    foreach (Tensor tensor in retained.Values) tensor.Dispose();
                }
            }
        }
    });

    private Tensor CreateSpeechTensor(float[] samples)
    {
        if (samples.Any(sample => !float.IsFinite(sample) || sample is < -1 or > 1))
            throw new ArgumentException("ASR requires finite normalized mono samples.");
        return speechHost!.CreateTensor<float>(samples, [samples.Length]);
    }

    private static SpeechRecognitionWaveformMetadata WaveformMetadata(ulong count) => new(SampleRate: 16000, ChannelCount: 1, ValidSampleCount: count);

    // Reports each update and returns the first other status: NeedInput, or Completed.
    private static SpeechRecognitionReadStatus DrainSpeech(SpeechRecognitionPullStream stream, IProgress<string> progress)
    {
        while (stream.TryReadNext(out SpeechRecognitionReadStatus status, out ReadOnlySpan<char> fragment))
        {
            if (status != SpeechRecognitionReadStatus.Update) return status;
            progress.Report(fragment.ToString());
        }
        return SpeechRecognitionReadStatus.Completed;
    }

    private static SpeechResult GetSpeechResult(SpeechRecognitionPullStream stream)
    {
        using SpeechRecognitionResult result = stream.GetResult();
        bool canceled = result.FinishReason == SpeechRecognitionFinishReason.Canceled;
        if (!canceled) ThrowOnError(result.ErrorCode);
        return new(result.Transcript, canceled);
    }

    private void RequireSpeechSession()
    {
        if (speechSession == null) throw new InvalidOperationException("Create a speech task session first.");
    }

    public Task UnloadSpeechAsync() => Run(() => { UnloadSpeech(); return true; });

    private void UnloadSpeech()
    {
        try { speechSession?.Close(); }
        finally
        {
            Release(ref speechLive);
            Release(ref speechSession);
            Release(ref speechHost);
            Release(ref speechTarget);
            Release(ref speechTokenizer);
            Release(ref speechTasks);
            Release(ref speechRuntime);
        }
    }
}
