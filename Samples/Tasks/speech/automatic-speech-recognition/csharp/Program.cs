// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Automatic speech recognition with the Windows ML Task API, from C#:
// transcribe a WAV file with a Whisper Task.
//
// Transcribe a 16 kHz WAV file with Whisper and print the transcript as it is
// produced. This is the C# counterpart of ..\main.cpp; it takes the same
// arguments and prints the same output.
//
//   The app owns placement. It keeps the decoded PCM on a CPU target and
//   builds single-stage encoder and decoder pipelines on that same target.
//   EncoderDecoderSpeechRecognitionConfiguration.SetBindings hands the
//   pipelines to the Task, which runs the decode loop with the tokenizer the
//   task was created with. The target's AudioTensorFactory turns the PCM into
//   a Whisper waveform tensor. A SpeechRecognizer transcribes it with a
//   cancellation source and streams transcript updates with await foreach
//   until completion. The streamed text must match the transcript of the
//   final result, and an error finish fails the sample. Ctrl+C cancels
//   recognition through a CancellationToken.
//
// Run it
//   ..\..\..\run_automatic_speech_recognition.ps1 -Language csharp
//       [-ModelDir <dir>] [-WavPath <wav>] [-Platform ARM64|x64]
//       [-Configuration Debug|Release]
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

namespace AutomaticSpeechRecognition;

internal static class Program
{
    private const long WhisperMelBins = 80;
    private const long WhisperTimeFrames = 3000;

    private static async Task<int> Main(string[] args)
    {
        // ONNX Runtime builds the two Whisper pipelines directly from the
        // prepared encoder_model.onnx and decoder_model.onnx files.
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: task-automatic-speech-recognition-csharp.exe <model-directory> <input.wav>");
            return 2;
        }

