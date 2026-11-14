// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Models-backed text generation with the Windows ML Task API, from C#.
//
// Bring your own weights: generate text from a published checkpoint folder or a
// single GGUF file. The app builds no pipeline and passes no tokenizer. This is
// the C# counterpart of ..\main.cpp and, with --llama, of
// ..\..\llama-text-generation\main.cpp; it prints the same output as they do.
//
//   How the app loads the model decides how Windows ML runs it. Without
//   --llama, WinMLRuntime.LoadModelFromPath (a checkpoint folder) or
//   LoadModelFromFile (a GGUF file) loads it, and SetModel binds it on a GPU
//   target: the model provider plans it for the GPU and creates the model's
//   own tokenizer from its files. With --llama, LlamaModelLoader loads a GGUF
//   file for the llama.cpp engine, on the CPU unless --device gpu, and a draft
//   model loaded the same way enables speculative decoding. Windows ML never
//   falls back from one route to the other.
//
//   Either way the configuration's tokenizer formats the prompt as one user
//   turn with the model's chat template, and a TextGenerator streams the reply
//   with await foreach as it is generated. The generator's result then reports
//   token counts and why generation stopped. Ctrl+C cancels generation through
//   a CancellationToken.
//
// Run it
//   .\run_model_text_generation.ps1 -Language csharp [-Format checkpoint|gguf]
//       [-ModelPath <checkpoint-folder|model.gguf>] [-SequenceCapacity <tokens>]
//       [-ProgramCache <directory> [-CacheWeights]]
//   .\run_llama_text_generation.ps1 -Language csharp [-ModelPath <model.gguf>]
//       [-DraftModel <draft.gguf>] [-Device cpu|gpu]
//
// The sample uses the sample wrapper in ..\..\..\..\Runtime\shared\csharp\Runtime,
// an idiomatic C# layer over the Runtime and Task APIs in the
// Microsoft.Windows.AI.MachineLearning package. Objects are IDisposable, and
// failures throw .NET exceptions that keep the HRESULT. A text generation
// session belongs to the thread that created it; TextGenerator keeps it on a
// thread of its own, so the sample can stream from any thread. The wrapper is
// sample code; see ..\..\..\..\Runtime\shared\csharp\README.md.

