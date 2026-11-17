// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Text generation features with the Windows ML Task API, from C#.
//
// The optional text generation interfaces, one scenario each, on a GGUF file
// that the llama.cpp model loader loads for the CPU:
//
//   continuation  Capabilities and the retained sequence after a reply, then
//                 ContinueTokens with only the new tokens of the next turn.
//   constraint    JSON schema output: a token constraint from the formatted
//                 conversation, constrained continuation, and the same request
//                 through a chat completion session.
//   speculative   Speculative method support, then prompt lookup and draft
//                 model decoding with their statistics.
//   watermark     A keyed watermark in sampled tokens, its statistics, and a
//                 detector that finds it with the key and not without it.
//   segments      A prompt passed as token segments.
//
// A scenario that the model or route does not support says so and the sample
// continues. Generation is greedy except in the watermark scenario, which
// samples with a fixed seed, so every run prints the same output. Ctrl+C
// cancels generation through a CancellationToken.
//
// Run it
//   .\run_text_generation_features.ps1 [-Scenario all|continuation|constraint|
//       speculative|watermark|segments] [-ModelPath <model.gguf>]
//
// The sample uses the sample wrapper in ..\..\..\..\Runtime\shared\csharp\Runtime,
// an idiomatic C# layer over the Runtime and Task APIs. A text generation
// session belongs to the thread that created it; TextGenerator keeps it on a
// thread of its own, streams from it, and runs code on that thread with
// InvokeAsync. The wrapper is sample code; see
// ..\..\..\..\Runtime\shared\csharp\README.md.

using System.Globalization;
using System.Text;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace TextGenerationFeatures;

internal static class Program
{
    private static readonly string[] ScenarioNames = ["continuation", "constraint", "speculative", "watermark", "segments"];

