// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;
using static Microsoft.Windows.AI.MachineLearning.Runtime.Tasks.WINML_TEXT_GENERATION_EOS_POLICY;
using static Microsoft.Windows.AI.MachineLearning.Runtime.Tasks.WINML_TEXT_GENERATION_OPTION_FIELDS;

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

internal sealed partial class TaskRuntime : IAsyncDisposable
{
    private readonly BlockingCollection<Action> work = new();
    private readonly Thread worker;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(string Role, string Content)> history = new();
    private unsafe IWinMLRuntime* runtime;
    private unsafe IWinMLTasks* tasks;
    private unsafe IWinMLTokenizer* tokenizer;
    private unsafe IWinMLTextGenerationTaskFactory* textFactory;
    private unsafe IWinMLTextGenerationSession* textSession;
    private unsafe IWinMLChatCompletionSession* chatSession;
    private bool disposed;

    public TaskRuntime()
    {
        worker = new Thread(() =>
        {
            foreach (var action in work.GetConsumingEnumerable()) action();
            stopped.TrySetResult();
        })
        { IsBackground = true, Name = "Windows ML Task worker" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private Task<T> Run<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task;
    }

    public unsafe Task<GpuDevice> LoadAsync(string modelPath, string tokenizerPath, uint? contextTokens, GpuDevice device,
        SpeculativeChoice? speculation = null) => Run(() =>
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

        IWinMLChatCompletionTaskFactory* chatFactory = null;
        IWinMLTextGenerationConfiguration* configuration = null;
        try
        {
            Guid iid = typeof(IWinMLRuntime).GUID;
            IWinMLRuntime* createdRuntime = null;
            PInvoke.WinMLCreateRuntime(&iid, (void**)&createdRuntime).ThrowOnFailure();
            runtime = createdRuntime;
            iid = typeof(IWinMLTasks).GUID;
            IWinMLTasks* createdTasks = null;
            PInvoke.WinMLCreateTasks(runtime, &iid, (void**)&createdTasks).ThrowOnFailure();
            tasks = createdTasks;
            if (!ownTokenizer)
            {
                IWinMLTokenizer* createdTokenizer = null;
                PInvoke.WinMLCreateTokenizerFromFile(tokenizerPath, &createdTokenizer).ThrowOnFailure();
                tokenizer = createdTokenizer;
            }
            IWinMLTextGenerationTaskFactory* factory = null;
            tasks->QueryInterface(out factory).ThrowOnFailure();
            textFactory = factory;

            textTarget = CreateGpuTarget(runtime, device);
            GpuDevice actualDevice = DescribeGpuTarget(textTarget);
            textModel = gguf ? runtime->LoadModelFromFile(modelPath, null) : runtime->LoadModelFromPath(modelPath, null);
            if (companion)
                textDraftModel = drafterGguf ? runtime->LoadModelFromFile(drafterPath, null) : runtime->LoadModelFromPath(drafterPath, null);
            textContextTokens = contextTokens;
            try { configuration = BindConfiguration(tryProgramCache: true); }
            catch (Exception error) when (companion && error.HResult == unchecked((int)0x80070032))
            {
                throw new NotSupportedException("The Runtime cannot use this companion drafter with this model. " +
                    "Pick a drafter trained for this model, or load without speculative decoding.", error);
            }
            catch (Exception error) when (gguf && ownTokenizer && error.HResult == unchecked((int)0x80070002))
            {
                throw new NotSupportedException("The Runtime could not read the tokenizer stored in this GGUF file. " +
                    "Set the tokenizer to the model's tokenizer_config.json (with tokenizer.json beside it) and try again.", error);
            }
            catch (Exception error) when (gguf && error.HResult == unchecked((int)0x8007139F) && ownTokenizer)
            {
                throw new NotSupportedException("The Runtime could not create a tokenizer from this GGUF file. " +
                    "Set the tokenizer to the model's tokenizer_config.json (with tokenizer.json beside it) and try again.", error);
            }
            catch (Exception error) when (gguf && error.HResult == unchecked((int)0x80070032))
            {
                throw new NotSupportedException("The CGC backend does not support this GGUF file's architecture, " +
                    "layout or tensor types. Use a supported GGUF file or a safetensors model directory.", error);
            }
            // The task and the Chat layer must use the exact tokenizer the configuration holds.
            if (tokenizer == null) tokenizer = configuration->GetTokenizer();
            SpeculativeSupport = ReadSpeculativeSupport(configuration);
            if (SpeculativeSupport.IsSupported(speculation.Mode, companion) == false)
                throw new NotSupportedException($"The Runtime reports that this model does not support {Describe(speculation.Mode)}. " +
                    SupportedMethodsText(SpeculativeSupport, companion));
            textTask = textFactory->CreateTextGenerationTask(tokenizer, WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT, 0, null);
            tasks->QueryInterface(out chatFactory).ThrowOnFailure();
            chatTask = chatFactory->CreateChatCompletionTask(tokenizer);
            try
            {
                // CreateSessions consumes the configuration whether or not it succeeds.
                var bound = configuration;
                configuration = null;
                CreateSessions(speculation.Mode, speculation.DraftTokens, bound);
            }
            catch (Exception error) when (speculation.Mode != SpeculativeMode.Off && error.HResult == unchecked((int)0x80070032))
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
            Release(chatFactory); Release(configuration);
        }
    });

