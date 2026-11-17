// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Chat completion: a two-turn conversation through the Chat Completion Task,
// from C#.
//
// Ask a question, then a follow-up that depends on the answer. The app keeps
// the message history and sends all of it on each turn. This is the C#
// counterpart of ..\main.cpp; it takes the same arguments and prints the same
// output.
//
//   A greedy Text Generation session on a CPU target backs a chat session
//   from the Chat Completion Task, so tokens it has already evaluated carry
//   over between turns. Each turn sends the full history; the session
//   applies the chat template, a ChatGenerator streams content deltas with
//   await foreach, and its outcome gives the reply, token counts, and finish
//   reason. When a prompt starts with tokens the session already holds, only
//   the rest is evaluated, and the outcome reports how many were reused. One
//   cancellation token serves both turns. Ctrl+C cancels it: the canceled
//   reply reports what was generated, and the conversation ends there.
//
// Run it
//   ..\..\..\run_chat_completion.ps1 -Language csharp [-Backend ort|llama]
//       [-Prompt <text>] [-FollowUp <text>] [-MaxNewTokens <count>]
//
// The sample uses the sample wrapper in ..\..\..\..\Runtime\shared\csharp\Runtime,
// an idiomatic C# layer over the Runtime and Task APIs in the
// Microsoft.Windows.AI.MachineLearning package. Objects are IDisposable, and
// failures throw .NET exceptions that keep the HRESULT. A text generation
// session belongs to the thread that created it; ChatGenerator keeps it and the
// chat session on a thread of their own, so the sample can stream from any
// thread. The wrapper is sample code; see ..\..\..\..\Runtime\shared\csharp\README.md.

using System.Text;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace ChatCompletion;