    // The JSON object that the constraint scenario asks for.
    private const string PlanetSchema =
        """{"type":"object","properties":{"name":{"type":"string"},"moons":{"type":"integer"}},"required":["name","moons"],"additionalProperties":false}""";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "-h" or "--help" or "/?")
        {
            PrintUsage();
            return 0;
        }

        if (args.Length is 0 or > 2)
        {
            Console.Error.WriteLine(args.Length == 0 ? "ERROR: A GGUF file is required." : $"ERROR: Unexpected argument: {args[2]}");
            PrintUsage();
            return 2;
        }

        string scenario = args.Length == 2 ? args[1].ToLowerInvariant() : "all";
        if (scenario != "all" && !ScenarioNames.Contains(scenario))
        {
            Console.Error.WriteLine($"ERROR: Unknown scenario: {args[1]}");
            PrintUsage();
            return 2;
        }

        // Model replies are not limited to the console's code page, and numbers
        // print the same everywhere.
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // Ctrl+C cancels generation. The source is not disposed: the console
        // handler can still be running on its own thread when Main returns.
        CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        Context context;
        try
        {
            context = Context.Create(args[0], cancellation.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Loading the model failed: 0x{ex.HResult:X8}");
            Console.Error.WriteLine($"       {ex.Message}");
            return 1;
        }

        using (context)
        {
            int exitCode = 0;
            foreach (string name in scenario == "all" ? ScenarioNames : [scenario])
            {
                Console.WriteLine($"== {name} ==");
                try
                {
                    await RunScenarioAsync(name, context);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    Console.WriteLine("Canceled; the remaining scenarios are skipped.");
                    return exitCode;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ERROR: {name} failed: 0x{ex.HResult:X8}");
                    Console.Error.WriteLine($"       {ex.Message}");
                    exitCode = 1;
                }

                Console.WriteLine();
            }

            return exitCode;
        }
    }

    private static Task RunScenarioAsync(string name, Context context)
    {
        return name switch
        {
            "continuation" => RunContinuationAsync(context),
            "constraint" => RunConstraintAsync(context),
            "speculative" => RunSpeculativeAsync(context),
            "watermark" => RunWatermarkAsync(context),
            _ => RunSegmentsAsync(context),
        };
    }

    // Reads what a session retains after a reply, and continues it with only
    // the tokens that the next turn adds.
    private static async Task RunContinuationAsync(Context context)
    {
        // Task objects report the Runtime they were created with.
        using (WinMLRuntime runtime = context.Tasks.GetRuntime())
        {
            Console.WriteLine($"WinMLTasks.GetRuntime: {DescribeRuntime(context, runtime)}");
        }

        using TextGenerationConfiguration configuration = context.CreateConfiguration();
        using (TaskRuntimeIdentity identity = configuration.As<TaskRuntimeIdentity>())
        using (WinMLRuntime runtime = identity.GetRuntime())
        {
            Console.WriteLine($"TaskRuntimeIdentity.GetRuntime: {DescribeRuntime(context, runtime)}");
        }

        await using ModelSession model = await ModelSession.CreateAsync(context, configuration);
        Console.WriteLine($"Default end-of-sequence tokens: {Join(model.Task.GetDefaultEosTokenIds())}");

        using TextGenerationOptions options = context.Tasks.TextGeneration.CreateTextGenerationOptions(
            new TextGenerationOptionValues { MaxNewTokens = 32 });
        TextGenerationOptionValues values = options.GetValues();
        Console.WriteLine(
            $"Options: maxNewTokens={values.MaxNewTokens} temperature={values.Temperature} topK={values.TopK} " +
            $"topP={values.TopP} repetitionPenalty={values.RepetitionPenalty} seed={values.Seed} " +
            $"watermark={values.WatermarkMode} stopTokens=[{Join(values.StopTokenIds ?? [])}]");

        // The session's capabilities, state, Runtime, and the configuration it
        // consumed, read on the session's thread. After CreateSession the
        // configuration reports the objects the llama.cpp route composed.
        TextGenerationCapabilities capabilities = await model.Generator.InvokeAsync(session =>
        {
            using WinMLRuntime runtime = session.GetRuntime();
            using TextGenerationConfiguration consumed = session.GetConfiguration();
            using Pipeline pipeline = consumed.GetPipeline(TextGenerationPipelinePhase.Unified);
            using Stage tokenStage = consumed.GetTokenInput(TextGenerationPipelinePhase.Unified, out uint tokenInput);
            using Stage outputStage = consumed.GetOutput(TextGenerationPipelinePhase.Unified, out uint output, out TextGenerationOutputKind outputKind);
            using Stage stateOwner = consumed.GetStateOwnerStage();
            Console.WriteLine($"Session.GetRuntime: {DescribeRuntime(context, runtime)}");
            Console.WriteLine(
                $"Session.GetConfiguration: mode={consumed.Mode} pipeline={Bound(pipeline)} tokenInput={tokenInput} " +
                $"output={output} ({outputKind}) stateOwner={Bound(stateOwner)}");
            Console.WriteLine($"Session state: {session.State}");
            return session.Capabilities;
        });
        Console.WriteLine(
            $"Capabilities: continuation={capabilities.SupportsContinuation} constraints={capabilities.SupportsConstraints} " +
            $"constrainedContinuation={capabilities.SupportsConstrainedContinuation} " +
            $"segmentedPrompts={capabilities.SupportsSegmentedPrompts} embeddingSegments={capabilities.SupportsEmbeddingSegments} " +
            $"profile={capabilities.ExecutionProfile} output={capabilities.OutputKind}");

        ConversationMessage firstQuestion = User("Name the three primary colors, separated by commas.");
        uint[] firstPrompt = Format(model.Tokenizer, [firstQuestion]);
        TextGenerationResult first = await StreamAsync(
            model.Generator, model.Generator.GenerateTokensAsync(firstPrompt, options, context.Cancellation), "Reply 1: ");
        string firstReply = first.Text;
        Console.WriteLine($"Tokens: prompt={first.PromptTokenCount} generated={first.GetTokenIds().Length}");

        // Compare token IDs, not text, to decide whether the next prompt
        // extends what the session holds.
        uint[] retained = await model.Generator.InvokeAsync(session => ReadSequence(session, "After reply 1"));

        uint[] secondPrompt = Format(model.Tokenizer, [firstQuestion, Assistant(firstReply), User("Now name the three secondary colors.")]);
        IAsyncEnumerable<TextGenerationUpdate> updates;
        if (capabilities.SupportsContinuation && retained.Length > 0 && StartsWith(secondPrompt, retained))
        {
            uint[] suffix = secondPrompt[retained.Length..];
            Console.WriteLine(
                $"The {retained.Length} retained tokens start the {secondPrompt.Length}-token prompt; ContinueTokens appends {suffix.Length}.");
            updates = model.Generator.ContinueTokensAsync(suffix, options, context.Cancellation);
        }
        else
        {
            Console.WriteLine(capabilities.SupportsContinuation
                ? "The retained tokens do not start the next prompt; GenerateTokens replaces the sequence."
                : "Continuation is not supported by this model/route; GenerateTokens replaces the sequence.");
            updates = model.Generator.GenerateTokensAsync(secondPrompt, options, context.Cancellation);
        }

        TextGenerationResult second = await StreamAsync(model.Generator, updates, "Reply 2: ");
        Console.WriteLine($"Tokens: prompt={second.PromptTokenCount} generated={second.GetTokenIds().Length}");
        await model.Generator.InvokeAsync(session => ReadSequence(session, "After reply 2"));

        // Reset discards the retained sequence.
        await model.Generator.InvokeAsync(session =>
        {
            session.Reset();
            return ReadSequence(session, "After Reset");
        });
    }

    // Generates JSON that matches a schema: directly under a token constraint,
    // through a chat completion session, and as a constrained continuation of
    // the conversation. A chat template without JSON response formats gets the
    // schema as a required tool call instead.
    private static async Task RunConstraintAsync(Context context)
    {
        using TextGenerationConfiguration configuration = context.CreateConfiguration();
        await using ModelSession model = await ModelSession.CreateAsync(context, configuration);
        TextGenerationCapabilities capabilities = await model.Generator.InvokeAsync(session => session.Capabilities);
        if (!capabilities.SupportsConstraints)
        {
            Console.WriteLine("Constrained generation is not supported by this model/route (Capabilities.SupportsConstraints is false).");
            return;
        }

        using TextGenerationOptions options = context.Tasks.TextGeneration.CreateTextGenerationOptions(96);
        using StructuredConversationFormatter formatter = model.Tokenizer.As<StructuredConversationFormatter>();
        using TokenizerConstraintFactory constraints = model.Tokenizer.As<TokenizerConstraintFactory>();

        // The formatted conversation describes the schema; the tokenizer turns
        // it into a constraint. A constraint belongs to the thread that creates
        // it, so the session's thread creates and reads it.
        ConversationMessage question = User("Describe the planet Mars: its name and its number of moons.");
        bool asTool = !SupportsJsonSchema(formatter, question);
        using (ConversationFormatResult formatted = formatter.FormatConversation(PlanetRequest([question], asTool)))
        {
            uint[] prompt = formatted.GetTokenIds();
            using TokenConstraint constraint = await model.Generator.InvokeAsync(_ => constraints.CreateConstraint(formatted));
            await StreamAsync(
                model.Generator,
                model.Generator.GenerateAsync(
                    (session, o, cancellation) =>
                    {
                        using TextGenerationConstraintSession constrained = session.As<TextGenerationConstraintSession>();
                        return constrained.GenerateTokensWithConstraint(prompt, constraint, o, cancellation);
                    },
                    options,
                    context.Cancellation),
                "GenerateTokensWithConstraint: ");
            Console.WriteLine($"Constraint: {await model.Generator.InvokeAsync(_ => constraint.State)}");
        }

        // A chat completion session over the same text generation session
        // formats the request, creates the constraint, and parses the reply
        // into events, each traceable to the tokens it came from.
        Console.WriteLine("Chat completion:");
        using ChatCompletionTask chatTask = context.Tasks.ChatCompletion.CreateChatCompletionTask(model.Tokenizer);
        using CancellationSource chatCancellation = context.Tasks.CreateCancellationSource();
        ConversationMessage reply;
        await using (context.Cancellation.Register(chatCancellation.Cancel))
        {
            reply = await model.Generator.InvokeAsync(
                session => RunChatCompletion(chatTask, session, PlanetRequest([question], asTool), options, chatCancellation));
        }

        // Continue the retained sequence with the next turn, under the
        // constraint that the next turn's formatted conversation describes.
        if (!capabilities.SupportsConstrainedContinuation)
        {
            Console.WriteLine("Constrained continuation is not supported by this model/route.");
            return;
        }

        if (asTool && (reply.ToolCalls is null || reply.ToolCalls.Count == 0))
        {
            Console.WriteLine("The model made no tool call; constrained continuation is skipped.");
            return;
        }

        IReadOnlyList<ConversationMessage> conversation = asTool
            ? [question, reply, new ConversationMessage { Role = "tool", ToolCallId = reply.ToolCalls![0].Id, ToolName = "report_planet", Content = "Recorded." }, User("Now do the same for Jupiter.")]
            : [question, reply, User("Now do the same for Jupiter.")];
        using ConversationFormatResult next = formatter.FormatConversation(PlanetRequest(conversation, asTool));
        uint[] nextPrompt = next.GetTokenIds();
        uint[] retained = await model.Generator.InvokeAsync(session => ReadSequence(session, "After the chat completion"));
        if (retained.Length == 0 || !StartsWith(nextPrompt, retained))
        {
            Console.WriteLine("The retained tokens do not start the next prompt; constrained continuation is skipped.");
            return;
        }

        uint[] suffix = nextPrompt[retained.Length..];
        Console.WriteLine($"The {retained.Length} retained tokens start the {nextPrompt.Length}-token prompt; ContinueTokensWithConstraint appends {suffix.Length}.");
        using TokenConstraint nextConstraint = await model.Generator.InvokeAsync(_ => constraints.CreateConstraint(next));
        await StreamAsync(
            model.Generator,
            model.Generator.GenerateAsync(
                (session, o, cancellation) =>
                {
                    using TextGenerationConstraintContinuation continuation = session.As<TextGenerationConstraintContinuation>();
                    return continuation.ContinueTokensWithConstraint(suffix, nextConstraint, o, cancellation);
                },
                options,
                context.Cancellation),
            "ContinueTokensWithConstraint: ");
        Console.WriteLine($"Constraint: {await model.Generator.InvokeAsync(_ => nextConstraint.State)}");
    }

    // Runs on the session's thread: the chat session and its stream belong to
    // it. Returns the assistant's reply as a conversation message.
    private static ConversationMessage RunChatCompletion(
        ChatCompletionTask chatTask,
        TextGenerationSession session,
        ConversationRequest request,
        TextGenerationOptions options,
        CancellationSource cancellation)
    {
        using ChatCompletionSession chat = chatTask.CreateSession(session);
        using ChatCompletionPullStream stream = chat.Generate(request, options, cancellation);
        using (ConversationFormatResult formatted = stream.GetFormatResult())
        using (ConversationOutputParser parser = stream.GetOutputParser())
        {
            Console.WriteLine($"  Formatted prompt: {formatted.GetTokenIds().Length} tokens, with an output parser");
        }

        bool constrained = stream.TryGetConstraint(out TokenConstraint? constraint);
        using (constraint)
        {
            Console.WriteLine($"  Constraint: {(constrained ? "present" : "none")}");
            while (stream.TryReadNext(out ConversationOutputEvent? outputEvent))
            {
                using (outputEvent)
                using (ChatCompletionEventProvenance provenance = outputEvent.As<ChatCompletionEventProvenance>())
                {
                    (uint firstToken, uint tokenCount) = provenance.SourceTokenRange;
                    string text = outputEvent.Type switch
                    {
                        ConversationOutputEventType.ContentDelta or ConversationOutputEventType.ToolArgumentsDelta => Quote(outputEvent.Text),
                        ConversationOutputEventType.ToolCallStart => outputEvent.ToolName,
                        _ => string.Empty,
                    };
                    Console.WriteLine(
                        $"  {outputEvent.Type} tokens [{firstToken}, {firstToken + tokenCount}) " +
                        $"({provenance.GetSourceTokenIds().Length} IDs) {text}");
                }
            }

            using ChatCompletionResult result = stream.GetResult();
            (uint promptTokens, uint completionTokens) = result.GetTokenCounts();
            if (result.FinishReason == ChatCompletionFinishReason.Canceled)
            {
                Console.WriteLine($"  Finished because: canceled after {completionTokens} tokens");
                throw new OperationCanceledException();
            }

            if (result.FinishReason == ChatCompletionFinishReason.Error)
            {
                throw WinMLException.GetExceptionForHResult(result.ErrorCode);
            }

            ChatCompletionPromptCacheUse cache = result.PromptCacheUse;
            ConversationToolCall[] toolCalls = result.GetToolCalls();
            Console.WriteLine($"  Content: {Quote(result.Content)}");
            foreach (ConversationToolCall call in toolCalls)
            {
                Console.WriteLine($"  Tool call: {call.Name}({call.ArgumentsJson})");
            }

            Console.WriteLine(
                $"  Finished because: {result.FinishReason}; tokens: prompt={promptTokens} completion={completionTokens}; " +
                $"prompt cache: {cache.Mode}, {cache.CachedPromptTokenCount} reused, {cache.PrefilledPromptTokenCount} evaluated");
            using (TextGenerationResult generation = result.GetTextGenerationResult())
            {
                Console.WriteLine(
                    $"  Text generation result: {generation.GetTokenIds().Length} tokens, finished because {generation.FinishReason}");
            }

            if (constraint is not null)
            {
                Console.WriteLine($"  Constraint: {constraint.State}");
            }

            // A tool result names the call it answers. The app names calls
            // that the model's output format gives no ID.
            ConversationMessage reply = toolCalls.Length > 0
                ? new ConversationMessage
                {
                    Role = "assistant",
                    ToolCalls = toolCalls.Select((call, i) => call.Id.Length > 0 ? call : call with { Id = $"call_{i}" }).ToArray(),
                }
                : Assistant(result.Content);
            stream.Close();
            chat.Close();
            return reply;
        }
    }
    // Reports what each speculative method does for the model, then decodes
    // with prompt lookup and with a draft model.
    private static async Task RunSpeculativeAsync(Context context)
    {
        // Support is known from the model's metadata after SetModel, without a session.
        using Model draftModel = context.Loader.LoadModelFromFile(context.ModelPath);
        using (TextGenerationConfiguration probe = context.CreateConfiguration())
        using (TextGenerationConfiguration probeWithDraft = context.CreateConfiguration(draftModel))
        using (TextGenerationSpeculativeSupport support = probe.As<TextGenerationSpeculativeSupport>())
        using (TextGenerationSpeculativeSupport supportWithDraft = probeWithDraft.As<TextGenerationSpeculativeSupport>())
        {
            foreach (TextGenerationSpeculativeMethod method in Enum.GetValues<TextGenerationSpeculativeMethod>())
            {
                Console.WriteLine($"{method,-12} without a draft model: {DescribeSupport(support, method)}");
                Console.WriteLine($"{method,-12} with a draft model:    {DescribeSupport(supportWithDraft, method)}");
            }
        }

        const string prompt = "Repeat this sentence exactly: The quick brown fox jumps over the lazy dog while the cat sleeps in the warm sun.";
        using TextGenerationOptions options = context.Tasks.TextGeneration.CreateTextGenerationOptions(48);

        // Prompt lookup drafts tokens that followed an earlier occurrence of
        // the end of the sequence; it needs no other model.
        using (TextGenerationConfiguration configuration = context.CreateConfiguration())
        {
            if (SelectSpeculativeMethod(configuration, TextGenerationSpeculativeMethod.PromptLookup))
            {
                await using ModelSession model = await ModelSession.CreateAsync(context, configuration);
                await GenerateSpeculativelyAsync(context, model, prompt, options);
            }
        }

        // A draft model, here a second copy of the same GGUF file. The session
        // composes it as a separate draft pipeline; its configuration then
        // reports the stages it composed, and the sample wires the same stages,
        // draft pipeline included, into a configuration of its own.
        using TextGenerationConfiguration handWired = context.Tasks.TextGeneration.CreateTextGenerationConfiguration();
        using (TextGenerationConfiguration configuration = context.CreateConfiguration(draftModel))
        {
            if (!SelectSpeculativeMethod(configuration, TextGenerationSpeculativeMethod.DraftModel))
            {
                return;
            }

            await using ModelSession model = await ModelSession.CreateAsync(context, configuration);
            await GenerateSpeculativelyAsync(context, model, prompt, options);
            await model.Generator.InvokeAsync(session => ComposeByHand(session, handWired, model.Tokenizer));
        }

        // The first session is closed, so a session of the hand-wired
        // configuration can run the same pipelines.
        using (TextGenerationSpeculativeConfiguration speculative = handWired.As<TextGenerationSpeculativeConfiguration>())
        {
            speculative.SetSpeculativeOptions(new TextGenerationSpeculativeOptions { Method = TextGenerationSpeculativeMethod.DraftModel, DraftTokenCount = 2 });
            TextGenerationSpeculativeOptions selected = speculative.GetSpeculativeOptions();
            using TextGenerationDraftPipeline draft = speculative.GetDraftPipeline();
            Console.WriteLine(
                $"Hand-wired: GetSpeculativeOptions method={selected.Method} draftTokenCount={selected.DraftTokenCount}; " +
                $"GetDraftPipeline: input {draft.InputIndex}, output {draft.OutputIndex}");
        }

        handWired.Validate();
        await using ModelSession composed = await ModelSession.CreateAsync(context, handWired);
        await GenerateSpeculativelyAsync(context, composed, prompt, options);
    }

    // Selects method when the bound model supports it.
    private static bool SelectSpeculativeMethod(TextGenerationConfiguration configuration, TextGenerationSpeculativeMethod method)
    {
        using (TextGenerationSpeculativeSupport support = configuration.As<TextGenerationSpeculativeSupport>())
        {
            if (!support.GetSpeculativeMethodSupport(method).Supported)
            {
                Console.WriteLine($"{method}: not supported by this model/route.");
                return false;
            }
        }

        using TextGenerationSpeculativeConfiguration speculative = configuration.As<TextGenerationSpeculativeConfiguration>();
        speculative.SetSpeculativeOptions(new TextGenerationSpeculativeOptions { Method = method, DraftTokenCount = 4 });
        TextGenerationSpeculativeOptions selected = speculative.GetSpeculativeOptions();
        Console.WriteLine($"{method}: GetSpeculativeOptions method={selected.Method} draftTokenCount={selected.DraftTokenCount}");
        return true;
    }

    private static async Task GenerateSpeculativelyAsync(Context context, ModelSession model, string prompt, TextGenerationOptions options)
    {
        TextGenerationResult result = await StreamAsync(
            model.Generator,
            model.Generator.GenerateTokensAsync(Format(model.Tokenizer, [User(prompt)]), options, context.Cancellation),
            "Reply: ");
        using TextGenerationSpeculativeResult speculativeResult = result.As<TextGenerationSpeculativeResult>();
        TextGenerationSpeculativeStatistics statistics = speculativeResult.SpeculativeStatistics;
        Console.WriteLine(
            $"Speculative decoding: method={statistics.Method} steps={statistics.VerificationStepCount} " +
            $"drafted={statistics.DraftTokenCount} accepted={statistics.AcceptedDraftTokenCount} " +
            $"generated={result.GetTokenIds().Length}");
    }

    // Wires the stages that a session composed into handWired, as an
    // application that builds its own pipelines does. Runs on the session's thread.
    private static bool ComposeByHand(TextGenerationSession session, TextGenerationConfiguration handWired, Tokenizer tokenizer)
    {
        const TextGenerationPipelinePhase unified = TextGenerationPipelinePhase.Unified;
        using TextGenerationConfiguration consumed = session.GetConfiguration();
        using TextGenerationSpeculativeConfiguration consumedSpeculative = consumed.As<TextGenerationSpeculativeConfiguration>();
        using Pipeline pipeline = consumed.GetPipeline(unified);
        using Stage tokenStage = consumed.GetTokenInput(unified, out uint tokenInput);
        using Stage outputStage = consumed.GetOutput(unified, out uint output, out TextGenerationOutputKind outputKind);
        using Stage stateOwner = consumed.GetStateOwnerStage();
        using ExecutionTarget tokenTarget = consumed.GetTokenTarget();
        using TextGenerationDraftPipeline draft = consumedSpeculative.GetDraftPipeline();

        handWired.Mode = TextGenerationConfigurationMode.Unified;
        handWired.SetPipeline(unified, pipeline);
        handWired.SetTokenInput(unified, tokenStage, tokenInput);
        handWired.SetOutput(unified, outputStage, output, outputKind);
        handWired.SetStateOwnerStage(stateOwner);
        handWired.SetTokenTarget(tokenTarget);
        handWired.SetTokenizer(tokenizer);
        using TextGenerationSpeculativeConfiguration speculative = handWired.As<TextGenerationSpeculativeConfiguration>();
        speculative.SetDraftPipeline(draft);
        Console.WriteLine($"Composed draft pipeline: input {draft.InputIndex}, output {draft.OutputIndex}, wired into a new configuration");
        return true;
    }
    // Watermarks sampled tokens with a key, then detects the watermark with
    // the key and fails to detect it with another key or in an opted-out reply.
    private static async Task RunWatermarkAsync(Context context)
    {
        TextGenerationWatermarkOptions watermark = new()
        {
            Algorithm = TextGenerationWatermarkAlgorithm.Gumbel,
            Key = 0x5EED_1234_ABCD_0042,
            KeyId = 42,
        };

        using TextGenerationConfiguration configuration = context.CreateConfiguration();
        using (TextGenerationWatermarkConfiguration watermarkConfiguration = configuration.As<TextGenerationWatermarkConfiguration>())
        {
            watermarkConfiguration.SetWatermarkOptions(watermark);
            TextGenerationWatermarkOptions configured = watermarkConfiguration.GetWatermarkOptions();
            Console.WriteLine(
                $"GetWatermarkOptions: algorithm={configured.Algorithm} keyId={configured.KeyId} " +
                $"enforcement={configured.Enforcement} contextScope={configured.ContextScope} " +
                $"contextWidth={configured.ContextWidth} key={(configured.Key == 0 ? "not reported" : "reported")}");
        }

        await using ModelSession model = await ModelSession.CreateAsync(context, configuration);
        using TextGenerationWatermarkDetector detector = context.Tasks.TextGeneration.As<TextGenerationWatermarkDetector>();
        TextGenerationWatermarkOptions otherKey = new()
        {
            Algorithm = TextGenerationWatermarkAlgorithm.Gumbel,
            Key = 0x0BAD_C0DE_0000_0001,
            KeyId = 7,
        };

        // A watermark applies only to sampled tokens, so sample with a fixed seed.
        uint[] prompt = Format(model.Tokenizer, [User("Write three sentences about autumn leaves.")]);
        foreach (TextGenerationWatermarkMode mode in new[] { TextGenerationWatermarkMode.Default, TextGenerationWatermarkMode.Disabled })
        {
            using TextGenerationOptions options = context.Tasks.TextGeneration.CreateTextGenerationOptions(new TextGenerationOptionValues
            {
                MaxNewTokens = 64,
                Temperature = 0.8f,
                Seed = 1234,
                WatermarkMode = mode,
            });
            TextGenerationResult result = await StreamAsync(
                model.Generator, model.Generator.GenerateTokensAsync(prompt, options, context.Cancellation), $"Reply ({mode}): ");
            uint[] tokens = result.GetTokenIds();
            if (result.TryAs(out TextGenerationWatermarkResult? watermarkResult))
            {
                using (watermarkResult)
                {
                    TextGenerationWatermarkStatistics s = watermarkResult.WatermarkStatistics;
                    Console.WriteLine(
                        $"Watermark statistics: algorithm={s.Algorithm} keyId={s.KeyId} generated={s.GeneratedTokenCount} " +
                        $"watermarked={s.WatermarkedTokenCount} repeatedContext={s.RepeatedContextTokenCount} " +
                        $"partialContext={s.PartialContextTokenCount} greedy={s.GreedyTokenCount} other={s.OtherUnwatermarkedTokenCount}");
                }
            }
            else
            {
                Console.WriteLine("Watermark statistics: none");
            }

            Console.WriteLine($"Detect with the key:     {DescribeDetection(detector.DetectWatermark(watermark, tokens))}");
            Console.WriteLine($"Detect with another key: {DescribeDetection(detector.DetectWatermark(otherKey, tokens))}");
        }
    }

    // Passes the prompt as two token segments, such as text that an
    // application assembles from parts.
    private static async Task RunSegmentsAsync(Context context)
    {
        using TextGenerationConfiguration configuration = context.CreateConfiguration();
        await using ModelSession model = await ModelSession.CreateAsync(context, configuration);
        if (!(await model.Generator.InvokeAsync(session => session.Capabilities)).SupportsSegmentedPrompts)
        {
            Console.WriteLine("Segmented prompts are not supported by this model/route (Capabilities.SupportsSegmentedPrompts is false).");
            return;
        }

        using TextGenerationOptions options = context.Tasks.TextGeneration.CreateTextGenerationOptions(32);
        uint[] prompt = Format(model.Tokenizer, [User("Name a fruit that is yellow.")]);
        int split = prompt.Length / 2;
        GenerationSegment[] segments = [GenerationSegment.FromTokens(prompt.AsMemory(0, split)), GenerationSegment.FromTokens(prompt.AsMemory(split))];
        Console.WriteLine($"Segments: {split} + {prompt.Length - split} tokens");
        TextGenerationResult segmented = await StreamAsync(
            model.Generator,
            model.Generator.GenerateAsync((session, o, cancellation) => session.GenerateSegments(segments, o, cancellation), options, context.Cancellation),
            "Reply: ");
        Console.WriteLine($"Tokens: prompt={segmented.PromptTokenCount} generated={segmented.GetTokenIds().Length}");
        await model.Generator.InvokeAsync(session => ReadSequence(session, "After the reply"));
    }

    // Streams a generation to the console and checks that the result text
    // matches the streamed fragments. The generator owns the result until its
    // next generation.
    private static async Task<TextGenerationResult> StreamAsync(TextGenerator generator, IAsyncEnumerable<TextGenerationUpdate> updates, string label)
    {
        Console.Write(label);
        StringBuilder streamed = new();
        try
        {
            await foreach (TextGenerationUpdate update in updates)
            {
                streamed.Append(update.Text);
                Console.Write(update.Text);
            }
        }
        catch (OperationCanceledException) when (generator.LastResult?.FinishReason == TextGenerationFinishReason.Canceled)
        {
            Console.WriteLine();
            Console.WriteLine($"Finished because: canceled after {generator.LastResult.GetTokenIds().Length} tokens");
            throw;
        }

        Console.WriteLine();
        TextGenerationResult result = generator.LastResult!;
        if (result.Text != streamed.ToString())
        {
            throw WinMLException.InvalidData("The result text does not match the streamed fragments.");
        }

        return result;
    }

    // Prints the sequence a session retains and returns its token IDs. Runs on
    // the session's thread.
    private static uint[] ReadSequence(TextGenerationSession session, string label)
    {
        using TextGenerationSequenceDisclosure disclosure = session.As<TextGenerationSequenceDisclosure>();
        TextGenerationSequenceState state = disclosure.SequenceState;
        uint[] retained = disclosure.GetRetainedTokenIds();
        Console.WriteLine(
            $"{label}: session={session.State} sequence={state.Disclosure} evaluated={state.EvaluatedTokenCount} " +
            $"retained={retained.Length} capabilities.sequenceState={session.Capabilities.SequenceState.Disclosure}");
        return retained;
    }

    // Formats a conversation with the model's chat template, ending with the
    // assistant's generation prompt.
    private static uint[] Format(Tokenizer tokenizer, IReadOnlyList<ConversationMessage> messages)
    {
        using StructuredConversationFormatter formatter = tokenizer.As<StructuredConversationFormatter>();
        using ConversationFormatResult formatted = formatter.FormatConversation(new ConversationRequest
        {
            Messages = messages,
            AddGenerationPrompt = true,
        });
        return formatted.GetTokenIds();
    }

    // Asks for a planet as JSON: a JSON schema response, or the arguments of a
    // tool call that the model must make.
    private static ConversationRequest PlanetRequest(IReadOnlyList<ConversationMessage> messages, bool asTool)
    {
        return asTool
            ? new ConversationRequest
            {
                Messages = messages,
                AddGenerationPrompt = true,
                Tools = [new ConversationTool { Name = "report_planet", Description = "Reports a planet.", ParametersJson = PlanetSchema }],
                ToolChoice = ConversationToolChoice.Required,
            }
            : new ConversationRequest
            {
                Messages = messages,
                AddGenerationPrompt = true,
                ResponseFormat = ConversationResponseFormat.JsonSchema,
                ResponseSchemaJson = PlanetSchema,
                ResponseSchemaName = "planet",
                ResponseSchemaStrict = true,
            };
    }

    // The formatter refuses a request field that the chat template cannot
    // render, such as a response format, with ERROR_NOT_SUPPORTED.
    private static bool SupportsJsonSchema(StructuredConversationFormatter formatter, ConversationMessage question)
    {
        try
        {
            using ConversationFormatResult formatted = formatter.FormatConversation(PlanetRequest([question], asTool: false));
            Console.WriteLine("Response format: JSON schema");
            return true;
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine(
                $"ResponseFormat JsonSchema is not supported by this model's chat template (0x{ex.HResult:X8}); " +
                "the schema becomes the arguments of a required tool call.");
            return false;
        }
    }

    private static ConversationMessage User(string content) => new() { Role = "user", Content = content };

    private static ConversationMessage Assistant(string content) => new() { Role = "assistant", Content = content };

    private static bool StartsWith(uint[] tokens, uint[] prefix) => tokens.AsSpan().StartsWith(prefix);

    private static string DescribeSupport(TextGenerationSpeculativeSupport support, TextGenerationSpeculativeMethod method)
    {
        try
        {
            TextGenerationSpeculativeMethodSupport s = support.GetSpeculativeMethodSupport(method);
            return s.Supported
                ? $"supported, drafts from {s.DraftSource}, default {s.DefaultDraftTokenCount}, at most {s.MaxDraftTokenCount} draft tokens"
                : $"not supported (model draft layers: {(s.ModelDraftLayersPresent ? "yes" : "no")})";
        }
        catch (NotSupportedException ex)
        {
            return $"reported only by sessions (0x{ex.HResult:X8})";
        }
    }

    private static string DescribeDetection(TextGenerationWatermarkDetection detection)
    {
        return $"scored {detection.ScoredTokenCount} of {detection.TokenCount} tokens, score={detection.Score:F2} p-value={detection.PValue:G3}";
    }

    private static string DescribeRuntime(Context context, WinMLRuntime runtime)
    {
        return runtime.IsSameObject(context.Runtime) ? "the Runtime the sample created" : "another Runtime object";
    }

    private static string Bound(WinMLObject? value) => value is null ? "none" : "bound";

    private static string Join(IEnumerable<uint> values) => string.Join(", ", values);

    private static string Quote(string text)
    {
        return "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: task-text-generation-features-csharp.exe <model.gguf> [scenario]");
        Console.WriteLine($"  scenario: all (default), {string.Join(", ", ScenarioNames)}.");
        Console.WriteLine("  The llama.cpp model loader loads the GGUF file for the CPU. Each scenario exercises one");
        Console.WriteLine("  optional text generation interface, or reports that the model or route does not support it.");
        Console.WriteLine("  Ctrl+C cancels generation.");
    }
}