    // Binds a new configuration to the loaded model (and drafter). A configuration is consumed by the session it
    // creates, so a different speculative method needs a new one.
    private unsafe IWinMLTextGenerationConfiguration* BindConfiguration(bool tryProgramCache, bool bindDrafter = true)
    {
        IWinMLModel* draft = bindDrafter ? textDraftModel : null;
        IWinMLTextGenerationConfiguration* configuration = textFactory->CreateTextGenerationConfiguration();
        IWinMLModelTextGenerationConfiguration* modelConfiguration = null;
        try
        {
            if (tokenizer != null) configuration->SetTokenizer(tokenizer);
            configuration->QueryInterface(out modelConfiguration).ThrowOnFailure();
            var draftField = draft != null ? WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_DRAFT_MODEL
                : WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_NONE;
            bool cached = false;
            if (tryProgramCache)
            {
                try
                {
                    fixed (char* directory = CompiledProgramDirectory)
                    {
                        var modelOptions = new WINML_TEXT_GENERATION_MODEL_OPTIONS
                        {
                            presentFields = WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY
                                | WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE | draftField,
                            compiledProgramDirectory = new PCWSTR(directory),
                            compiledProgramStorage = WINML_COMPILED_PROGRAM_STORAGE.WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS,
                            draftModel = draft
                        };
                        modelConfiguration->SetModel(textTarget, textModel, &modelOptions);
                    }
                    cached = true;
                }
                // A route that cannot keep compiled programs refuses the option; compile per session instead.
                // If the drafter is what it refused, the SetModel below fails too and reports it.
                catch (Exception error) when (error.HResult == unchecked((int)0x80070032)) { }
            }
            if (!cached)
            {
                var modelOptions = new WINML_TEXT_GENERATION_MODEL_OPTIONS { presentFields = draftField, draftModel = draft };
                modelConfiguration->SetModel(textTarget, textModel, draft != null ? &modelOptions : null);
            }
            TextProgramCacheUsed = cached;
            if (textContextTokens is uint capacity) configuration->SetLogicalCapacity(capacity);
            configuration->Validate();
            var bound = configuration;
            configuration = null;
            return bound;
        }
        finally { Release(modelConfiguration); Release(configuration); }
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

    public unsafe Task<GenerationResult> GenerateAsync(bool chat, string prompt, string systemPrompt,
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
            IWinMLTextGenerationOptions* options = null;
            IWinMLCancellationSource* source = null;
            try
            {
                var values = new WINML_TEXT_GENERATION_OPTIONS
                {
                    presentFields = WINML_TEXT_GENERATION_OPTION_FIELD_ALL,
                    maxNewTokens = settings.MaxTokens,
                    temperature = settings.Temperature,
                    topK = settings.TopK,
                    topP = settings.TopP,
                    minP = settings.MinP,
                    repetitionPenalty = settings.RepetitionPenalty,
                    repetitionPenaltyWindow = settings.RepetitionWindow,
                    presencePenalty = settings.PresencePenalty,
                    frequencyPenalty = settings.FrequencyPenalty,
                    seed = settings.Seed
                };
                fixed (uint* stopPointer = stops)
                    options = textFactory->CreateTextGenerationOptions(&values, checked((uint)stops.Length), stopPointer);
                source = tasks->CreateCancellationSource();
                // The registration is disposed before releasing source, including while Cancel races completion.
                nint sourceAddress = (nint)source;
                using var registration = cancellation.Register(() => ((IWinMLCancellationSource*)sourceAddress)->Cancel());
                GenerationResult result = chat
                    ? GenerateChat(prompt, systemPrompt, options, source, progress, stopwatch)
                    : GenerateCompletion(prompt, options, source, progress, stopwatch);
                return result;
            }
            catch (Exception generationError)
            {
                try { textSession->Reset(); }
                catch (Exception resetError)
                {
                    throw new AggregateException("Generation failed and the Task session could not be reset. Release the sessions before retrying.",
                        generationError, resetError);
                }
                throw;
            }
            finally { Release(source); Release(options); }
        });
    }

    private unsafe GenerationResult GenerateCompletion(string prompt, IWinMLTextGenerationOptions* options,
        IWinMLCancellationSource* cancellation, IProgress<GenerationUpdate> progress, Stopwatch stopwatch)
    {
        IWinMLTextGenerationPullStream* stream = null;
        IWinMLTextGenerationResult* result = null;
        try
        {
            // Completion is independent of the retained chat sequence.
            textSession->Reset();
            long start = Stopwatch.GetTimestamp();
            TimeSpan? first = null, last = null;
            uint generated = 0;
            stream = textSession->GenerateText(prompt, options, cancellation);
            while (true)
            {
                WINML_TEXT_GENERATION_READ_STATUS status;
                uint token;
                PCWSTR fragment;
                stream->ReadNext(&status, &token, &fragment);
                if (status == WINML_TEXT_GENERATION_READ_STATUS.WINML_TEXT_GENERATION_READ_STATUS_COMPLETED) break;
                // Each read returns one generated token; time it before the UI sees it.
                last = Stopwatch.GetElapsedTime(start);
                first ??= last;
                generated++;
                progress.Report(new(fragment.ToString()));
            }
            TimeSpan total = Stopwatch.GetElapsedTime(start);
            result = stream->GetResult();
            PCWSTR text;
            result->GetText(&text);
            var reason = result->GetFinishReason();
            bool canceled = reason == WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_CANCELED;
            if (!canceled) result->GetErrorCode().ThrowOnFailure();
            return new(text.ToString(), result->GetPromptTokenCount(), reason.ToString(), canceled, stopwatch.Elapsed,
                new GenerationTiming(generated, first, last, total, TokenEvents: true), ReadSpeculativeStats(result));
        }
        finally
        {
            try { if (stream != null) stream->Close(); }
            finally { Release(result); Release(stream); }
        }
    }

    private unsafe GenerationResult GenerateChat(string prompt, string systemPrompt, IWinMLTextGenerationOptions* options,
        IWinMLCancellationSource* cancellation, IProgress<GenerationUpdate> progress, Stopwatch stopwatch)
    {
        var messages = new List<(string Role, string Content)>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) messages.Add(("system", systemPrompt));
        messages.AddRange(history);
        messages.Add(("user", prompt));
        var allocations = new List<nint>();
        var nativeMessages = new WINML_CONVERSATION_MESSAGE[messages.Count];
        IWinMLChatCompletionPullStream* stream = null;
        IWinMLChatCompletionResult* result = null;
        try
        {
            PCWSTR Allocate(string value)
            {
                nint address = Marshal.StringToCoTaskMemUni(value);
                allocations.Add(address);
                return new PCWSTR((char*)address);
            }
            for (int i = 0; i < messages.Count; i++)
            {
                nativeMessages[i].role = Allocate(messages[i].Role);
                nativeMessages[i].content = Allocate(messages[i].Content);
            }
            long start = Stopwatch.GetTimestamp();
            TimeSpan? first = null, last = null;
            fixed (WINML_CONVERSATION_MESSAGE* messagePointer = nativeMessages)
            {
                var request = new WINML_CONVERSATION_REQUEST
                {
                    messageCount = (uint)messages.Count,
                    messages = messagePointer,
                    addGenerationPrompt = true,
                    enableThinking = WINML_CONVERSATION_ENABLE_THINKING.WINML_CONVERSATION_ENABLE_THINKING_DISABLED
                };
                stream = chatSession->Generate(&request, options, cancellation);
            }
            while (true)
            {
                WINML_CHAT_COMPLETION_READ_STATUS status;
                IWinMLConversationOutputEvent* item = null;
                stream->ReadNext(&status, &item);
                try
                {
                    if (status == WINML_CHAT_COMPLETION_READ_STATUS.WINML_CHAT_COMPLETION_READ_STATUS_COMPLETED) break;
                    WINML_CONVERSATION_OUTPUT_EVENT_TYPE type;
                    item->GetType(&type);
                    if (type is WINML_CONVERSATION_OUTPUT_EVENT_TYPE.WINML_CONVERSATION_OUTPUT_EVENT_TYPE_CONTENT_DELTA
                        or WINML_CONVERSATION_OUTPUT_EVENT_TYPE.WINML_CONVERSATION_OUTPUT_EVENT_TYPE_REASONING_DELTA)
                    {
                        // Chat events are parser deltas, not tokens: the parser may hold text back.
                        last = Stopwatch.GetElapsedTime(start);
                        first ??= last;
                        PCWSTR fragment;
                        item->GetText(&fragment);
                        progress.Report(new(fragment.ToString(),
                            type == WINML_CONVERSATION_OUTPUT_EVENT_TYPE.WINML_CONVERSATION_OUTPUT_EVENT_TYPE_REASONING_DELTA));
                    }
                    else if (type == WINML_CONVERSATION_OUTPUT_EVENT_TYPE.WINML_CONVERSATION_OUTPUT_EVENT_TYPE_ERROR)
                    {
                        Windows.Win32.Foundation.HRESULT error;
                        PCWSTR message;
                        item->GetError(&error, &message);
                        if (error.Value != unchecked((int)0x800704C7))
                            throw new COMException(message.ToString(), error.Value);
                    }
                }
                finally { Release(item); }
            }
            TimeSpan total = Stopwatch.GetElapsedTime(start);
            result = stream->GetResult();
            PCWSTR text;
            result->GetContent(&text);
            var reason = result->GetFinishReason();
            bool canceled = reason == WINML_CHAT_COMPLETION_FINISH_REASON.WINML_CHAT_COMPLETION_FINISH_REASON_CANCELED;
            if (!canceled) result->GetErrorCode().ThrowOnFailure();
            uint promptTokens, completionTokens;
            result->GetTokenCounts(&promptTokens, &completionTokens);
            WINML_CHAT_COMPLETION_PROMPT_CACHE cache = result->GetPromptCacheUse();
            string mode = cache.mode.ToString();
            var cacheUse = new PromptCacheUse(mode[(mode.LastIndexOf("_MODE_", StringComparison.Ordinal) + 6)..].Replace('_', ' ').ToLowerInvariant(),
                cache.promptTokenCount, cache.cachedPromptTokenCount, cache.prefilledPromptTokenCount);
            string answer = text.ToString();
            if (!canceled)
            {
                history.Add(("user", prompt));
                history.Add(("assistant", answer));
            }
            return new(answer, promptTokens, reason.ToString(), canceled, stopwatch.Elapsed,
                new GenerationTiming(completionTokens, first, last, total, TokenEvents: false, cacheUse), ReadSpeculativeStats(result));
        }
        finally
        {
            try { if (stream != null) stream->Close(); }
            finally
            {
                Release(result); Release(stream);
                foreach (nint address in allocations) Marshal.FreeCoTaskMem(address);
            }
        }
    }

    public unsafe Task ResetAsync() => Run(() =>
    {
        if (textSession != null) textSession->Reset();
        history.Clear();
        return true;
    });

    public unsafe Task UnloadAsync() => Run(() => { Unload(); return true; });

    private unsafe void Unload()
    {
        try { CloseSessions(); }
        finally
        {
            Release(chatTask); chatTask = null;
            Release(textTask); textTask = null;
            Release(textTaskNoEos); textTaskNoEos = null;
            Release(textDraftModel); textDraftModel = null;
            Release(textModel); textModel = null;
            Release(textTarget); textTarget = null;
            textContextTokens = null;
            Release(textFactory); textFactory = null;
            Release(tokenizer); tokenizer = null;
            Release(tasks); tasks = null;
            Release(runtime); runtime = null;
            loadedSpeculation = SpeculativeChoice.Off;
            SpeculativeSupport = null;
            history.Clear();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return new ValueTask(stopped.Task);
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
        disposed = true;
        work.CompleteAdding();
        return new ValueTask(FinishDisposeAsync(cleanup));
    }

    private async Task FinishDisposeAsync(Task cleanup)
    {
        try { await cleanup; }
        finally { await stopped.Task; work.Dispose(); }
    }

    private static unsafe void Release<T>(T* value) where T : unmanaged
    {
        if (value != null)
            ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)value)[2])(value);
    }
}
