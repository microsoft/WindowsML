// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using System.Text.RegularExpressions;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace TaskStudio.Runtime;

internal sealed record GenerationUpdate(string Text, bool IsReasoning = false);

// Times are measured by the app on the pull stream, from the request call; they include the app's own read loop.
internal sealed record GenerationTiming(uint GeneratedTokens, TimeSpan? FirstToken, TimeSpan? LastToken, TimeSpan Total,
    bool TokenEvents, PromptCacheUse? Cache = null)
{
    public double? TtftMs => FirstToken?.TotalMilliseconds;
    public double? DecodeMs => GeneratedTokens > 1 && FirstToken is { } first && LastToken is { } last && last > first
        ? (last - first).TotalMilliseconds : null;
    public double? DecodeTokensPerSecond => DecodeMs is double ms ? (GeneratedTokens - 1) * 1000.0 / ms : null;
    public double? MsPerToken => DecodeMs is double ms ? ms / (GeneratedTokens - 1) : null;
}

internal sealed record PromptCacheUse(string Mode, uint PromptTokens, uint CachedTokens, uint PrefilledTokens);
internal sealed record GenerationResult(string Text, uint PromptTokens, string FinishReason, bool Canceled, TimeSpan Elapsed,
    GenerationTiming? Timing = null, SpeculativeStats? Speculation = null);

internal sealed record GenerationSettings(uint MaxTokens = 256, float Temperature = 0,
    uint TopK = 0, float TopP = 1, float MinP = 0, float RepetitionPenalty = 1,
    uint RepetitionWindow = 0, float PresencePenalty = 0, float FrequencyPenalty = 0,
    ulong Seed = 0)
{
    // App policy for one request; the context window must also hold the prompt plus these tokens.
    public const uint MaxNewTokensLimit = 32768;

    public void Validate()
    {
        if (MaxTokens is < 1 or > MaxNewTokensLimit) throw new ArgumentOutOfRangeException(nameof(MaxTokens), $"Maximum new tokens must be between 1 and {MaxNewTokensLimit:N0}.");
        if (!float.IsFinite(Temperature) || Temperature < 0)
            throw new ArgumentOutOfRangeException(nameof(Temperature), "Temperature must be finite and nonnegative.");
        if (!float.IsFinite(TopP) || TopP is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(TopP), "Top-p must be greater than 0 and at most 1.");
        if (!float.IsFinite(MinP) || MinP is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MinP), "Min-p must be between 0 and 1.");
        if (!float.IsFinite(RepetitionPenalty) || RepetitionPenalty <= 0)
            throw new ArgumentOutOfRangeException(nameof(RepetitionPenalty), "Repetition penalty must be greater than 0.");
        if (!float.IsFinite(PresencePenalty) || !float.IsFinite(FrequencyPenalty))
            throw new ArgumentException("Presence and frequency penalties must be finite numbers.");
    }
}

// The prefill programs the model provider compiles to read a prompt. Generated tokens are the same under both.
internal enum PrefillProfile
{
    // The provider's choice. For a dense decoder, a 512-token program reads long prompts
    // and a 128-token program reads short prompts and prompt tails.
    Default,
    // One 128-token program, with less to compile and keep on the GPU, but slower on prompts longer than 128 tokens.
    Compact
}

internal sealed partial class TaskRuntime : IAsyncDisposable
{
    private const int NotSupportedHResult = unchecked((int)0x80070032);
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const int InvalidStateHResult = unchecked((int)0x8007139F);
    private const int CanceledHResult = unchecked((int)0x800704C7);
    private const int NoInterfaceHResult = unchecked((int)0x80004002);

    // Text generation sessions and their streams belong to the thread that created them, so every Runtime call runs here.
    private readonly SessionThread worker = new("Windows ML Task worker");
    private readonly List<(string Role, string Content)> history = new();
    private WinMLRuntime? runtime;
    private WinMLTasks? tasks;
    private Tokenizer? tokenizer;
    private TextGenerationSession? textSession;
    private ChatCompletionSession? chatSession;
    private Task? disposal;