// The objects every scenario shares: the Runtime, a CPU target, the model
// loaded once through the llama.cpp loader, and the Task API.
internal sealed class Context : IDisposable
{
    private Context(WinMLRuntime runtime, LlamaModelLoader loader, ExecutionTarget target, Model model, WinMLTasks tasks, string modelPath, CancellationToken cancellation)
    {
        Runtime = runtime;
        Loader = loader;
        Target = target;
        Model = model;
        Tasks = tasks;
        ModelPath = modelPath;
        Cancellation = cancellation;
    }

    public WinMLRuntime Runtime { get; }

    public LlamaModelLoader Loader { get; }

    public ExecutionTarget Target { get; }

    public Model Model { get; }

    public WinMLTasks Tasks { get; }

    public string ModelPath { get; }

    public CancellationToken Cancellation { get; }

    public static Context Create(string modelPath, CancellationToken cancellation)
    {
        WinMLRuntime runtime = WinMLRuntime.Create();
        LlamaModelLoader? loader = null;
        ExecutionTarget? target = null;
        Model? model = null;
        try
        {
            // The loader is available when the Windows ML package includes the llama.cpp engine.
            if (!runtime.TryAs(out loader))
            {
                throw WinMLException.NoInterface(
                    "The llama.cpp model loader is not available. Use a Windows ML package that includes the llama.cpp engine.");
            }

            target = runtime.CreateCpuExecutionTarget();
            model = loader.LoadModelFromFile(modelPath);
            return new Context(runtime, loader, target, model, WinMLTasks.Create(runtime), modelPath, cancellation);
        }
        catch
        {
            model?.Dispose();
            target?.Dispose();
            loader?.Dispose();
            runtime.Dispose();
            throw;
        }
    }