using System.Globalization;
using System.Text;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace ModelTextGeneration;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Options? options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Options.PrintUsage();
            return 2;
        }

        if (options is null)
        {
            Options.PrintUsage();
            return 0;
        }

        // Model replies are not limited to the console's code page.
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return options.Llama ? await RunLlamaAsync(options) : await RunModelProviderAsync(options);
        }
        catch (Exception ex)
        {
            ReportFailure(options.Llama ? "llama.cpp text generation" : "Text generation", ex);
            return 1;
        }
    }

    // Bring your own weights: the model provider serves the model on the GPU.
    private static async Task<int> RunModelProviderAsync(Options options)
    {
        using WinMLRuntime runtime = WinMLRuntime.Create();
        using ExecutionTarget? target = TryCreateGpuTarget(runtime);
        if (target is null)
        {
            Console.Error.WriteLine(
                "No GPU execution target was found (HRESULT_FROM_WIN32(ERROR_NOT_FOUND)). The model provider " +
                "plans the model for the GPU. On a device without a GPU, run a GGUF file on the CPU with --llama.");
            return 1;
        }

        // A checkpoint folder (config.json, safetensors weights, tokenizer.json
        // and tokenizer_config.json) loads with LoadModelFromPath; a single GGUF
        // file with LoadModelFromFile.
        using Model model = Directory.Exists(options.ModelPath)
            ? runtime.LoadModelFromPath(options.ModelPath)
            : runtime.LoadModelFromFile(options.ModelPath);

        // The Task API object comes from the Runtime. Its text generation factory
        // creates the configuration, whose model-backed interface binds a loaded
        // model in place of a pipeline.
        using WinMLTasks tasks = WinMLTasks.Create(runtime);
        using TextGenerationConfiguration configuration = tasks.TextGeneration.CreateTextGenerationConfiguration();

        // Sessions compile programs for the GPU. A program cache directory keeps
        // them, so later runs start faster. With --cache-weights it also keeps the
        // weights prepared for each program, so later runs read them instead of
        // preparing them again, at the cost of about one copy of the prepared
        // weights per program on disk.
        TextGenerationModelOptions modelOptions = new()
        {
            CompiledProgramDirectory = options.ProgramCache,
            CompiledProgramStorage = options.CacheWeights ? CompiledProgramStorage.ProgramsAndWeights : null,
        };

        // No SetTokenizer: SetModel creates the model's own tokenizer from its files.
        try
        {
            using ModelTextGenerationConfiguration modelConfiguration = configuration.As<ModelTextGenerationConfiguration>();
            modelConfiguration.SetModel(target, model, modelOptions);
        }
        catch (NotSupportedException)
        {
            Console.Error.WriteLine(
                "Windows ML cannot run this model for text generation (HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)).");
            Console.Error.WriteLine(
                "Use a supported checkpoint, such as Qwen2.5-0.5B-Instruct as a safetensors folder or GGUF file. " +
                "To run a GGUF file that Windows ML does not support on llama.cpp instead, load it with the " +
                "llama.cpp loader; run this sample with --llama.");
            return 1;
        }

        if (options.Capacity != 0)
        {
            configuration.LogicalCapacity = options.Capacity;
        }

        // The task must use the configuration's tokenizer: the one SetModel created.
        using Tokenizer tokenizer = configuration.GetTokenizer();
        using TextGenerationTask task = tasks.TextGeneration.CreateTextGenerationTask(tokenizer);
        await using TextGenerator generator = await TextGenerator.CreateAsync(tasks, task, configuration);
        await StreamChatReplyAsync(tasks, tokenizer, generator, options);
        return 0;
    }

    // Explicit llama.cpp: an engine-specific opt-in for text generation. Nothing
    // selects llama.cpp unless the app loads the model this way.
    private static async Task<int> RunLlamaAsync(Options options)
    {
        using WinMLRuntime runtime = WinMLRuntime.Create();

        // The loader is available when the Windows ML package includes the
        // llama.cpp engine.
        if (!runtime.TryAs(out LlamaModelLoader? loader))
        {
            Console.Error.WriteLine(
                "The llama.cpp model loader is not available (E_NOINTERFACE). " +
                "Use a Windows ML package that includes the llama.cpp engine.");
            return 1;
        }

        using (loader)
        {
            return await RunLlamaAsync(runtime, loader, options);
        }
    }

    private static async Task<int> RunLlamaAsync(WinMLRuntime runtime, LlamaModelLoader loader, Options options)
    {
        // The packaged llama.cpp backend runs on the CPU. A GPU target needs a
        // GPU backend that you deploy yourself.
        using ExecutionTarget? target = options.UseGpu ? TryCreateGpuTarget(runtime) : runtime.CreateCpuExecutionTarget();
        if (target is null)
        {
            Console.Error.WriteLine(
                "No GPU execution target was found (HRESULT_FROM_WIN32(ERROR_NOT_FOUND)). Use --device cpu.");
            return 1;
        }

        using Model model = loader.LoadModelFromFile(options.ModelPath);

        // A draft model must be loaded through the same loader and share the
        // model's tokenizer.
        using Model? draftModel = options.DraftModelPath is null ? null : loader.LoadModelFromFile(options.DraftModelPath);

        using WinMLTasks tasks = WinMLTasks.Create(runtime);
        using TextGenerationConfiguration configuration = tasks.TextGeneration.CreateTextGenerationConfiguration();

        // SetModel reads the tokenizer from the GGUF file. Each session loads the weights.
        using (ModelTextGenerationConfiguration modelConfiguration = configuration.As<ModelTextGenerationConfiguration>())
        {
            modelConfiguration.SetModel(target, model, new TextGenerationModelOptions { DraftModel = draftModel });
        }

        if (draftModel is not null)
        {
            using TextGenerationSpeculativeConfiguration speculative = configuration.As<TextGenerationSpeculativeConfiguration>();
            speculative.SetSpeculativeOptions(new TextGenerationSpeculativeOptions { Method = TextGenerationSpeculativeMethod.DraftModel });
        }

        using Tokenizer tokenizer = configuration.GetTokenizer();
        using TextGenerationTask task = tasks.TextGeneration.CreateTextGenerationTask(tokenizer);
        TextGenerator generator;
        try
        {
            generator = await TextGenerator.CreateAsync(tasks, task, configuration);
        }
        catch (NotSupportedException) when (options.UseGpu)
        {
            Console.Error.WriteLine(
                "No deployed llama.cpp backend runs on this GPU (HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). " +
                "Use --device cpu, or deploy a llama.cpp GPU backend with the app.");
            return 1;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException)
        {
            // Loading and binding succeed without the llama.cpp payload; the
            // first session loads it.
            Console.Error.WriteLine(
                $"This sample requires the llama.cpp payload, which was not found (0x{ex.HResult:X8}). Deploy it " +
                "beside the app by referencing Microsoft.Windows.AI.MachineLearning.LibLlama.Core.");
            return 1;
        }

        // Disposing the generator closes the session, which holds execution
        // resources until then.
        TextGenerationSpeculativeStatistics? statistics;
        await using (generator)
        {
            statistics = await StreamChatReplyAsync(tasks, tokenizer, generator, options);
        }

        if (draftModel is not null && statistics is TextGenerationSpeculativeStatistics speculativeStatistics)
        {
            Console.WriteLine(
                $"Speculative decoding: steps={speculativeStatistics.VerificationStepCount} " +
                $"drafted={speculativeStatistics.DraftTokenCount} accepted={speculativeStatistics.AcceptedDraftTokenCount}");
        }

        return 0;
    }

    // Returns null when the device has no GPU target.
    private static ExecutionTarget? TryCreateGpuTarget(WinMLRuntime runtime)
    {
        return runtime.TryCreateExecutionTarget(ExecutionTargetKind.Gpu, ExecutionTargetPreference.Performance, out ExecutionTarget? target)
            ? target
            : null;
    }

    // Formats the prompt as one user turn with the model's chat template, so an
    // instruct model ends its reply on its own, then streams the reply and
    // prints the token counts and why generation stopped. Returns the
    // speculative decoding statistics of a speculative configuration.
    private static async Task<TextGenerationSpeculativeStatistics?> StreamChatReplyAsync(
        WinMLTasks tasks,
        Tokenizer tokenizer,
        TextGenerator generator,
        Options options)
    {
        using TextGenerationOptions generationOptions = tasks.TextGeneration.CreateTextGenerationOptions(options.MaxNewTokens);
        uint[] promptTokens = EncodeChatPrompt(tokenizer, options.Prompt);
        Console.Write($"Prompt: {options.Prompt}\nReply: ");

        // Ctrl+C cancels generation. The source is not disposed: the console
        // handler can still be running on its own thread when the stream ends,
        // and a source without timers holds nothing that needs releasing.
        CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        StringBuilder streamed = new();
        Console.CancelKeyPress += cancel;
        try
        {
            await foreach (TextGenerationUpdate update in generator.GenerateTokensAsync(promptTokens, generationOptions, cancellation.Token))
            {
                streamed.Append(update.Text);
                Console.Write(update.Text);
            }
        }
        catch (OperationCanceledException) when (generator.LastResult is not null)
        {
            // A canceled generation still reports what it generated.
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }

        // The streamed fragments and the final text must agree. A generation
        // that completed or was canceled always has a result.
        TextGenerationResult result = generator.LastResult!;
        if (result.Text != streamed.ToString())
        {
            throw new InvalidDataException("The result text does not match the streamed fragments.");
        }

        Console.WriteLine();
        Console.WriteLine($"Tokens: prompt={result.PromptTokenCount} generated={result.GetTokenIds().Length}");
        Console.WriteLine($"Finished because: {DescribeFinishReason(result.FinishReason)}");

        // Closing the session reports a failure that disposing it would not.
        await generator.CloseAsync();

        // Results of a speculative configuration also report how many draft
        // tokens the model accepted.
        if (result.TryAs(out TextGenerationSpeculativeResult? speculative))
        {
            using (speculative)
            {
                return speculative.SpeculativeStatistics;
            }
        }

        return null;
    }

    // An instruct model ends its reply with an end-of-sequence token when the
    // prompt uses the chat format it was trained on. The structured
    // conversation formatter renders the model's own chat template for one user
    // turn and returns the prompt token IDs.
    private static uint[] EncodeChatPrompt(Tokenizer tokenizer, string userMessage)
    {
        using StructuredConversationFormatter formatter = tokenizer.As<StructuredConversationFormatter>();
        using ConversationFormatResult formatted = formatter.FormatConversation(new ConversationRequest
        {
            FormatterMode = ConversationFormatterMode.ModelDefault,
            Messages = [new ConversationMessage { Role = "user", Content = userMessage }],
            AddGenerationPrompt = true,
        });
        return formatted.GetTokenIds();
    }

    // The finish reason explains why generation stopped. Reporting it
    // distinguishes a model that ended its own turn from one that ran out of room.
    private static string DescribeFinishReason(TextGenerationFinishReason reason)
    {
        return reason switch
        {
            TextGenerationFinishReason.EosToken => "end-of-sequence token",
            TextGenerationFinishReason.StopToken => "stop token",
            TextGenerationFinishReason.MaxTokens => "reached max-new-tokens",
            TextGenerationFinishReason.SequenceCapacity => "reached the model's sequence capacity",
            TextGenerationFinishReason.Canceled => "canceled",
            _ => "error",
        };
    }

    // Prints the HRESULT of a failed sample and what usually causes it.
    private static void ReportFailure(string operation, Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {operation} failed: 0x{ex.HResult:X8}");
        Console.Error.WriteLine($"       {ex.Message}");
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

internal sealed record Options(
    string ModelPath,
    string Prompt,
    uint MaxNewTokens,
    bool Llama,
    ulong Capacity,
    string? ProgramCache,
    bool CacheWeights,
    string? DraftModelPath,
    bool UseGpu)
{
    public const string DefaultChatPrompt = "Write one sentence about the ocean.";
    public const uint DefaultMaxNewTokens = 64;

    // Returns null when help is requested.
    public static Options? Parse(string[] args)
    {
        string? modelPath = null;
        string prompt = DefaultChatPrompt;
        uint maxNewTokens = DefaultMaxNewTokens;
        bool llama = false;
        ulong capacity = 0;
        string? programCache = null;
        bool cacheWeights = false;
        string? draftModelPath = null;
        string? device = null;
        int positionalCount = 0;
        for (int i = 0; i < args.Length; ++i)
        {
            string argument = args[i];
            switch (argument.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    return null;
                case "--llama":
                    llama = true;
                    continue;
                case "--capacity":
                    if (!TryParseCount(Value(args, ref i), out capacity))
                    {
                        throw new ArgumentException("--capacity must be a positive number of tokens.");
                    }

                    continue;
                case "--program-cache":
                    programCache = Path.GetFullPath(Value(args, ref i));
                    continue;
                case "--cache-weights":
                    cacheWeights = true;
                    continue;
                case "--draft-model":
                    draftModelPath = Value(args, ref i);
                    continue;
                case "--device":
                    device = Value(args, ref i).ToLowerInvariant();
                    if (device is not ("cpu" or "gpu"))
                    {
                        throw new ArgumentException($"--device must be cpu or gpu, not '{device}'.");
                    }

                    continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unrecognized argument: {argument}");
            }

            switch (positionalCount++)
            {
                case 0:
                    modelPath = argument;
                    break;
                case 1:
                    prompt = argument;
                    break;
                case 2:
                    if (!TryParseCount(argument, out ulong count) || count > uint.MaxValue)
                    {
                        throw new ArgumentException($"max-new-tokens must be a positive number, not '{argument}'.");
                    }

                    maxNewTokens = (uint)count;
                    break;
                default:
                    throw new ArgumentException($"Unexpected argument: {argument}");
            }
        }

        if (modelPath is null)
        {
            throw new ArgumentException("A checkpoint folder or GGUF file is required.");
        }

        if (llama && (capacity != 0 || programCache is not null || cacheWeights))
        {
            throw new ArgumentException("--capacity, --program-cache and --cache-weights apply to the model provider; omit --llama.");
        }

        if (cacheWeights && programCache is null)
        {
            throw new ArgumentException("--cache-weights requires --program-cache.");
        }

        if (!llama && (draftModelPath is not null || device is not null))
        {
            throw new ArgumentException("--draft-model and --device apply only with --llama.");
        }

        return new Options(modelPath, prompt, maxNewTokens, llama, capacity, programCache, cacheWeights, draftModelPath, device == "gpu");
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: task-model-text-generation-csharp.exe <checkpoint-folder|model.gguf> [prompt] [max-new-tokens]");
        Console.WriteLine("           [--capacity <tokens>] [--program-cache <directory> [--cache-weights]]");
        Console.WriteLine("       task-model-text-generation-csharp.exe --llama <model.gguf> [prompt] [max-new-tokens]");
        Console.WriteLine("           [--draft-model <draft.gguf>] [--device cpu|gpu]");
        Console.WriteLine($"  The prompt defaults to \"{DefaultChatPrompt}\" and max-new-tokens to {DefaultMaxNewTokens}.");
        Console.WriteLine("  Without --llama, the model provider plans the model for the GPU and creates its tokenizer");
        Console.WriteLine("  from its files. --capacity sets the largest sequence a session holds; --program-cache keeps");
        Console.WriteLine("  the programs that sessions compile, so later runs start faster; --cache-weights also keeps");
        Console.WriteLine("  the weights prepared for each program.");
        Console.WriteLine("  --llama loads a GGUF file with the llama.cpp model loader instead, on the CPU unless");
        Console.WriteLine("  --device gpu. --draft-model adds speculative decoding with a smaller GGUF model that shares");
        Console.WriteLine("  the model's tokenizer.");
        Console.WriteLine("  The prompt is formatted with the model's chat template. Ctrl+C cancels generation.");
    }

    private static string Value(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{args[index]} requires a value.");
        }

        return args[++index];
    }

    private static bool TryParseCount(string value, out ulong count)
    {
        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count != 0;
    }
}
