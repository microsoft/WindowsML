// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;
using static Microsoft.Windows.AI.MachineLearning.Runtime.Tasks.WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS;

namespace TaskStudio.Runtime;

internal sealed record SpeechCapabilities(bool LiveInput, GpuDevice Device);
internal sealed record SpeechResult(string Text, bool Canceled);

internal sealed partial class TaskRuntime
{
    private unsafe IWinMLRuntime* speechRuntime;
    private unsafe IWinMLTasks* speechTasks;
    private unsafe IWinMLTokenizer* speechTokenizer;
    private unsafe IWinMLExecutionTarget* speechTarget;
    private unsafe IWinMLRawTensorFactory* speechTensors;
    private unsafe IWinMLAutomaticSpeechRecognitionSession* speechSession;
    private unsafe IWinMLAutomaticSpeechRecognitionLiveSession* speechLive;

    public unsafe Task<SpeechCapabilities> LoadSpeechAsync(string modelPath, string tokenizerPath, string language, GpuDevice device) => Run(() =>
    {
        if (!Directory.Exists(modelPath) || !File.Exists(Path.Combine(modelPath, "config.json")))
            throw new ArgumentException("Choose a speech model directory containing config.json and safetensors weights.");
        if (!File.Exists(tokenizerPath))
            throw new ArgumentException("Choose the speech model's matching tokenizer_config.json.");
        if (speechRuntime != null) throw new InvalidOperationException("Release the speech session before loading another model.");
        IWinMLAutomaticSpeechRecognitionTaskFactory* factory = null;
        IWinMLAutomaticSpeechRecognitionTask* task = null;
        IWinMLAutomaticSpeechRecognitionConfiguration* configuration = null;
        IWinMLModelAutomaticSpeechRecognitionConfiguration* modelConfiguration = null;
        IWinMLModel* model = null;
        IWinMLExecutionTarget* host = null;
        try
        {
            Guid iid = typeof(IWinMLRuntime).GUID;
            IWinMLRuntime* createdRuntime = null;
            PInvoke.WinMLCreateRuntime(&iid, (void**)&createdRuntime).ThrowOnFailure();
            speechRuntime = createdRuntime;
            iid = typeof(IWinMLTasks).GUID;
            IWinMLTasks* createdTasks = null;
            PInvoke.WinMLCreateTasks(speechRuntime, &iid, (void**)&createdTasks).ThrowOnFailure();
            speechTasks = createdTasks;
            IWinMLTokenizer* createdTokenizer = null;
            PInvoke.WinMLCreateTokenizerFromFile(tokenizerPath, &createdTokenizer).ThrowOnFailure();
            speechTokenizer = createdTokenizer;
            speechTarget = CreateGpuTarget(speechRuntime, device);
            GpuDevice actualDevice = DescribeGpuTarget(speechTarget);
            host = speechRuntime->CreateCpuExecutionTarget();
            IWinMLRawTensorFactory* tensors = null;
            host->QueryInterface(out tensors).ThrowOnFailure();
            speechTensors = tensors;
            speechTasks->QueryInterface(out factory).ThrowOnFailure();
            task = factory->CreateAutomaticSpeechRecognitionTask(speechTokenizer);
            configuration = task->CreateConfiguration();
            configuration->QueryInterface(out modelConfiguration).ThrowOnFailure();
            model = speechRuntime->LoadModelFromPath(modelPath, null);
            try
            {
                fixed (char* directory = CompiledProgramDirectory)
                {
                    var modelOptions = new WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTIONS
                    {
                        presentFields = WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS.WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY
                            | WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS.WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE,
                        compiledProgramDirectory = new PCWSTR(directory),
                        compiledProgramStorage = WINML_COMPILED_PROGRAM_STORAGE.WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS
                    };
                    modelConfiguration->SetModel(speechTarget, model, &modelOptions);
                }
                SpeechProgramCacheUsed = true;
            }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070032))
            {
                // Some speech models (such as Whisper) cannot keep compiled programs; compile per session instead.
                modelConfiguration->SetModel(speechTarget, model, null);
                SpeechProgramCacheUsed = false;
            }
            configuration->SetLanguage(language);
            configuration->Validate();
            speechSession = task->CreateSession(configuration);
            IWinMLAutomaticSpeechRecognitionLiveSession* live = null;
            var support = speechSession->QueryInterface(out live);
            if (support.Value != unchecked((int)0x80004002)) support.ThrowOnFailure();
            speechLive = live;
            return new SpeechCapabilities(speechLive != null, actualDevice);
        }
        catch
        {
            UnloadSpeech();
            throw;
        }
        finally
        {
            Release(host); Release(model); Release(modelConfiguration); Release(configuration); Release(task); Release(factory);
        }
    });

    public unsafe Task<SpeechResult> TranscribeAsync(float[] samples, IProgress<string> progress, CancellationToken cancellation) => Run(() =>
    {
        RequireSpeechSession();
        if (samples.Length == 0) throw new ArgumentException("Provide a nonempty audio clip.");
        IWinMLTensor* tensor = null;
        IWinMLAutomaticSpeechRecognitionPullStream* stream = null;
        IWinMLCancellationSource* source = speechTasks->CreateCancellationSource();
        try
        {
            nint sourceAddress = (nint)source;
            using var registration = cancellation.Register(() => ((IWinMLCancellationSource*)sourceAddress)->Cancel());
            speechSession->Reset();
            tensor = CreateSpeechTensor(samples);
            var metadata = WaveformMetadata((ulong)samples.Length);
            stream = speechSession->TranscribeWaveform(tensor, &metadata, source);
            if (DrainSpeech(stream, progress) != WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_COMPLETED)
                throw new InvalidOperationException("The ASR backend requested additional input for a complete waveform.");
            return GetSpeechResult(stream);
        }
        finally
        {
            try { if (stream != null) stream->Close(); }
            finally { Release(stream); Release(tensor); Release(source); }
        }
    });

    public unsafe Task<SpeechResult> TranscribeLiveAsync(ChannelReader<float[]> chunks,
        IProgress<string> progress, CancellationToken cancellation) => Run(() =>
    {
        RequireSpeechSession();
        if (speechLive == null) throw new NotSupportedException("This speech session does not expose native live input. Record a clip, then transcribe it instead.");
        IWinMLAutomaticSpeechRecognitionPullStream* stream = null;
        IWinMLAutomaticSpeechRecognitionLiveInput* input = null;
        IWinMLCancellationSource* source = speechTasks->CreateCancellationSource();
        var retained = new Dictionary<ulong, nint>();
        IWinMLTensor* pending = null;
        try
        {
            nint sourceAddress = (nint)source;
            using var registration = cancellation.Register(() => ((IWinMLCancellationSource*)sourceAddress)->Cancel());
            speechSession->Reset();
            var metadata = WaveformMetadata(1600);
            speechLive->BeginLiveInput(&metadata, 8, source, &stream, &input);
            ulong chunkId = 0;
            while (chunks.WaitToReadAsync(cancellation).AsTask().GetAwaiter().GetResult())
            {
                while (chunks.TryRead(out var samples))
                {
                    cancellation.ThrowIfCancellationRequested();
                    pending = CreateSpeechTensor(samples);
                    BOOL accepted, hasWatermark;
                    ulong watermark;
                    do
                    {
                        input->WriteChunk(chunkId, pending, (ulong)samples.Length, &accepted, &hasWatermark, &watermark);
                        if (accepted)
                        {
                            retained.Add(chunkId++, (nint)pending);
                            pending = null;
                        }
                        if (hasWatermark)
                        {
                            ulong retiredThrough = watermark;
                            foreach (ulong retired in retained.Keys.Where(id => id <= retiredThrough).ToArray())
                            {
                                Release((IWinMLTensor*)retained[retired]);
                                retained.Remove(retired);
                            }
                        }
                        var status = DrainSpeech(stream, progress);
                        if (status == WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_COMPLETED)
                            return GetSpeechResult(stream);
                        if (!accepted && status == WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_NEED_INPUT)
                            throw new InvalidOperationException("The live ASR backend rejected a chunk but could not advance its input queue.");
                    } while (!accepted);
                }
            }
            input->CompleteInput();
            if (DrainSpeech(stream, progress) != WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_COMPLETED)
                throw new InvalidOperationException("The live ASR stream requested more input after CompleteInput.");
            return GetSpeechResult(stream);
        }
        finally
        {
            // Early stream Close makes input Close legal and retires all borrowed tensors.
            try { if (stream != null) stream->Close(); }
            finally
            {
                try { if (input != null) input->Close(); }
                finally
                {
                    Release(input); Release(stream); Release(pending);
                    foreach (nint tensor in retained.Values) Release((IWinMLTensor*)tensor);
                    Release(source);
                }
            }
        }
    });

    private unsafe IWinMLTensor* CreateSpeechTensor(float[] samples)
    {
        if (samples.Any(sample => !float.IsFinite(sample) || sample is < -1 or > 1))
            throw new ArgumentException("ASR requires finite normalized mono samples.");
        ulong length = (ulong)samples.Length;
        var descriptor = new WINML_TENSOR_DESC
        {
            dataType = WINML_TENSOR_DATA_TYPE.WINML_TENSOR_DATA_TYPE_FLOAT32,
            dimensionCount = 1,
            dimensions = &length
        };
        fixed (float* data = samples)
            return speechTensors->CreateTensor(&descriptor, data, length * sizeof(float));
    }

    private static WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA WaveformMetadata(ulong count) =>
        new() { sampleRate = 16000, channelCount = 1, validSampleCount = count };

    private static unsafe WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS ReadSpeech(
        IWinMLAutomaticSpeechRecognitionPullStream* stream, IProgress<string> progress)
    {
        WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS status;
        PCWSTR fragment;
        stream->ReadNext(&status, &fragment);
        if (status == WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_UPDATE) progress.Report(fragment.ToString());
        return status;
    }

    private static unsafe WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS DrainSpeech(
        IWinMLAutomaticSpeechRecognitionPullStream* stream, IProgress<string> progress)
    {
        WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS status;
        do { status = ReadSpeech(stream, progress); }
        while (status == WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_UPDATE);
        return status;
    }

    private static unsafe SpeechResult GetSpeechResult(IWinMLAutomaticSpeechRecognitionPullStream* stream)
    {
        IWinMLAutomaticSpeechRecognitionResult* result = stream->GetResult();
        try
        {
            PCWSTR transcript;
            result->GetTranscript(&transcript);
            bool canceled = result->GetFinishReason() ==
                WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON.WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON_CANCELED;
            if (!canceled) result->GetErrorCode().ThrowOnFailure();
            return new(transcript.ToString(), canceled);
        }
        finally { Release(result); }
    }

    private unsafe void RequireSpeechSession()
    {
        if (speechSession == null) throw new InvalidOperationException("Create a speech task session first.");
    }

    public unsafe Task UnloadSpeechAsync() => Run(() => { UnloadSpeech(); return true; });

    private unsafe void UnloadSpeech()
    {
        try { if (speechSession != null) speechSession->Close(); }
        finally
        {
            Release(speechLive); speechLive = null;
            Release(speechSession); speechSession = null;
            Release(speechTensors); speechTensors = null;
            Release(speechTarget); speechTarget = null;
            Release(speechTokenizer); speechTokenizer = null;
            Release(speechTasks); speechTasks = null;
            Release(speechRuntime); speechRuntime = null;
        }
    }
}
