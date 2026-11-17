// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Speech recognition from published weights with the Windows ML Task API,
// from C#.
//
// Bind a speech model folder to the speech recognition task and stream a
// transcript of a WAV file. The app passes the model's tokenizer explicitly.
// This is the C# counterpart of ..\main.cpp; it takes the same arguments and
// prints the same output.
//
//   WinMLRuntime.LoadModelFromPath loads the model folder, and
//   ModelSpeechRecognitionConfiguration.SetModel binds it on a GPU target. The
//   WAV samples stay in host memory on a CPU target; the task moves them to the
//   GPU. A SpeechRecognizer streams the transcript with await foreach as it is
//   recognized. The streamed text must match the transcript of the final
//   result, and an error finish fails the sample. Ctrl+C cancels recognition
//   through a CancellationToken.
//
//   With --live, which the C++ sample does not have, the sample writes the
//   same waveform to a live session in one-second chunks instead, when the
//   model accepts live input.
//
// Run it
//   ..\..\..\run_model_speech_recognition.ps1 -SampleLanguage csharp
//       [-ModelPath <model-folder>] [-AudioPath <input.wav>] [-Language <tag>] [-Live]
//
// The sample uses the sample wrapper in ..\..\..\..\Runtime\shared\csharp\Runtime,
// an idiomatic C# layer over the Runtime and Task APIs in the
// Microsoft.Windows.AI.MachineLearning package, and WhisperAudio in
// ..\..\..\..\Runtime\shared\csharp to read the WAV file. A speech recognition
// session belongs to the thread that created it; SpeechRecognizer keeps it on
// a thread of its own. See ..\..\..\..\Runtime\shared\csharp\README.md.

using System.Text;
using WinMLSamples;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace ModelSpeechRecognition;

internal static class Program
{
    // Live input is written one second at a time, and the session holds at
    // most this many chunks that it has not recognized yet.
    private const int LiveChunkSamples = (int)WhisperAudio.SampleRate;
    private const uint MaximumPendingChunks = 4;

    private static async Task<int> Main(string[] args)
    {
        Options? options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(
                "Usage: task-model-speech-recognition-csharp.exe <model-directory> <tokenizer.json> <input.wav> " +
                "[--language <tag>] [--mode transcribe|translate] [--live]");
            return 2;
        }