internal static class Program
{
    private const string DefaultQuestion = "Name three primary colors.";
    private const string DefaultFollowUp = "Which of those is the color of the sky?";
    private const uint DefaultMaxNewTokens = 48;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: task-chat-completion-csharp.exe <model> <tokenizer-source|-> [question] [follow-up] [max-new-tokens]");
            return 2;
        }

        string? tokenizerSource = args[1] == "-" ? null : args[1];
        string[] questions =
        [
            args.Length >= 3 ? args[2] : DefaultQuestion,
            args.Length >= 4 ? args[3] : DefaultFollowUp,
        ];
        uint maxNewTokens = DefaultMaxNewTokens;
        if (args.Length >= 5 && !TryParsePositiveTokenCount(args[4], out maxNewTokens))
        {
            Console.Error.WriteLine("max-new-tokens must be a positive 32-bit integer.");
            return 2;
        }

        // Model replies are not limited to the console's code page.
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            // The chat Task runs on a Text Generation Task, built here on the
            // CPU with greedy decoding so the replies are the same from run to run.
            using WinMLRuntime runtime = WinMLRuntime.Create();
            using ExecutionTarget target = runtime.CreateCpuExecutionTarget();
            using TextGenerationObjects objects = TextGenerationBuilder.Build(runtime, target, args[0], tokenizerSource, maxNewTokens);

            // The chat session wraps the text generation session, and the
            // tokens that session holds carry over from one turn to the next.
            await using ChatGenerator chat = await objects.CreateChatGeneratorAsync();
            await RunConversationAsync(chat, objects.Options, questions);

            // Closing the sessions reports a failure that disposing them would not.
            await chat.CloseAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Chat completion failed: 0x{ex.HResult:X8}");
            return 1;
        }
    }

    private static async Task RunConversationAsync(ChatGenerator chat, TextGenerationOptions options, string[] questions)
    {
        // Ctrl+C cancels generation. One token serves both turns, so the
        // conversation ends with a canceled turn. The source is not
        // disposed: the console handler can still be running on its own
        // thread when the conversation ends, and a source without timers
        // holds nothing that needs releasing.
        CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancel;
        try
        {
            List<ConversationMessage> history = [];
            foreach (string question in questions)
            {
                history.Add(new ConversationMessage { Role = "user", Content = question });
                WriteText(Console.Out, $"User: {question}\nAssistant: ");
                (string reply, bool canceled) = await CompleteTurnAsync(chat, options, history, cancellation.Token);
                if (canceled)
                {
                    // The token stays canceled, so a canceled turn ends the conversation.
                    break;
                }

                history.Add(new ConversationMessage { Role = "assistant", Content = reply });
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    // Send the conversation, print the reply as it streams, and return the
    // reply so the caller can add it to the history, and whether it was canceled.
    private static async Task<(string Reply, bool Canceled)> CompleteTurnAsync(
        ChatGenerator chat,
        TextGenerationOptions options,
        List<ConversationMessage> history,
        CancellationToken cancellationToken)
    {
        ConversationRequest request = new()
        {
            FormatterMode = ConversationFormatterMode.ModelDefault,
            Messages = [.. history],
            AddGenerationPrompt = true,
        };

        // Content events carry the reply as it is generated. This model and
        // prompt produce no reasoning or tool-call events.
        int? eventError = null;
        try
        {
            await foreach (ChatCompletionUpdate update in chat.GenerateAsync(request, options, cancellationToken))
            {
                if (update.Type == ConversationOutputEventType.ContentDelta)
                {
                    WriteText(Console.Out, update.Text);
                }
                else if (update.Type == ConversationOutputEventType.Error)
                {
                    WriteText(Console.Error, $"\nGeneration failed: {update.Text}\n");
                    eventError = update.ErrorCode;
                    break;
                }
            }
        }
        catch (Exception) when (eventError is null && chat.LastOutcome is not null)
        {
            // A completion that ran, including a canceled one, reports its
            // outcome below.
        }

        if (eventError is int error)
        {
            throw WinMLException.GetExceptionForHResult(error);
        }

        Console.WriteLine();
        ChatCompletionOutcome outcome = chat.LastOutcome!;
        bool canceled = outcome.FinishReason == ChatCompletionFinishReason.Canceled;

        // A canceled completion reports HRESULT_FROM_WIN32(ERROR_CANCELLED) as
        // its error code; it still reports what it generated.
        if (outcome.ErrorCode < 0 && !canceled)
        {
            throw WinMLException.GetExceptionForHResult(outcome.ErrorCode);
        }

        Console.WriteLine(
            $"  Tokens: prompt={outcome.PromptTokenCount} (reused={outcome.PromptCacheUse.CachedPromptTokenCount}) " +
            $"completion={outcome.CompletionTokenCount}");
        Console.WriteLine($"  Finished because: {DescribeChatFinishReason(outcome.FinishReason)}");
        return (outcome.Content, canceled);
    }

    private static string DescribeChatFinishReason(ChatCompletionFinishReason reason)
    {
        return reason switch
        {
            ChatCompletionFinishReason.EosToken => "end-of-sequence token",
            ChatCompletionFinishReason.StopToken or ChatCompletionFinishReason.AdditionalStop => "stop token",
            ChatCompletionFinishReason.MaxTokens => "reached max-new-tokens",
            ChatCompletionFinishReason.SequenceCapacity => "reached the model's sequence capacity",
            ChatCompletionFinishReason.Canceled => "canceled",
            _ => "error",
        };
    }

    // Writes text with Windows line endings, as the C++ sample's text-mode
    // console output does, including the line breaks in a reply.
    internal static void WriteText(TextWriter writer, string text)
    {
        writer.Write(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    // Accepts what the C++ sample's ParsePositiveTokenCount accepts: optional
    // leading white space and plus sign, then decimal digits with nothing
    // after them, for a value from 1 to 4294967295. Like wcstoull, it takes
    // ASCII white space and the digits of the blocks in DigitZeros.
    private static bool TryParsePositiveTokenCount(string value, out uint count)
    {
        count = 0;
        int index = 0;
        while (index < value.Length && value[index] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
        {
            ++index;
        }

        if (index < value.Length && value[index] == '+')
        {
            ++index;
        }

        int firstDigit = index;
        ulong parsed = 0;
        for (int digit; index < value.Length && (digit = DigitValue(value[index])) >= 0; ++index)
        {
            parsed = parsed * 10 + (ulong)digit;
            if (parsed > uint.MaxValue)
            {
                return false;
            }
        }

        if (index == firstDigit || index != value.Length || parsed == 0)
        {
            return false;
        }

        count = (uint)parsed;
        return true;
    }

    // The zero of each block of ten decimal digits that wcstoull accepts.
    private static readonly char[] DigitZeros =
    [
        '0', '\u0660', '\u06F0', '\u0966', '\u09E6', '\u0A66', '\u0AE6', '\u0B66', '\u0C66',
        '\u0CE6', '\u0D66', '\u0E50', '\u0ED0', '\u0F20', '\u1040', '\u17E0', '\u1810', '\uFF10',
    ];

    private static int DigitValue(char c)
    {
        foreach (char zero in DigitZeros)
        {
            if (c >= zero && c - zero <= 9)
            {
                return c - zero;
            }
        }

        return -1;
    }
}

// The Text Generation Task objects behind the chat session: a port of
// BuildTextGeneration in ..\..\..\shared\text_generation_task.h. The model's
// file extension selects how the Runtime pipeline is built; every route then
// configures the Task the same way.
internal static class TextGenerationBuilder
{
    // Capacity used only when a model does not declare one. The prepared Qwen
    // export is built with this many positions.
    private const ulong DefaultSequenceCapacity = 128;

    // Symbolic dimensions to fix on an ONNX stage. The exported model has none
    // to override.
    private static readonly (string Name, long Extent)[] SymbolicDimensionOverrides = [];

    public static TextGenerationObjects Build(
        WinMLRuntime runtime,
        ExecutionTarget target,
        string modelPath,
        string? tokenizerSource,
        uint maxNewTokens)
    {
        TextGenerationObjects objects = new();
        try
        {
            string extension = GetLowercaseExtension(modelPath);
            if (extension == ".gguf")
            {
                BuildGguf(objects, runtime, target, modelPath, maxNewTokens);
                return objects;
            }

            // ONNX models read their tokenizer from a separate source.
            if (tokenizerSource is null)
            {
                throw new ArgumentNullException(nameof(tokenizerSource));
            }

            if (extension == ".ort")
            {
                Model model = objects.Own(runtime.LoadModelFromFile(modelPath));
                BuildOrdinal(objects, runtime, target, model, tokenizerSource, maxNewTokens,
                    ReadDeclaredSequenceCapacity(model, DefaultSequenceCapacity));
                return objects;
            }

            if (extension != ".onnx")
            {
                throw WinMLException.NotSupported($"Unsupported model file type: {extension}");
            }

            BuildOnnx(objects, runtime, target, modelPath, tokenizerSource, maxNewTokens, DefaultSequenceCapacity);
            return objects;
        }
        catch
        {
            objects.Dispose();
            throw;
        }
    }

    // GGUF models expose a logical token input, logits output, tokenizer, and
    // state owner directly. The Task remains independent of the llama.cpp
    // implementation.
    private static void BuildGguf(
        TextGenerationObjects objects,
        WinMLRuntime runtime,
        ExecutionTarget target,
        string modelPath,
        uint maxNewTokens)
    {
        Model model = objects.Own(runtime.LoadModelFromFile(modelPath));
        Stage stage;
        Pipeline pipeline;
        using (PipelineBuilder builder = runtime.CreatePipelineBuilder())
        {
            stage = objects.Own(builder.AddModelStage(model, target, "task-text-generation"));
            pipeline = objects.Own(builder.Build());
        }

        Tokenizer tokenizer = objects.Own(Tokenizer.CreateFromFile(modelPath));
        Configure(objects, runtime, pipeline, stage, 0, stage, 0, stage, target, tokenizer, maxNewTokens);
    }

    // Compiled artifacts preserve logical ordinals but do not expose ONNX
    // names. Declare state and output behavior before Build, then configure
    // the Task the same way as the source-model path.
    private static void BuildOrdinal(
        TextGenerationObjects objects,
        WinMLRuntime runtime,
        ExecutionTarget target,
        Model model,
        string tokenizerSource,
        uint maxNewTokens,
        ulong sequenceCapacity)
    {
        Stage stage;
        Pipeline pipeline;
        using (PipelineBuilder builder = runtime.CreatePipelineBuilder())
        {
            stage = objects.Own(builder.AddModelStage(model, target, "task-compiled-text-generation"));
            using (StatefulStageOptions statefulOptions = stage.As<StatefulStageOptions>())
            {
                statefulOptions.AddStateTensorPair(1, 1);
                statefulOptions.SequenceCapacityHint = sequenceCapacity;
            }

            stage.RequestOutput(0);
            pipeline = objects.Own(builder.Build());
        }

        Tokenizer tokenizer = objects.Own(Tokenizer.CreateFromFile(tokenizerSource));
        Configure(objects, runtime, pipeline, stage, 0, stage, 0, stage, target, tokenizer, maxNewTokens);
    }

    // The ONNX route resolves the token and logits endpoints by name, as the
    // package's winml\tasks\text_generation\onnx\TextGeneration.hpp helper
    // does. State pairs are still supplied by the caller because cache
    // ownership is part of the model format, not something the Task can
    // safely infer. Capacity is read from the model this function loads.
    private static void BuildOnnx(
        TextGenerationObjects objects,
        WinMLRuntime runtime,
        ExecutionTarget target,
        string modelPath,
        string tokenizerSource,
        uint maxNewTokens,
        ulong fallbackCapacity)
    {
        objects.ReportsOnnxSteps = true;
        Model model = objects.Own(OnnxStep("load model", () => runtime.LoadModelFromFile(modelPath)));
        ulong sequenceCapacity = ReadDeclaredSequenceCapacity(model, fallbackCapacity);
        Tokenizer tokenizer = objects.Own(OnnxStep("load tokenizer", () => Tokenizer.CreateFromFile(tokenizerSource)));

        // Building the Task includes creating its session, which
        // TextGenerationObjects.CreateChatGeneratorAsync reports under this step.
        OnnxStep("build Task", () =>
        {
            uint tokenInputIndex;
            uint outputIndex;
            using (OrtModelSchema schema = model.As<OrtModelSchema>())
            {
                tokenInputIndex = schema.FindInputIndex("input_ids");
                outputIndex = schema.FindOutputIndex("logits");
            }

            Stage stage;
            Pipeline pipeline;
            using (PipelineBuilder builder = runtime.CreatePipelineBuilder())
            {
                stage = objects.Own(builder.AddModelStage(model, target, "task-onnx-text-generation"));
                using (OnnxSymbolicDimensionOverrides overrides = stage.As<OnnxSymbolicDimensionOverrides>())
                {
                    foreach ((string name, long extent) in SymbolicDimensionOverrides)
                    {
                        overrides.SetOverride(name, extent);
                    }
                }

                using (StatefulStageOptions statefulOptions = stage.As<StatefulStageOptions>())
                {
                    statefulOptions.AddStateTensorPair(1, 1);
                    if (sequenceCapacity != 0)
                    {
                        statefulOptions.SequenceCapacityHint = sequenceCapacity;
                    }
                }

                stage.RequestOutput(outputIndex);
                pipeline = objects.Own(builder.Build());
            }

            Configure(objects, runtime, pipeline, stage, tokenInputIndex, stage, outputIndex, stage, target, tokenizer, maxNewTokens);
        });
    }

    // Configures one unified pipeline for the Text Generation Task, with the
    // calls, in the order, of BuildTextGeneration in the package's
    // winml\tasks\text_generation\TextGeneration.hpp helper. The session is
    // created later, by the ChatGenerator.
    private static void Configure(
        TextGenerationObjects objects,
        WinMLRuntime runtime,
        Pipeline pipeline,
        Stage tokenInputStage,
        uint tokenInputIndex,
        Stage outputStage,
        uint outputIndex,
        Stage stateOwnerStage,
        ExecutionTarget tokenTarget,
        Tokenizer tokenizer,
        uint maxNewTokens)
    {
        WinMLTasks tasks = objects.Own(WinMLTasks.Create(runtime));
        TextGenerationTask task = objects.Own(tasks.TextGeneration.CreateTextGenerationTask(tokenizer));
        TextGenerationOptions options = objects.Own(tasks.TextGeneration.CreateTextGenerationOptions(maxNewTokens));
        TextGenerationConfiguration configuration = objects.Own(tasks.TextGeneration.CreateTextGenerationConfiguration());
        configuration.Mode = TextGenerationConfigurationMode.Unified;
        configuration.SetPipeline(TextGenerationPipelinePhase.Unified, pipeline);
        configuration.SetTokenInput(TextGenerationPipelinePhase.Unified, tokenInputStage, tokenInputIndex);
        configuration.SetOutput(TextGenerationPipelinePhase.Unified, outputStage, outputIndex, TextGenerationOutputKind.Logits);
        configuration.SetStateOwnerStage(stateOwnerStage);
        configuration.SetTokenTarget(tokenTarget);
        configuration.SetTokenizer(tokenizer);
        configuration.Validate();

        objects.Tasks = tasks;
        objects.Task = task;
        objects.Options = options;
        objects.Configuration = configuration;
        objects.Tokenizer = tokenizer;
        objects.StateOwnerStage = stateOwnerStage;
    }

    // The exported model declares its own capacity in the packed state it
    // binds, so the sample reads it instead of assuming a value. A model that
    // does not publish a usable capacity falls back to the documented default.
    private static ulong ReadDeclaredSequenceCapacity(Model model, ulong fallbackCapacity)
    {
        try
        {
            using ModelSchema schema = model.As<ModelSchema>();
            TensorSchemaDescription state = schema.GetInputTensorDescription(1);
            if (state.Dimensions.Length != 4)
            {
                return fallbackCapacity;
            }

            ulong declared = state.Dimensions[2];
            return declared is 0 or ulong.MaxValue ? fallbackCapacity : declared;
        }
        catch (Exception)
        {
            // A model without a schema or a state input has no declared capacity.
            return fallbackCapacity;
        }
    }

    // Reports which step of the ONNX route failed, then rethrows.
    internal static T OnnxStep<T>(string step, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            ReportOnnxStepFailure(step, ex);
            throw;
        }
    }

    internal static void OnnxStep(string step, Action action)
    {
        OnnxStep(step, () =>
        {
            action();
            return true;
        });
    }

    internal static void ReportOnnxStepFailure(string step, Exception ex)
    {
        Console.Error.WriteLine($"ONNX text generation failed at {step}: 0x{ex.HResult:X8}");
    }

    // The extension as std::filesystem::path::extension returns it, in lower
    // case: empty for a file name that only starts with a dot.
    private static string GetLowercaseExtension(string path)
    {
        string fileName = Path.GetFileName(path);
        int dot = fileName.LastIndexOf('.');
        return dot <= 0 || fileName == ".." ? string.Empty : fileName[dot..].ToLowerInvariant();
    }
}

// Owns the objects the Text Generation Task uses, and disposes them in the
// reverse order of creation.
internal sealed class TextGenerationObjects : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public WinMLTasks Tasks { get; set; } = null!;

    public TextGenerationTask Task { get; set; } = null!;

    public TextGenerationOptions Options { get; set; } = null!;

    public TextGenerationConfiguration Configuration { get; set; } = null!;

    public Tokenizer Tokenizer { get; set; } = null!;

    public Stage StateOwnerStage { get; set; } = null!;

    // The ONNX route reports the step at which it failed.
    public bool ReportsOnnxSteps { get; set; }

    public T Own<T>(T value)
        where T : IDisposable
    {
        _owned.Add(value);
        return value;
    }

    // Creates the Text Generation session and a chat session over it. The
    // sequence capacity is read once the session exists, before the chat Task
    // is created, as the C++ sample does.
    public async Task<ChatGenerator> CreateChatGeneratorAsync()
    {
        bool sessionCreated = false;
        try
        {
            return await ChatGenerator.CreateAsync(Tasks, Task, Configuration, Tokenizer, _ =>
            {
                sessionCreated = true;
                if (ReportsOnnxSteps)
                {
                    TextGenerationBuilder.OnnxStep("read capacity", ReadCapacity);
                }
                else
                {
                    ReadCapacity();
                }
            });
        }
        catch (Exception ex) when (ReportsOnnxSteps && !sessionCreated)
        {
            TextGenerationBuilder.ReportOnnxStepFailure("build Task", ex);
            throw;
        }
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; --i)
        {
            _owned[i].Dispose();
        }

        _owned.Clear();
    }

    // Reads the sequence capacity of the stateful stage that backs the
    // session. The chat sample does not print it, but a state owner stage
    // that cannot report it fails here, as in the C++ sample.
    private void ReadCapacity()
    {
        using StatefulStage statefulStage = StateOwnerStage.As<StatefulStage>();
        _ = statefulStage.SequenceCapacity;
        _ = statefulStage.GetDeclaredSequenceCapacity(out _);
    }
}
