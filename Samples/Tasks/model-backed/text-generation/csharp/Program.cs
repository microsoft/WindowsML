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
//   --llama, IWinMLRuntime::LoadModelFromPath (a checkpoint folder) or
//   LoadModelFromFile (a GGUF file) loads it, and SetModel binds it on a GPU
//   target: the model provider plans it for the GPU and creates the model's
//   own tokenizer from its files. With --llama, IWinMLLlamaModelLoader loads a
//   GGUF file for the llama.cpp engine, on the CPU unless --device gpu, and a
//   draft model loaded the same way enables speculative decoding. Windows ML
//   never falls back from one route to the other.
//
//   Either way the configuration's tokenizer formats the prompt as one user
//   turn with the model's chat template, GenerateTokens starts generation, and
//   the pull stream yields text fragments as they are produced and then the
//   result: token counts and why generation stopped. Ctrl+C cancels it.
//
// Run it
//   .\run_model_text_generation.ps1 -Language csharp [-Format checkpoint|gguf]
//       [-ModelPath <checkpoint-folder|model.gguf>] [-SequenceCapacity <tokens>]
//       [-ProgramCache <directory> [-CacheWeights]]
//   .\run_llama_text_generation.ps1 -Language csharp [-ModelPath <model.gguf>]
//       [-DraftModel <draft.gguf>] [-Device cpu|gpu]
//
// The Runtime and Task APIs are COM-style C APIs. CsWin32 generates the C#
// bindings when the project builds, from the Win32 metadata in the
// Microsoft.Windows.AI.MachineLearning package; NativeMethods.txt here and in
// ..\..\..\..\Runtime\shared\csharp lists the APIs to generate. Generated
// interface methods throw an exception for a failure HRESULT and return their
// [out, retval] value.

using System.Globalization;
using System.Text;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;
using WinMLSamples;
using Win32 = Windows.Win32.PInvoke;

namespace ModelTextGeneration;

internal static unsafe class Program
{
    private const int ENoInterface = unchecked((int)0x80004002);

    // HRESULT_FROM_WIN32 values of the Win32 errors the samples explain.
    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorPathNotFound = unchecked((int)0x80070003);
    private const int ErrorNotSupported = unchecked((int)0x80070032);
    private const int ErrorModNotFound = unchecked((int)0x8007007E);
    private const int ErrorNotFound = unchecked((int)0x80070490);