    private Task<T> Run<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(disposal is not null, this);
        return worker.InvokeAsync(action);
    }

    public Task<GpuDevice> LoadAsync(string modelPath, string tokenizerPath, uint? contextTokens, GpuDevice device,
        SpeculativeChoice? speculation = null, PrefillProfile prefill = PrefillProfile.Default) => Run(() =>
    {
        speculation ??= SpeculativeChoice.Off;
        bool gguf = IsGgufFile(modelPath);
        if (!gguf && (!Directory.Exists(modelPath) || !File.Exists(Path.Combine(modelPath, "config.json"))))
            throw new ArgumentException("Select a model directory containing config.json and safetensors weights, or a .gguf file.");
        // A GGUF file carries its own tokenizer; SetModel creates it when none is set.
        bool ownTokenizer = gguf && string.IsNullOrWhiteSpace(tokenizerPath);
        if (!ownTokenizer && !File.Exists(tokenizerPath))
            throw new ArgumentException("Select the matching model's tokenizer_config.json.");
        if (contextTokens is < 128 or > 262144)
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        if (runtime != null) throw new InvalidOperationException("Release the current sessions before loading another model.");
        bool companion = speculation.Mode == SpeculativeMode.CompanionDrafter;
        string drafterPath = speculation.DrafterPath?.Trim() ?? "";
        bool drafterGguf = companion && IsGgufFile(drafterPath);
        if (companion && !drafterGguf && !Directory.Exists(drafterPath))
            throw new ArgumentException("Select the companion drafter: a draft model folder, or a .gguf file when the model is GGUF.");
        if (companion && drafterGguf != gguf)
            throw new ArgumentException("Load the drafter the same way as the model: a folder for a safetensors model, a .gguf file for a GGUF model.");

        TextGenerationConfiguration? configuration = null;
        try
        {
            runtime = WinMLRuntime.Create();
            tasks = WinMLTasks.Create(runtime);
            if (!ownTokenizer) tokenizer = Tokenizer.CreateFromFile(tokenizerPath);

            textTarget = CreateGpuTarget(runtime, device);
            GpuDevice actualDevice = DescribeGpuTarget(textTarget);
            textModel = gguf ? runtime.LoadModelFromFile(modelPath) : runtime.LoadModelFromPath(modelPath);
            if (companion)
                textDraftModel = drafterGguf ? runtime.LoadModelFromFile(drafterPath) : runtime.LoadModelFromPath(drafterPath);
            textContextTokens = contextTokens;
            textPrefill = prefill;
            try { configuration = BindConfiguration(tryProgramCache: true); }
            catch (Exception error) when (companion && error.HResult == NotSupportedHResult)
            {
                throw new NotSupportedException("The Runtime cannot use this companion drafter with this model. " +
                    "Pick a drafter trained for this model, or load without speculative decoding.", error);
            }
            catch (Exception error) when (gguf && ownTokenizer && error.HResult == FileNotFoundHResult)
            {
                throw new NotSupportedException("The Runtime could not read the tokenizer stored in this GGUF file. " +
                    "Set the tokenizer to the model's tokenizer_config.json (with tokenizer.json beside it) and try again.", error);
            }
            catch (Exception error) when (gguf && error.HResult == InvalidStateHResult && ownTokenizer)
            {
                throw new NotSupportedException("The Runtime could not create a tokenizer from this GGUF file. " +
                    "Set the tokenizer to the model's tokenizer_config.json (with tokenizer.json beside it) and try again.", error);
            }
            catch (Exception error) when (gguf && error.HResult == NotSupportedHResult)
            {
                throw new NotSupportedException("The CGC backend does not support this GGUF file's architecture, " +
                    "layout or tensor types. Use a supported GGUF file or a safetensors model directory.", error);
            }
            // The task and the Chat layer must use the exact tokenizer the configuration holds.
            tokenizer ??= configuration.GetTokenizer();
            SpeculativeSupport = ReadSpeculativeSupport(configuration);
            if (SpeculativeSupport.IsSupported(speculation.Mode, companion) == false)
                throw new NotSupportedException($"The Runtime reports that this model does not support {Describe(speculation.Mode)}. " +
                    SupportedMethodsText(SpeculativeSupport, companion));
            textTask = tasks.TextGeneration.CreateTextGenerationTask(tokenizer);
            try { chatTask = tasks.ChatCompletion.CreateChatCompletionTask(tokenizer); }
            // The chat task formats conversations with the tokenizer's chat template, and a tokenizer without one,
            // such as a speech model's, doesn't offer the formatter.
            catch (Exception error) when (error.HResult == NoInterfaceHResult)
            {
                throw new NotSupportedException("This tokenizer has no chat template, which Chat needs. " + (ownTokenizer
                    ? "Set the tokenizer to the model's tokenizer_config.json and try again."
                    : "Select the tokenizer_config.json that came with this model and try again."), error);
            }
            try
            {
                // CreateSessions consumes the configuration whether or not it succeeds.
                var bound = configuration;
                configuration = null;
                CreateSessions(speculation.Mode, speculation.DraftTokens, bound);
            }
            catch (Exception error) when (speculation.Mode != SpeculativeMode.Off && error.HResult == NotSupportedHResult)
            {
                // The refusal doesn't name its cause. A drafter the support query accepts can still be refused once the
                // session reads its weights, and a model the GPU can't run, such as NVFP4 weights, is refused here too.
                string method = companion ? "this drafter" : Describe(speculation.Mode);
                string refused = companion ? "drafter" : "method";
                throw new NotSupportedException($"The Runtime cannot create sessions for this model with {method} on this GPU. " +
                    $"It doesn't say whether it refused the model or the {refused}, so load the model with speculative decoding " +
                    "off to tell them apart.", error);
            }
            loadedSpeculation = speculation;
            history.Clear();
            return actualDevice;
        }
        catch
        {
            Unload();
            throw;
        }
        finally
        {
            configuration?.Dispose();
        }
    });

    // Binds a new configuration to the loaded model (and drafter). A configuration is consumed by the session it
    // creates, so a different speculative method needs a new one.
    private TextGenerationConfiguration BindConfiguration(bool tryProgramCache, bool bindDrafter = true)
    {
        Model? draft = bindDrafter ? textDraftModel : null;
        TextGenerationConfiguration? configuration = tasks!.TextGeneration.CreateTextGenerationConfiguration();
        try
        {
            if (tokenizer != null) configuration.SetTokenizer(tokenizer);
            using var modelConfiguration = configuration.As<ModelTextGenerationConfiguration>();
            // The default profile is left unset so the Runtime keeps its own choice.
            TextGenerationPrefillProfile? prefillProfile = textPrefill == PrefillProfile.Compact ? TextGenerationPrefillProfile.Compact : null;
            bool cached = false;
            if (tryProgramCache)
            {
                try
                {
                    modelConfiguration.SetModel(textTarget!, textModel!, new TextGenerationModelOptions
                    {
                        CompiledProgramDirectory = CompiledProgramDirectory,
                        CompiledProgramStorage = CompiledProgramStorage.Programs,
                        DraftModel = draft,
                        PrefillProfile = prefillProfile
                    });
                    cached = true;
                }
                // A route that cannot keep compiled programs refuses the option; compile per session instead.
                // If the drafter is what it refused, the SetModel below fails too and reports it.
                catch (Exception error) when (error.HResult == NotSupportedHResult) { }
            }
            if (!cached)
                modelConfiguration.SetModel(textTarget!, textModel!, new TextGenerationModelOptions { DraftModel = draft, PrefillProfile = prefillProfile });
            TextProgramCacheUsed = cached;
            if (textContextTokens is uint capacity) configuration.LogicalCapacity = capacity;
            configuration.Validate();
            var bound = configuration;
            configuration = null;
            return bound;
        }
        finally { configuration?.Dispose(); }
    }

    private static string SupportedMethodsText(SpeculativeSupportReport report, bool drafterBound)
    {
        var supported = new[] { SpeculativeMode.PromptLookup, SpeculativeMode.ModelLayers, SpeculativeMode.CompanionDrafter }
            .Where(mode => report.IsSupported(mode, drafterBound) == true).Select(Describe).ToList();
        return supported.Count > 0 ? $"Supported: {string.Join(", ", supported)}." : "No speculative method is reported as supported.";
    }

    public static bool IsGgufFile(string path) =>
        File.Exists(path) && Path.GetExtension(path).Equals(".gguf", StringComparison.OrdinalIgnoreCase);

    // Compiled programs are reused across runs from here. Only programs are stored: weights are always
    // prepared again from the model's own files (safetensors or GGUF), never from a cached weights file.
    public static string CompiledProgramDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsMLTaskStudio", "CompiledPrograms");

    // Whether the loaded model's route accepted the compiled-program directory. Read after the load completes.
    public bool TextProgramCacheUsed { get; private set; }
    public bool SpeechProgramCacheUsed { get; private set; }

    public Task<GenerationResult> GenerateAsync(bool chat, string prompt, string systemPrompt,
        GenerationSettings settings, uint[] stopTokenIds, IProgress<GenerationUpdate> progress, CancellationToken cancellation)
    {
        settings.Validate();
        // Snapshot caller-owned input before queuing work on the native thread.
        uint[] stops = (uint[])stopTokenIds.Clone();
        return Run(() =>
        {
            if (textSession == null || chatSession == null) throw new InvalidOperationException("Create task sessions first.");
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A prompt is required.");
            var stopwatch = Stopwatch.StartNew();
            TextGenerationOptions? options = null;
            CancellationSource? source = null;
            try
            {
                options = tasks!.TextGeneration.CreateTextGenerationOptions(new TextGenerationOptionValues
                {
                    MaxNewTokens = settings.MaxTokens,
                    Temperature = settings.Temperature,
                    TopK = settings.TopK,
                    TopP = settings.TopP,
                    MinP = settings.MinP,
                    RepetitionPenalty = settings.RepetitionPenalty,
                    RepetitionPenaltyWindow = settings.RepetitionWindow,
                    PresencePenalty = settings.PresencePenalty,
                    FrequencyPenalty = settings.FrequencyPenalty,
                    Seed = settings.Seed,
                    StopTokenIds = stops
                });
                source = tasks.CreateCancellationSource();
                // The registration is disposed before releasing source, including while Cancel races completion.
                using var registration = cancellation.Register(source.Cancel);
                return chat
                    ? GenerateChat(prompt, systemPrompt, options, source, progress, stopwatch)
                    : GenerateCompletion(prompt, options, source, progress, stopwatch);
            }
            catch (Exception generationError)
            {
                try { textSession.Reset(); }
                catch (Exception resetError)
                {
                    throw new AggregateException("Generation failed and the Task session could not be reset. Release the sessions before retrying.",
                        generationError, resetError);
                }
                if (generationError.HResult == ContextWindowExceeded) throw ContextWindowError(generationError);
                throw;
            }
            finally { source?.Dispose(); options?.Dispose(); }
        });
    }

    // The Runtime refuses a request whose prompt and generated tokens don't fit the session's context window.
    private const int ContextWindowExceeded = unchecked((int)0x8007007A);

    private static InvalidOperationException ContextWindowError(Exception error) =>
        new("The prompt and the generated tokens don't fit the context window. Create the task sessions with a larger Context window.", error);

    private GenerationResult GenerateCompletion(string prompt, TextGenerationOptions options,
        CancellationSource cancellation, IProgress<GenerationUpdate> progress, Stopwatch stopwatch, uint[]? tokenIds = null)
    {
        // Completion is independent of the retained chat sequence.
        textSession!.Reset();
        long start = Stopwatch.GetTimestamp();
        TimeSpan? first = null, last = null;
        uint generated = 0;
        using TextGenerationPullStream stream = tokenIds is null
            ? textSession.GenerateText(prompt, options, cancellation)
            : textSession.GenerateTokens(tokenIds, options, cancellation);
        while (stream.TryReadNext(out _, out ReadOnlySpan<char> fragment))
        {
            // Each read returns one generated token; time it before the UI sees it.
            last = Stopwatch.GetElapsedTime(start);
            first ??= last;
            generated++;
            progress.Report(new(fragment.ToString()));
        }
        TimeSpan total = Stopwatch.GetElapsedTime(start);
        using TextGenerationResult result = stream.GetResult();
        var reason = result.FinishReason;
        bool canceled = reason == TextGenerationFinishReason.Canceled;
        if (!canceled) ThrowOnError(result.ErrorCode);
        var completion = new GenerationResult(result.Text, result.PromptTokenCount,
            EnumNames.Native("WINML_TEXT_GENERATION_FINISH_REASON_", reason), canceled, stopwatch.Elapsed,
            new GenerationTiming(generated, first, last, total, TokenEvents: true), ReadSpeculativeStats(result));
        stream.Close();
        return completion;
    }

    private GenerationResult GenerateChat(string prompt, string systemPrompt, TextGenerationOptions options,
        CancellationSource cancellation, IProgress<GenerationUpdate> progress, Stopwatch stopwatch)
    {
        var messages = new List<ConversationMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) messages.Add(new() { Role = "system", Content = systemPrompt });
        messages.AddRange(history.Select(turn => new ConversationMessage { Role = turn.Role, Content = turn.Content }));
        messages.Add(new() { Role = "user", Content = prompt });
        var request = new ConversationRequest
        {
            Messages = messages,
            AddGenerationPrompt = true,
            EnableThinking = ConversationEnableThinking.Disabled
        };
        long start = Stopwatch.GetTimestamp();
        TimeSpan? first = null, last = null;
        using ChatCompletionPullStream stream = chatSession!.Generate(request, options, cancellation);
        while (stream.TryReadNext(out ConversationOutputEvent? item))
        {
            using (item)
            {
                ConversationOutputEventType type = item.Type;
                if (type is ConversationOutputEventType.ContentDelta or ConversationOutputEventType.ReasoningDelta)
                {
                    // Chat events are parser deltas, not tokens: the parser may hold text back.
                    last = Stopwatch.GetElapsedTime(start);
                    first ??= last;
                    progress.Report(new(item.Text, type == ConversationOutputEventType.ReasoningDelta));
                }
                else if (type == ConversationOutputEventType.Error)
                {
                    int error = item.GetError(out string message);
                    if (error != CanceledHResult)
                        throw message.Length == 0 ? WinMLException.GetExceptionForHResult(error) : WinMLException.GetExceptionForHResult(error, message);
                }
            }
        }
        TimeSpan total = Stopwatch.GetElapsedTime(start);
        using ChatCompletionResult result = stream.GetResult();
        string answer = result.Content;
        var reason = result.FinishReason;
        bool canceled = reason == ChatCompletionFinishReason.Canceled;
        if (!canceled) ThrowOnError(result.ErrorCode);
        var (promptTokens, completionTokens) = result.GetTokenCounts();
        ChatCompletionPromptCacheUse cache = result.PromptCacheUse;
        var cacheUse = new PromptCacheUse(EnumNames.Words(cache.Mode),
            cache.PromptTokenCount, cache.CachedPromptTokenCount, cache.PrefilledPromptTokenCount);
        if (!canceled)
        {
            history.Add(("user", prompt));
            history.Add(("assistant", answer));
        }
        var reply = new GenerationResult(answer, promptTokens, EnumNames.Native("WINML_CHAT_COMPLETION_FINISH_REASON_", reason), canceled,
            stopwatch.Elapsed, new GenerationTiming(completionTokens, first, last, total, TokenEvents: false, cacheUse), ReadSpeculativeStats(result));
        stream.Close();
        return reply;
    }

    public Task ResetAsync() => Run(() =>
    {
        textSession?.Reset();
        history.Clear();
        return true;
    });

    public Task UnloadAsync() => Run(() => { Unload(); return true; });

    private void Unload()
    {
        try { CloseSessions(); }
        finally
        {
            Release(ref chatTask);
            Release(ref textTask);
            Release(ref textTaskNoEos);
            Release(ref textDraftModel);
            Release(ref textModel);
            Release(ref textTarget);
            textContextTokens = null;
            textPrefill = PrefillProfile.Default;
            Release(ref tokenizer);
            Release(ref tasks);
            Release(ref runtime);
            loadedSpeculation = SpeculativeChoice.Off;
            SpeculativeSupport = null;
            history.Clear();
        }
    }

    public ValueTask DisposeAsync()
    {
        // Only the first call reports a cleanup failure; later calls wait for the worker to stop, so closing again works.
        if (disposal is not null) return new ValueTask(disposal);
        Task cleanup = Run(() =>
        {
            try { Unload(); }
            finally
            {
                try { UnloadSpeech(); }
                finally { UnloadDecision(); }
            }
            return true;
        });
        Task finished = FinishDisposeAsync(cleanup);
        disposal = finished.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new ValueTask(finished);
    }

    private async Task FinishDisposeAsync(Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        finally { worker.Dispose(); }
    }

    // A result's error code; the Runtime reports a failure there rather than from the call that read the stream.
    private static void ThrowOnError(int errorCode)
    {
        if (errorCode < 0) throw WinMLException.GetExceptionForHResult(errorCode);
    }

    private static void Release<T>(ref T? value) where T : class, IDisposable
    {
        value?.Dispose();
        value = null;
    }
}

// The app reports and exports Runtime enumerations by their native names
// (WINML_TEXT_GENERATION_FINISH_REASON_STOP_TOKEN), by the last part of the name (STOP_TOKEN), or as lowercase words
// ("prompt lookup"). Each relies on the wrapper's enum members following the native names; the smoke harness's
// --names check fails if one doesn't.
internal static partial class EnumNames
{
    public static string Words<T>(T value) where T : struct, Enum =>
        WordBoundary().Replace(value.ToString(), " ").ToLowerInvariant();

    public static string Constant<T>(T value) where T : struct, Enum => Words(value).Replace(' ', '_').ToUpperInvariant();

    public static string Native<T>(string prefix, T value) where T : struct, Enum => prefix + Constant(value);

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();
}