    // A configuration bound to the model, and to a draft model when one is
    // given. SetModel reads the tokenizer from the GGUF file.
    public TextGenerationConfiguration CreateConfiguration(Model? draftModel = null)
    {
        TextGenerationConfiguration configuration = Tasks.TextGeneration.CreateTextGenerationConfiguration();
        try
        {
            using ModelTextGenerationConfiguration modelConfiguration = configuration.As<ModelTextGenerationConfiguration>();
            modelConfiguration.SetModel(Target, Model, draftModel is null ? null : new TextGenerationModelOptions { DraftModel = draftModel });
            return configuration;
        }
        catch
        {
            configuration.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Tasks.Dispose();
        Model.Dispose();
        Target.Dispose();
        Loader.Dispose();
        Runtime.Dispose();
    }
}

// A session for one configuration on its own thread, with the configuration's
// tokenizer and the task created from it.
internal sealed class ModelSession : IAsyncDisposable
{
    private ModelSession(Tokenizer tokenizer, TextGenerationTask task, TextGenerator generator)
    {
        Tokenizer = tokenizer;
        Task = task;
        Generator = generator;
    }

    public Tokenizer Tokenizer { get; }

    public TextGenerationTask Task { get; }

    public TextGenerator Generator { get; }

    // The task must use the configuration's tokenizer: the one SetModel created.
    public static async Task<ModelSession> CreateAsync(Context context, TextGenerationConfiguration configuration)
    {
        Tokenizer tokenizer = configuration.GetTokenizer();
        TextGenerationTask? task = null;
        try
        {
            task = context.Tasks.TextGeneration.CreateTextGenerationTask(tokenizer);
            TextGenerator generator = await TextGenerator.CreateAsync(context.Tasks, task, configuration);
            return new ModelSession(tokenizer, task, generator);
        }
        catch
        {
            task?.Dispose();
            tokenizer.Dispose();
            throw;
        }
    }

    // Disposing the generator closes the session on its thread.
    public async ValueTask DisposeAsync()
    {
        await Generator.DisposeAsync();
        Task.Dispose();
        Tokenizer.Dispose();
    }
}