    private static int Main(string[] args)
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
            return options.Llama ? RunLlama(options) : RunModelProvider(options);
        }
        catch (Exception ex)
        {
            ReportFailure(options.Llama ? "llama.cpp text generation" : "Text generation", ex);
            return 1;
        }
    }

    // Bring your own weights: the model provider serves the model on the GPU.
    private static int RunModelProvider(Options options)
    {
        using ComObjects objects = new();
        IWinMLRuntime* runtime = CreateRuntime(objects);
        IWinMLExecutionTarget* target = CreateGpuTarget(runtime, objects);
        if (target == null)
        {
            Console.Error.WriteLine(
                "No GPU execution target was found (HRESULT_FROM_WIN32(ERROR_NOT_FOUND)). The model provider " +
                "plans the model for the GPU. On a device without a GPU, run a GGUF file on the CPU with --llama.");
            return 1;
        }

        // A checkpoint folder (config.json, safetensors weights, tokenizer.json
        // and tokenizer_config.json) loads with LoadModelFromPath; a single GGUF
        // file with LoadModelFromFile.
        IWinMLModel* model = Directory.Exists(options.ModelPath)
            ? objects.Add(runtime->LoadModelFromPath(options.ModelPath, null))
            : objects.Add(runtime->LoadModelFromFile(options.ModelPath, null));

        TextGenerationObjects text = CreateTextGeneration(runtime, objects);

        // Sessions compile programs for the GPU. A program cache directory keeps
        // them, so later runs start faster. With --cache-weights it also keeps the
        // weights prepared for each program, so later runs read them instead of
        // preparing them again, at the cost of about one copy of the prepared
        // weights per program on disk.
        fixed (char* programCache = options.ProgramCache)
        {
            WINML_TEXT_GENERATION_MODEL_OPTIONS modelOptions = default;
            if (programCache != null)
            {
                modelOptions.presentFields =
                    WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY;
                modelOptions.compiledProgramDirectory = new PCWSTR(programCache);
                if (options.CacheWeights)
                {
                    modelOptions.presentFields |=
                        WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE;
                    modelOptions.compiledProgramStorage =
                        WINML_COMPILED_PROGRAM_STORAGE.WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS_AND_WEIGHTS;
                }
            }

            // No SetTokenizer: SetModel creates the model's own tokenizer from its files.
            try
            {
                text.ModelConfiguration->SetModel(target, model, &modelOptions);
            }
            catch (Exception ex) when (ex.HResult == ErrorNotSupported)
            {
                Console.Error.WriteLine(
                    "Windows ML cannot run this model for text generation (HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)).");
                Console.Error.WriteLine(
                    "Use a supported checkpoint, such as Qwen2.5-0.5B-Instruct as a safetensors folder or GGUF file. " +
                    "To run a GGUF file that Windows ML does not support on llama.cpp instead, load it with the " +
                    "llama.cpp loader; run this sample with --llama.");
                return 1;
            }
        }

        if (options.Capacity != 0)
        {
            text.Configuration->SetLogicalCapacity(options.Capacity);
        }

        IWinMLTextGenerationSession* session = CreateSession(text, objects);
        GenerateAndClose(text, session, options, objects);
        return 0;
    }

    // Explicit llama.cpp: an engine-specific opt-in for text generation. Nothing
    // selects llama.cpp unless the app loads the model this way.
    private static int RunLlama(Options options)
    {
        using ComObjects objects = new();
        IWinMLRuntime* runtime = CreateRuntime(objects);

        // The loader is available when the Windows ML package includes the
        // llama.cpp engine.
        HRESULT queried = runtime->QueryInterface(out IWinMLLlamaModelLoader* loader);
        if (queried.Value == ENoInterface)
        {
            Console.Error.WriteLine(
                "The llama.cpp model loader is not available (E_NOINTERFACE). " +
                "Use a Windows ML package that includes the llama.cpp engine.");
            return 1;
        }

        queried.ThrowOnFailure();
        objects.Add(loader);

        // The packaged llama.cpp backend runs on the CPU. A GPU target needs a
        // GPU backend that you deploy yourself.
        IWinMLExecutionTarget* target;
        if (options.UseGpu)
        {
            target = CreateGpuTarget(runtime, objects);
            if (target == null)
            {
                Console.Error.WriteLine(
                    "No GPU execution target was found (HRESULT_FROM_WIN32(ERROR_NOT_FOUND)). Use --device cpu.");
                return 1;
            }
        }
        else
        {
            target = objects.Add(runtime->CreateCpuExecutionTarget());
        }

        IWinMLModel* model = objects.Add(loader->LoadModelFromFile(options.ModelPath, null));

        // A draft model must be loaded through the same loader and share the
        // model's tokenizer.
        IWinMLModel* draftModel = null;
        WINML_TEXT_GENERATION_MODEL_OPTIONS modelOptions = default;
        if (options.DraftModelPath is not null)
        {
            draftModel = objects.Add(loader->LoadModelFromFile(options.DraftModelPath, null));
            modelOptions.presentFields =
                WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_DRAFT_MODEL;
            modelOptions.draftModel = draftModel;
        }

        TextGenerationObjects text = CreateTextGeneration(runtime, objects);

        // SetModel reads the tokenizer from the GGUF file. Each session loads the weights.
        text.ModelConfiguration->SetModel(target, model, &modelOptions);

        if (draftModel != null)
        {
            IWinMLTextGenerationSpeculativeConfiguration* speculative =
                objects.QueryInterface<IWinMLTextGenerationSpeculativeConfiguration>(text.Configuration);
            WINML_TEXT_GENERATION_SPECULATIVE_OPTIONS speculativeOptions = new()
            {
                method = WINML_TEXT_GENERATION_SPECULATIVE_METHOD.WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL,
            };
            speculative->SetSpeculativeOptions(&speculativeOptions);
        }

        IWinMLTextGenerationSession* session;
        try
        {
            session = CreateSession(text, objects);
        }
        catch (Exception ex) when (options.UseGpu && ex.HResult == ErrorNotSupported)
        {
            Console.Error.WriteLine(
                "No deployed llama.cpp backend runs on this GPU (HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). " +
                "Use --device cpu, or deploy a llama.cpp GPU backend with the app.");
            return 1;
        }
        catch (Exception ex) when (ex.HResult is ErrorFileNotFound or ErrorModNotFound)
        {
            // Loading and binding succeed without the llama.cpp payload; the
            // first session loads it.
            Console.Error.WriteLine(
                $"This sample requires the llama.cpp payload, which was not found (0x{ex.HResult:X8}). Deploy it " +
                "beside the app by referencing Microsoft.Windows.AI.MachineLearning.LibLlama.Core.");
            return 1;
        }

        TextGenerationResult result = GenerateAndClose(text, session, options, objects);
        if (draftModel != null)
        {
            WINML_TEXT_GENERATION_SPECULATIVE_STATISTICS statistics = result.Speculative;
            Console.WriteLine(
                $"Speculative decoding: steps={statistics.verificationStepCount} " +
                $"drafted={statistics.draftTokenCount} accepted={statistics.acceptedDraftTokenCount}");
        }

        return 0;
    }

    private static IWinMLRuntime* CreateRuntime(ComObjects objects)
    {
        // Factory functions return an HRESULT; interface methods throw on failure.
        IWinMLRuntime* runtime;
        Guid runtimeIid = typeof(IWinMLRuntime).GUID;
        PInvoke.WinMLCreateRuntime(&runtimeIid, (void**)&runtime).ThrowOnFailure();
        return objects.Add(runtime);
    }

    // Returns null when the device has no GPU target.
    private static IWinMLExecutionTarget* CreateGpuTarget(IWinMLRuntime* runtime, ComObjects objects)
    {
        try
        {
            return objects.Add(runtime->CreateExecutionTarget(
                WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_GPU,
                WINML_EXECUTION_TARGET_PREFERENCE.WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE));
        }
        catch (Exception ex) when (ex.HResult == ErrorNotFound)
        {
            return null;
        }
    }

    // The Task API object comes from the Runtime. Its text generation factory
    // creates the configuration, whose model-backed interface binds a loaded
    // model in place of a pipeline.
    private static TextGenerationObjects CreateTextGeneration(IWinMLRuntime* runtime, ComObjects objects)
    {
        IWinMLTasks* tasks;
        Guid tasksIid = typeof(IWinMLTasks).GUID;
        PInvoke.WinMLCreateTasks(runtime, &tasksIid, (void**)&tasks).ThrowOnFailure();
        objects.Add(tasks);

        IWinMLTextGenerationTaskFactory* factory = objects.QueryInterface<IWinMLTextGenerationTaskFactory>(tasks);
        IWinMLTextGenerationConfiguration* configuration = objects.Add(factory->CreateTextGenerationConfiguration());
        IWinMLModelTextGenerationConfiguration* modelConfiguration =
            objects.QueryInterface<IWinMLModelTextGenerationConfiguration>(configuration);
        return new TextGenerationObjects(tasks, factory, configuration, modelConfiguration);
    }

    // The task must use the configuration's tokenizer: the one SetModel created.
    private static IWinMLTextGenerationSession* CreateSession(TextGenerationObjects text, ComObjects objects)
    {
        text.Tokenizer = objects.Add(text.Configuration->GetTokenizer());
        IWinMLTextGenerationTask* task = objects.Add(text.Factory->CreateTextGenerationTask(
            text.Tokenizer,
            WINML_TEXT_GENERATION_EOS_POLICY.WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT,
            0,
            null));
        return objects.Add(task->CreateSession(text.Configuration));
    }

    // A session holds execution resources until Close.
    private static TextGenerationResult GenerateAndClose(
        TextGenerationObjects text,
        IWinMLTextGenerationSession* session,
        Options options,
        ComObjects objects)
    {
        TextGenerationResult result;
        try
        {
            result = StreamChatReply(text, session, options.Prompt, options.MaxNewTokens, objects);
        }
        catch
        {
            CloseQuietly(() => session->Close());
            throw;
        }

        session->Close();
        return result;
    }

    // Formats the prompt as one user turn with the model's chat template, so an
    // instruct model ends its reply on its own, then streams the reply and
    // prints the token counts and why generation stopped.
    private static TextGenerationResult StreamChatReply(
        TextGenerationObjects text,
        IWinMLTextGenerationSession* session,
        string prompt,
        uint maxNewTokens,
        ComObjects objects)
    {
        WINML_TEXT_GENERATION_OPTIONS values = new()
        {
            presentFields = WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_MAX_NEW_TOKENS,
            maxNewTokens = maxNewTokens,
        };
        IWinMLTextGenerationOptions* options = objects.Add(text.Factory->CreateTextGenerationOptions(&values, 0, null));
        IWinMLCancellationSource* cancellation = objects.Add(text.Tasks->CreateCancellationSource());

        uint[] promptTokens = EncodeChatPrompt(text.Tokenizer, prompt, objects);
        Console.Write($"Prompt: {prompt}\nReply: ");

        // Ctrl+C cancels generation through the cancellation source, from the
        // thread that handles the console event. The gate keeps the handler
        // from using the source after this method stops listening.
        object gate = new();
        bool listening = true;
        IntPtr cancellationHandle = (IntPtr)cancellation;
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            lock (gate)
            {
                if (listening)
                {
                    ((IWinMLCancellationSource*)cancellationHandle)->Cancel();
                }
            }
        };

        Console.CancelKeyPress += cancel;
        TextGenerationResult result;
        try
        {
            IWinMLTextGenerationPullStream* stream;
            fixed (uint* tokens = promptTokens)
            {
                stream = objects.Add(session->GenerateTokens((uint)promptTokens.Length, tokens, options, cancellation));
            }

            // A pull stream holds execution resources until Close.
            try
            {
                result = ReadStream(stream, objects);
            }
            finally
            {
                CloseQuietly(() => stream->Close());
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            lock (gate)
            {
                listening = false;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Tokens: prompt={result.PromptTokenCount} generated={result.GeneratedTokenCount}");
        Console.WriteLine($"Finished because: {DescribeFinishReason(result.FinishReason)}");
        return result;
    }

    // An instruct model ends its reply with an end-of-sequence token when the
    // prompt uses the chat format it was trained on. The structured
    // conversation formatter renders the model's own chat template for one user
    // turn and returns the prompt token IDs.
    private static uint[] EncodeChatPrompt(IWinMLTokenizer* tokenizer, string userMessage, ComObjects objects)
    {
        IWinMLStructuredConversationFormatter* formatter =
            objects.QueryInterface<IWinMLStructuredConversationFormatter>(tokenizer);

        IWinMLConversationFormatResult* formatted;
        fixed (char* role = "user")
        fixed (char* content = userMessage)
        {
            WINML_CONVERSATION_MESSAGE message = new() { role = new PCWSTR(role), content = new PCWSTR(content) };
            WINML_CONVERSATION_REQUEST request = new()
            {
                formatterMode = WINML_CONVERSATION_FORMATTER_MODE.WINML_CONVERSATION_FORMATTER_MODE_MODEL_DEFAULT,
                messageCount = 1,
                messages = &message,
                addGenerationPrompt = true,
            };
            formatted = objects.Add(formatter->FormatConversation(&request));
        }

        uint tokenCount;
        uint* tokenIds;
        formatted->GetTokenIds(&tokenCount, &tokenIds);
        try
        {
            return new ReadOnlySpan<uint>(tokenIds, checked((int)tokenCount)).ToArray();
        }
        finally
        {
            Win32.CoTaskMemFree(tokenIds);
        }
    }

    // Drains the pull stream, printing each fragment as it arrives, then reads
    // the terminal result. The streamed fragments and the final text must agree.
    private static TextGenerationResult ReadStream(IWinMLTextGenerationPullStream* stream, ComObjects objects)
    {
        StringBuilder streamed = new();
        for (;;)
        {
            WINML_TEXT_GENERATION_READ_STATUS status;
            uint tokenId;
            PCWSTR fragment;
            stream->ReadNext(&status, &tokenId, &fragment);
            if (status == WINML_TEXT_GENERATION_READ_STATUS.WINML_TEXT_GENERATION_READ_STATUS_UPDATE)
            {
                if (fragment.Value == null)
                {
                    throw new InvalidDataException("The stream returned an update without text.");
                }

                string piece = new(fragment.Value);
                streamed.Append(piece);
                Console.Write(piece);
                continue;
            }

            if (status != WINML_TEXT_GENERATION_READ_STATUS.WINML_TEXT_GENERATION_READ_STATUS_COMPLETED)
            {
                throw new InvalidDataException($"Unexpected read status {status}.");
            }

            break;
        }

        IWinMLTextGenerationResult* result = objects.Add(stream->GetResult());
        WINML_TEXT_GENERATION_FINISH_REASON finishReason = result->GetFinishReason();

        // A canceled generation reports HRESULT_FROM_WIN32(ERROR_CANCELLED);
        // any other failure is the error that ended generation.
        HRESULT error = result->GetErrorCode();
        if (finishReason != WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_CANCELED)
        {
            error.ThrowOnFailure();
        }

        PCWSTR finalText;
        result->GetText(&finalText);
        if (finalText.Value == null || new string(finalText.Value) != streamed.ToString())
        {
            throw new InvalidDataException("The result text does not match the streamed fragments.");
        }

        uint generatedTokenCount;
        uint* generatedTokenIds;
        result->GetTokenIds(&generatedTokenCount, &generatedTokenIds);
        Win32.CoTaskMemFree(generatedTokenIds);

        // Results of a speculative configuration also report how many draft
        // tokens the model accepted.
        WINML_TEXT_GENERATION_SPECULATIVE_STATISTICS speculative = default;
        IWinMLTextGenerationSpeculativeResult* speculativeResult =
            objects.TryQueryInterface<IWinMLTextGenerationSpeculativeResult>(result);
        if (speculativeResult != null)
        {
            speculative = speculativeResult->GetSpeculativeStatistics();
        }

        return new TextGenerationResult(result->GetPromptTokenCount(), generatedTokenCount, finishReason, speculative);
    }

    // The finish reason explains why generation stopped. Reporting it
    // distinguishes a model that ended its own turn from one that ran out of room.
    private static string DescribeFinishReason(WINML_TEXT_GENERATION_FINISH_REASON reason)
    {
        return reason switch
        {
            WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_EOS_TOKEN => "end-of-sequence token",
            WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_STOP_TOKEN => "stop token",
            WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_MAX_TOKENS => "reached max-new-tokens",
            WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_SEQUENCE_CAPACITY => "reached the model's sequence capacity",
            WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_CANCELED => "canceled",
            _ => "error",
        };
    }

    // Close failures while another failure is already propagating are not
    // reported, as the C++ samples' StreamCloser does not report them.
    private static void CloseQuietly(Action close)
    {
        try
        {
            close();
        }
        catch (Exception)
        {
        }
    }

    // Prints the HRESULT of a failed sample and what usually causes it.
    private static void ReportFailure(string operation, Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {operation} failed: 0x{ex.HResult:X8}");
        Console.Error.WriteLine($"       {ex.Message}");
        switch (ex.HResult)
        {
            case ENoInterface:
                Console.Error.WriteLine(
                    "The Windows ML package deployed with this app does not provide this API. Build with a package " +
                    "that includes model-backed Tasks; see the model-backed README.");
                break;
            case ErrorNotSupported:
                Console.Error.WriteLine(
                    "Windows ML does not support this model, option or execution target for this Task.");
                break;
            case ErrorFileNotFound:
            case ErrorPathNotFound:
                Console.Error.WriteLine("A model or input file was not found. Check the paths.");
                break;
        }
    }
}

// The Task objects that every route uses. The tokenizer is the configuration's,
// read after SetModel creates it.
internal sealed unsafe class TextGenerationObjects(
    IWinMLTasks* tasks,
    IWinMLTextGenerationTaskFactory* factory,
    IWinMLTextGenerationConfiguration* configuration,
    IWinMLModelTextGenerationConfiguration* modelConfiguration)
{
    public IWinMLTasks* Tasks { get; } = tasks;

    public IWinMLTextGenerationTaskFactory* Factory { get; } = factory;

    public IWinMLTextGenerationConfiguration* Configuration { get; } = configuration;

    public IWinMLModelTextGenerationConfiguration* ModelConfiguration { get; } = modelConfiguration;

    public IWinMLTokenizer* Tokenizer { get; set; }
}

internal readonly record struct TextGenerationResult(
    uint PromptTokenCount,
    uint GeneratedTokenCount,
    WINML_TEXT_GENERATION_FINISH_REASON FinishReason,
    WINML_TEXT_GENERATION_SPECULATIVE_STATISTICS Speculative);

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