        // Transcripts are not limited to the console's code page.
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return await RunAsync(options);
        }
        catch (Exception ex)
        {
            ReportFailure("Speech recognition", ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync(Options options)
    {
        WavData audio = WhisperAudio.LoadWavFile(options.WavPath);
        if (audio.Truncated)
        {
            Console.Error.WriteLine(
                "Input audio exceeds the sample's 30-second WAV limit. Split it into clips of at most 30 seconds; " +
                "no transcript was produced.");
            return 2;
        }

        if (audio.Samples.Length == 0 || audio.SampleRate != WhisperAudio.SampleRate)
        {
            throw WinMLException.InvalidData("The WAV file holds no 16 kHz audio.");
        }

        using WinMLRuntime runtime = WinMLRuntime.Create();
        using ExecutionTarget target = runtime.CreateExecutionTarget(ExecutionTargetKind.Gpu, ExecutionTargetPreference.Performance);
        using Model model = runtime.LoadModelFromPath(options.ModelDirectory);
        using Tokenizer tokenizer = Tokenizer.CreateFromFile(options.TokenizerPath);
        using WinMLTasks tasks = WinMLTasks.Create(runtime);
        using AutomaticSpeechRecognitionTask task = tasks.AutomaticSpeechRecognition.CreateAutomaticSpeechRecognitionTask(tokenizer);
        using SpeechRecognitionConfiguration configuration = task.CreateConfiguration();

        try
        {
            using ModelSpeechRecognitionConfiguration modelConfiguration = configuration.As<ModelSpeechRecognitionConfiguration>();
            modelConfiguration.SetModel(target, model);
        }
        catch (NotSupportedException)
        {
            Console.Error.WriteLine(
                "Windows ML cannot run this model for speech recognition (HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). " +
                "Use a supported speech model folder, such as openai/whisper-tiny.");
            return 1;
        }

        // Without a language, the model's default applies.
        configuration.Language = options.Language;
        configuration.Mode = options.Translate ? SpeechRecognitionMode.Translate : SpeechRecognitionMode.Transcribe;
        configuration.Validate();
        SpeechRecognizer recognizer;
        try
        {
            recognizer = await SpeechRecognizer.CreateAsync(tasks, task, configuration);
        }
        catch (NotSupportedException) when (options.Translate)
        {
            Console.Error.WriteLine(
                "Models-backed speech recognition transcribes only; this Windows ML release doesn't translate with a " +
                "model-backed configuration (HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)).");
            return 1;
        }

        await using (recognizer)
        {
            // A session exposes live input only when its model accepts audio as
            // the app writes it.
            if (options.Live && !await recognizer.InvokeAsync(SupportsLiveInput))
            {
                Console.Error.WriteLine(
                    "This speech model does not accept live input (E_NOINTERFACE). Run without --live to transcribe " +
                    "the whole waveform.");
                return 1;
            }

            // WAV parsing stays outside the task. Its input is host-resident PCM.
            using ExecutionTarget waveformTarget = runtime.CreateCpuExecutionTarget();
            SpeechRecognitionFinishReason finishReason = options.Live
                ? await TranscribeLiveAsync(tasks, recognizer, waveformTarget.AudioTensorFactory, audio.Samples)
                : await TranscribeAsync(recognizer, waveformTarget.AudioTensorFactory, audio.Samples);
            Console.WriteLine($"Finished because: {DescribeFinishReason(finishReason)}");

            // Closing the session reports a failure that disposing it would not.
            await recognizer.CloseAsync();
        }

        return 0;
    }

    // Transcribes the whole waveform and prints the transcript as it is
    // recognized. Returns why recognition finished.
    private static async Task<SpeechRecognitionFinishReason> TranscribeAsync(
        SpeechRecognizer recognizer,
        AudioTensorFactory audioFactory,
        float[] samples)
    {
        using Tensor waveform = WhisperAudio.CreateWaveformTensor(audioFactory, samples, out SpeechRecognitionWaveformMetadata metadata);
        Console.Write("Transcript: ");
        StringBuilder streamed = new();
        using CancelKeyScope cancel = new();
        try
        {
            await foreach (SpeechRecognitionUpdate update in recognizer.TranscribeWaveformAsync(waveform, metadata, cancel.Token))
            {
                // Without live input or predictions, every update carries text.
                if (update.Status != SpeechRecognitionReadStatus.Update)
                {
                    throw WinMLException.Unexpected($"Unexpected read status {update.Status}.");
                }

                streamed.Append(update.Text);
                Console.Write(update.Text);
            }
        }
        catch (OperationCanceledException) when (recognizer.LastOutcome is not null)
        {
            // A canceled recognition still reports what it recognized.
        }

        // An operation that completed or was canceled always has an outcome.
        return CheckOutcome(recognizer.LastOutcome!, streamed.ToString());
    }

    // Writes the waveform to a live session in fixed-size chunks and prints
    // the transcript as it is recognized. Returns why recognition finished.
    private static async Task<SpeechRecognitionFinishReason> TranscribeLiveAsync(
        WinMLTasks tasks,
        SpeechRecognizer recognizer,
        AudioTensorFactory audioFactory,
        float[] samples)
    {
        Console.Write("Transcript: ");
        using CancelKeyScope cancel = new();

        // Live input, like the session, belongs to the session's thread.
        (SpeechRecognitionOutcome outcome, string streamed) = await recognizer.InvokeAsync(
            session => TranscribeLive(session, tasks, audioFactory, samples, cancel.Token));
        return CheckOutcome(outcome, streamed);
    }

    // Runs on the session's thread.
    private static (SpeechRecognitionOutcome Outcome, string Streamed) TranscribeLive(
        SpeechRecognitionSession session,
        WinMLTasks tasks,
        AudioTensorFactory audioFactory,
        float[] samples,
        CancellationToken cancellationToken)
    {
        using SpeechRecognitionLiveSession live = session.As<SpeechRecognitionLiveSession>();
        using CancellationSource cancellation = tasks.CreateCancellationSource();
        using CancellationTokenRegistration registration = cancellationToken.Register(cancellation.Cancel);

        // The metadata gives the sample rate and channel count, and a nonzero
        // valid sample count: BeginLiveInput rejects zero with E_INVALIDARG.
        // The sample writes the whole waveform, so it passes its length.
        using SpeechRecognitionPullStream stream = live.BeginLiveInput(
            new SpeechRecognitionWaveformMetadata(WhisperAudio.SampleRate, ChannelCount: 1, ValidSampleCount: (ulong)samples.Length),
            MaximumPendingChunks,
            cancellation,
            out SpeechRecognitionLiveInput input);
        using (input)
        {
            // The session reads a chunk's tensor until the lifetime watermark
            // passes the chunk, so the sample keeps each chunk until then.
            Queue<(ulong Id, Tensor Waveform)> pending = new();
            Tensor? chunk = null;
            try
            {
                StringBuilder streamed = new();
                int offset = 0;
                int chunkLength = 0;
                ulong chunkId = 0;
                bool inputComplete = false;
                while (true)
                {
                    if (!inputComplete)
                    {
                        if (offset < samples.Length && !cancellation.IsCanceled)
                        {
                            if (chunk is null)
                            {
                                chunkLength = Math.Min(LiveChunkSamples, samples.Length - offset);
                                chunk = WhisperAudio.CreateWaveformTensor(audioFactory, samples.AsSpan(offset, chunkLength), out _);
                            }

                            SpeechRecognitionChunkResult written = input.WriteChunk(chunkId, chunk, (ulong)chunkLength);
                            ReleaseChunks(pending, written.LifetimeWatermark);
                            if (written.Accepted)
                            {
                                pending.Enqueue((chunkId, chunk));
                                chunk = null;
                                ++chunkId;
                                offset += chunkLength;
                                continue;
                            }

                            // The session holds the maximum pending chunks.
                            // Read the stream, then write the chunk again.
                        }
                        else
                        {
                            // The end of the audio, or a cancel: the stream
                            // completes once the session finishes.
                            input.CompleteInput();
                            inputComplete = true;
                        }
                    }

                    if (!stream.TryReadNext(out SpeechRecognitionReadStatus status, out ReadOnlySpan<char> text))
                    {
                        break;
                    }

                    switch (status)
                    {
                        case SpeechRecognitionReadStatus.Update:
                            streamed.Append(text);
                            Console.Write(text.ToString());
                            break;
                        case SpeechRecognitionReadStatus.NeedInput when !inputComplete:
                            // Write the next chunk.
                            break;
                        default:
                            throw WinMLException.Unexpected($"Unexpected read status {status}.");
                    }
                }

                using SpeechRecognitionResult result = stream.GetResult();
                return (new SpeechRecognitionOutcome(result.Transcript, result.FinishReason, result.ErrorCode), streamed.ToString());
            }
            finally
            {
                // Close the stream first: it cancels an operation that has
                // not completed, so the session stops reading the chunks and
                // the live input can close. Then release the chunks.
                stream.Dispose();
                input.Dispose();
                chunk?.Dispose();
                ReleaseChunks(pending, ulong.MaxValue);
            }
        }
    }

    private static bool SupportsLiveInput(SpeechRecognitionSession session)
    {
        if (!session.TryAs(out SpeechRecognitionLiveSession? live))
        {
            return false;
        }

        live.Dispose();
        return true;
    }

    // Disposes the chunks whose IDs are at most watermark.
    private static void ReleaseChunks(Queue<(ulong Id, Tensor Waveform)> pending, ulong? watermark)
    {
        while (watermark is ulong released && pending.TryPeek(out (ulong Id, Tensor Waveform) oldest) && oldest.Id <= released)
        {
            pending.Dequeue().Waveform.Dispose();
        }
    }

    // A failed operation fails the sample, and the streamed fragments must
    // add up to the final transcript.
    private static SpeechRecognitionFinishReason CheckOutcome(SpeechRecognitionOutcome outcome, string streamed)
    {
        if (outcome.FinishReason == SpeechRecognitionFinishReason.Error ||
            (outcome.FinishReason != SpeechRecognitionFinishReason.Canceled && outcome.ErrorCode < 0))
        {
            throw WinMLException.GetExceptionForHResult(outcome.ErrorCode);
        }

        if (outcome.Transcript != streamed)
        {
            throw WinMLException.InvalidData("The transcript does not match the streamed fragments.");
        }

        Console.WriteLine();
        return outcome.FinishReason;
    }

    private static string DescribeFinishReason(SpeechRecognitionFinishReason reason)
    {
        return reason switch
        {
            SpeechRecognitionFinishReason.InputComplete => "input complete",
            SpeechRecognitionFinishReason.Capacity => "reached the model's capacity",
            SpeechRecognitionFinishReason.Canceled => "canceled",
            _ => "error",
        };
    }

    // Prints the HRESULT of a failed sample and what usually causes it.
    private static void ReportFailure(string operation, Exception ex)
    {
        Console.Error.WriteLine($"{operation} failed: 0x{ex.HResult:X8}");
        switch (ex)
        {
            case InvalidCastException:
                Console.Error.WriteLine(
                    "The Windows ML package deployed with this app does not provide this API. Build with a package " +
                    "that includes model-backed Tasks; see the model-backed README.");
                break;
            case NotSupportedException:
                Console.Error.WriteLine(
                    "Windows ML does not support this model, option or execution target for this Task.");
                break;
            case FileNotFoundException:
            case DirectoryNotFoundException:
                Console.Error.WriteLine("A model or input file was not found. Check the paths.");
                break;
        }
    }
}

// Ctrl+C cancels Token while the scope is open. The source is not disposed:
// the console handler can still be running on its own thread when the scope
// ends, and a source without timers holds nothing that needs releasing.
internal sealed class CancelKeyScope : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly ConsoleCancelEventHandler _handler;

    public CancelKeyScope()
    {
        _handler = (_, e) =>
        {
            e.Cancel = true;
            _source.Cancel();
        };
        Console.CancelKeyPress += _handler;
    }

    public CancellationToken Token => _source.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= _handler;
    }
}

internal sealed record Options(
    string ModelDirectory,
    string TokenizerPath,
    string WavPath,
    string? Language,
    bool Translate,
    bool Live)
{
    // Parses the C++ sample's arguments plus --live. Returns null for
    // arguments that the sample does not accept.
    public static Options? Parse(string[] args)
    {
        List<string> positional = [];
        string? language = null;
        bool translate = false;
        bool live = false;
        for (int index = 0; index < args.Length; ++index)
        {
            string argument = args[index];
            if (argument == "--language" && index + 1 < args.Length)
            {
                language = args[++index];
            }
            else if (argument == "--mode" && index + 1 < args.Length)
            {
                string mode = args[++index];
                translate = mode == "translate";
                if (!translate && mode != "transcribe")
                {
                    return null;
                }
            }
            else if (argument == "--live")
            {
                live = true;
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal) || positional.Count == 3)
            {
                return null;
            }
            else
            {
                positional.Add(argument);
            }
        }

        return positional.Count == 3 ? new Options(positional[0], positional[1], positional[2], language, translate, live) : null;
    }
}