        // Transcripts are not limited to the console's code page.
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            await RunAsync(args[0], args[1]);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Speech recognition failed: 0x{ex.HResult:X8}");
            return 1;
        }
    }

    private static async Task RunAsync(string modelDirectory, string wavPath)
    {
        // The Runtime creates the targets and pipelines that the ASR Task composes.
        using WinMLRuntime runtime = WinMLRuntime.Create();

        // WAV decoding produces host-resident PCM. Keep that application input on CPU.
        using ExecutionTarget waveformTarget = runtime.CreateCpuExecutionTarget();

        // WhisperSpeechRecognition.BuildAsync loads encoder_model.onnx,
        // decoder_model.onnx, and tokenizer.json, then validates the typed Task
        // configuration over those caller-owned Runtime objects.
        await using WhisperSpeechRecognition whisper = await WhisperSpeechRecognition.BuildAsync(runtime, waveformTarget, modelDirectory);
        SpeechRecognizer recognizer = whisper.Recognizer;
        await recognizer.InvokeAsync(session =>
        {
            // The session discloses the Runtime that owns it: the one the app created.
            using (TaskRuntimeIdentity identity = session.As<TaskRuntimeIdentity>())
            using (WinMLRuntime sessionRuntime = identity.GetRuntime())
            {
                if (!sessionRuntime.IsSameObject(runtime))
                {
                    throw WinMLException.Unexpected("The session belongs to another Runtime.");
                }
            }

            if (session.State != SpeechRecognitionSessionState.Ready)
            {
                throw WinMLException.Unexpected($"The session is {session.State}, not ready.");
            }

            // An encoder-decoder session also transcribes encoder input that the
            // app computed, such as Whisper log-mel features.
            if (!session.TryAs(out SpeechRecognitionEncoderInput? encoderInput))
            {
                throw WinMLException.NoInterface("The encoder-decoder session does not take encoder input.");
            }

            encoderInput.Dispose();
            return true;
        });

        // The Task receives both the waveform tensor and metadata describing the
        // valid sample span.
        using Tensor waveform = CreateWaveformTensor(waveformTarget, wavPath, out SpeechRecognitionWaveformMetadata metadata);

        // The recognizer passes a cancellation source into the operation, and
        // Ctrl+C cancels it. The streamed text must add up to the transcript of
        // the final result.
        Console.Write("Transcript: ");
        StringBuilder streamed = new();
        CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancel;
        try
        {
            await foreach (SpeechRecognitionUpdate update in recognizer.TranscribeWaveformAsync(waveform, metadata, cancellation.Token))
            {
                // Without predictions, every update carries text.
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
        finally
        {
            Console.CancelKeyPress -= cancel;
        }

        // An operation that completed or was canceled always has an outcome.
        SpeechRecognitionOutcome outcome = recognizer.LastOutcome!;
        if (outcome.FinishReason == SpeechRecognitionFinishReason.Error ||
            (outcome.FinishReason != SpeechRecognitionFinishReason.Canceled && outcome.ErrorCode < 0))
        {
            throw WinMLException.GetExceptionForHResult(outcome.ErrorCode);
        }

        if (outcome.Transcript != streamed.ToString())
        {
            throw WinMLException.InvalidData("The transcript does not match the streamed fragments.");
        }

        Console.WriteLine();
    }

    // The Task consumes a Runtime tensor plus waveform metadata. This adapter
    // keeps file parsing outside the Task API and checks the expected PCM format.
    private static Tensor CreateWaveformTensor(ExecutionTarget target, string wavPath, out SpeechRecognitionWaveformMetadata metadata)
    {
        WavData audio = WhisperAudio.LoadWavFile(wavPath);
        if (audio.Samples.Length == 0 || audio.SampleRate != WhisperAudio.SampleRate)
        {
            throw WinMLException.InvalidData("The WAV file holds no 16 kHz audio.");
        }

        return WhisperAudio.CreateWaveformTensor(target.AudioTensorFactory, audio.Samples, out metadata);
    }

    // Whisper is represented as independent encoder and decoder pipelines. The
    // typed Task owns their orchestration while the caller owns Runtime placement.
    private sealed class WhisperSpeechRecognition : IAsyncDisposable
    {
        private readonly List<IDisposable> _objects;

        private WhisperSpeechRecognition(List<IDisposable> objects, SpeechRecognizer recognizer)
        {
            _objects = objects;
            Recognizer = recognizer;
        }

        public SpeechRecognizer Recognizer { get; }

        public static async Task<WhisperSpeechRecognition> BuildAsync(WinMLRuntime runtime, ExecutionTarget target, string modelDirectory)
        {
            string encoderPath = ResolveWhisperModelPath(modelDirectory, "encoder_model");
            string decoderPath = ResolveWhisperModelPath(modelDirectory, "decoder_model");
            string tokenizerPath = Path.Combine(modelDirectory, "tokenizer.json");
            if (!File.Exists(tokenizerPath))
            {
                throw new FileNotFoundException("tokenizer.json was not found.", tokenizerPath);
            }

            // The pipelines, stages, and models stay alive with the session.
            List<IDisposable> objects = [];
            try
            {
                // This prepared encoder needs two ONNX Runtime optimizations
                // disabled. The session configuration keeps that model-specific
                // choice next to the stage that consumes it.
                (Pipeline encoderPipeline, Stage encoderStage) = BuildSingleStagePipeline(
                    runtime,
                    target,
                    encoderPath,
                    "task-whisper-encoder",
                    [("feature_size", WhisperMelBins), ("encoder_sequence_length", WhisperTimeFrames)],
                    [("optimization.disable_specified_optimizers", "NhwcTransformer;ConvActivationFusion")],
                    objects);
                (Pipeline decoderPipeline, Stage decoderStage) = BuildSingleStagePipeline(
                    runtime, target, decoderPath, "task-whisper-decoder", [], [], objects);
                Tokenizer tokenizer = Tokenizer.CreateFromFile(tokenizerPath);
                objects.Add(tokenizer);

                // What the package's C++ BuildAutomaticSpeechRecognition helper does:
                // create the task, bind the pipelines with SetBindings, validate,
                // and create the session.
                WinMLTasks tasks = WinMLTasks.Create(runtime);
                objects.Add(tasks);
                AutomaticSpeechRecognitionTask task = tasks.AutomaticSpeechRecognition.CreateAutomaticSpeechRecognitionTask(tokenizer);
                objects.Add(task);
                using SpeechRecognitionConfiguration configuration = task.CreateConfiguration();
                using (EncoderDecoderSpeechRecognitionConfiguration whisper = configuration.As<EncoderDecoderSpeechRecognitionConfiguration>())
                {
                    whisper.SetBindings(new EncoderDecoderSpeechRecognitionBindings
                    {
                        EncoderPipeline = encoderPipeline,
                        EncoderStage = encoderStage,
                        DecoderPipeline = decoderPipeline,
                        DecoderStage = decoderStage,
                        TensorTarget = target,
                    });
                }

                configuration.Validate();
                SpeechRecognizer recognizer = await SpeechRecognizer.CreateAsync(tasks, task, configuration);
                return new WhisperSpeechRecognition(objects, recognizer);
            }
            catch
            {
                DisposeAll(objects);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Recognizer.DisposeAsync();
            DisposeAll(_objects);
        }

        // Locate one required Whisper graph before it is loaded through the Runtime.
        private static string ResolveWhisperModelPath(string modelDirectory, string stem)
        {
            string onnxPath = Path.Combine(modelDirectory, stem + ".onnx");
            return File.Exists(onnxPath) ? onnxPath : throw new FileNotFoundException($"{stem}.onnx was not found.", onnxPath);
        }

        // The smallest useful Runtime pipeline: load one model, place it on a
        // target, resolve symbolic dimensions and pass ONNX Runtime session
        // settings when given, and build the pipeline. The model, stage, and
        // pipeline are added to objects.
        private static (Pipeline Pipeline, Stage Stage) BuildSingleStagePipeline(
            WinMLRuntime runtime,
            ExecutionTarget target,
            string modelPath,
            string debugName,
            (string Name, long Extent)[] symbolicDimensions,
            (string Key, string Value)[] ortSessionConfig,
            List<IDisposable> objects)
        {
            Model model = runtime.LoadModelFromFile(modelPath);
            objects.Add(model);
            using PipelineBuilder builder = runtime.CreatePipelineBuilder();
            Stage stage = builder.AddModelStage(model, target, debugName);
            objects.Add(stage);
            if (symbolicDimensions.Length != 0)
            {
                using OnnxSymbolicDimensionOverrides overrides = stage.As<OnnxSymbolicDimensionOverrides>();
                foreach ((string name, long extent) in symbolicDimensions)
                {
                    overrides.SetOverride(name, extent);
                }
            }

            if (ortSessionConfig.Length != 0)
            {
                using OrtStageOptions ortOptions = stage.As<OrtStageOptions>();
                foreach ((string key, string value) in ortSessionConfig)
                {
                    ortOptions.SetSessionConfigEntry(key, value);
                }
            }

            Pipeline pipeline = builder.Build();
            objects.Add(pipeline);
            return (pipeline, stage);
        }

        private static void DisposeAll(List<IDisposable> objects)
        {
            for (int i = objects.Count - 1; i >= 0; --i)
            {
                objects[i].Dispose();
            }

            objects.Clear();
        }
    }
}
