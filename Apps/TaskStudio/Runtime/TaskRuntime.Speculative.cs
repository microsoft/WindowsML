// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;
using static Microsoft.Windows.AI.MachineLearning.Runtime.Tasks.WINML_TEXT_GENERATION_SPECULATIVE_METHOD;

namespace TaskStudio.Runtime;

// How draft tokens are proposed. The session verifies them in one step and keeps only what its own sampling
// produces, so the choice changes speed, not the sampling procedure.
internal enum SpeculativeMode
{
    Off,
    // Drafts copied from earlier text in the prompt and reply; needs no extra model.
    PromptLookup,
    // Multi-token prediction layers the model itself carries.
    ModelLayers,
    // A drafter trained against the model, such as a DFlash block-diffusion draft model.
    CompanionDrafter
}

// DraftTokens of zero lets the Runtime choose.
internal sealed record SpeculativeChoice(SpeculativeMode Mode, string? DrafterPath = null, uint DraftTokens = 0)
{
    public static SpeculativeChoice Off { get; } = new(SpeculativeMode.Off);
}

// What the Runtime reports for a method on the loaded model, before a session is created.
internal sealed record SpeculativeMethodSupport(bool Supported, string DraftSource, uint DefaultDraftTokens, uint MaxDraftTokens,
    bool ModelDraftLayersPresent);

internal sealed record SpeculativeSupportReport(SpeculativeMethodSupport? PromptLookup, SpeculativeMethodSupport? Model)
{
    // Null entries mean the serving path reports support only by creating a session.
    public bool? IsSupported(SpeculativeMode mode, bool drafterBound) => mode switch
    {
        SpeculativeMode.Off => true,
        SpeculativeMode.PromptLookup => PromptLookup?.Supported,
        SpeculativeMode.ModelLayers => drafterBound ? false : Model is null ? null : Model.Supported && Model.ModelDraftLayersPresent,
        SpeculativeMode.CompanionDrafter => drafterBound ? Model?.Supported : false,
        _ => false
    };
}

// Per-stream statistics from the Runtime. Each verification step keeps its accepted drafts plus one token of its own.
internal sealed record SpeculativeStats(string Method, uint VerificationSteps, uint DraftTokens, uint AcceptedTokens)
{
    public bool Active => VerificationSteps > 0;
    public double? AcceptRate => DraftTokens > 0 ? (double)AcceptedTokens / DraftTokens : null;
    public double? TokensPerStep => VerificationSteps > 0 ? (double)(AcceptedTokens + VerificationSteps) / VerificationSteps : null;

    public string Describe() => !Active
        ? $"speculation {Method.ToLowerInvariant()}: no verification steps"
        : $"drafted {DraftTokens:N0}, accepted {AcceptedTokens:N0} ({AcceptRate:P0}), {TokensPerStep:0.00} tokens per verify step";
}

// One prompt of the Off vs speculative comparison.
internal sealed record SpeculativeComparisonRow(string Prompt, SpeculativeMode Mode, string Text, GenerationTiming Timing,
    SpeculativeStats? Stats, string FinishReason, uint PromptTokens);

internal static class TextComparison
{
    // Characters two outputs share before they first differ.
    public static int CommonPrefix(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }
}

internal sealed partial class TaskRuntime
{
    private unsafe IWinMLExecutionTarget* textTarget;
    private unsafe IWinMLModel* textModel;
    private unsafe IWinMLModel* textDraftModel;
    private uint? textContextTokens;
    private unsafe IWinMLTextGenerationTask* textTask;
    private unsafe IWinMLTextGenerationTask* textTaskNoEos;
    private unsafe IWinMLChatCompletionTask* chatTask;
    private SpeculativeChoice loadedSpeculation = SpeculativeChoice.Off;

    public SpeculativeChoice LoadedSpeculation => loadedSpeculation;
    public SpeculativeSupportReport? SpeculativeSupport { get; private set; }

    private static WINML_TEXT_GENERATION_SPECULATIVE_METHOD ToMethod(SpeculativeMode mode) => mode switch
    {
        SpeculativeMode.PromptLookup => WINML_TEXT_GENERATION_SPECULATIVE_METHOD_PROMPT_LOOKUP,
        SpeculativeMode.ModelLayers or SpeculativeMode.CompanionDrafter => WINML_TEXT_GENERATION_SPECULATIVE_METHOD_MODEL,
        _ => WINML_TEXT_GENERATION_SPECULATIVE_METHOD_NONE
    };

    public static string Describe(SpeculativeMode mode) => mode switch
    {
        SpeculativeMode.PromptLookup => "prompt lookup",
        SpeculativeMode.ModelLayers => "model draft layers (MTP)",
        SpeculativeMode.CompanionDrafter => "drafter model",
        _ => "off"
    };

    private static string ShortName<T>(T value, string marker) where T : Enum
    {
        string name = value.ToString();
        int at = name.LastIndexOf(marker, StringComparison.Ordinal);
        return (at >= 0 ? name[(at + marker.Length)..] : name).Replace('_', ' ').ToLowerInvariant();
    }

    // Reads what the bound model supports. A path that reports only through CreateSession returns null entries.
    private static unsafe SpeculativeSupportReport ReadSpeculativeSupport(IWinMLTextGenerationConfiguration* configuration)
    {
        IWinMLTextGenerationSpeculativeSupport* support = null;
        if (configuration->QueryInterface(out support).Failed || support == null) return new(null, null);
        try
        {
            SpeculativeMethodSupport? Query(WINML_TEXT_GENERATION_SPECULATIVE_METHOD method)
            {
                try
                {
                    var value = support->GetSpeculativeMethodSupport(method);
                    return new(value.supported, ShortName(value.draftSource, "_SOURCE_"), value.defaultDraftTokenCount,
                        value.maxDraftTokenCount, value.modelDraftLayersPresent);
                }
                catch (Exception error) when (error.HResult == unchecked((int)0x80070032)) { return null; }
            }
            return new(Query(WINML_TEXT_GENERATION_SPECULATIVE_METHOD_PROMPT_LOOKUP), Query(WINML_TEXT_GENERATION_SPECULATIVE_METHOD_MODEL));
        }
        finally { Release(support); }
    }

    private static unsafe void ApplySpeculation(IWinMLTextGenerationConfiguration* configuration, SpeculativeMode mode, uint draftTokens)
    {
        IWinMLTextGenerationSpeculativeConfiguration* speculative = null;
        configuration->QueryInterface(out speculative).ThrowOnFailure();
        try
        {
            speculative->SetSpeculativeOptions(new WINML_TEXT_GENERATION_SPECULATIVE_OPTIONS
            {
                method = ToMethod(mode),
                draftTokenCount = mode == SpeculativeMode.Off ? 0 : draftTokens
            });
        }
        finally { Release(speculative); }
    }

    // Creates the text and chat sessions with the given speculation, from a configuration bound for it. The
    // configuration is consumed (released) whether or not the sessions are created.
    private unsafe void CreateSessions(SpeculativeMode mode, uint draftTokens, IWinMLTextGenerationConfiguration* configuration = null,
        bool ignoreEndOfText = false)
    {
        // An EXPLICIT policy with no tokens never stops at end of text, so a benchmark generates its full count.
        if (ignoreEndOfText && textTaskNoEos == null)
            textTaskNoEos = textFactory->CreateTextGenerationTask(tokenizer, WINML_TEXT_GENERATION_EOS_POLICY.WINML_TEXT_GENERATION_EOS_POLICY_EXPLICIT, 0, null);
        // Only the companion method takes the drafter; the Runtime refuses one with Off or prompt lookup.
        configuration = configuration != null ? configuration
            : BindConfiguration(TextProgramCacheUsed, bindDrafter: mode == SpeculativeMode.CompanionDrafter);
        try
        {
            ApplySpeculation(configuration, mode, draftTokens);
            textSession = (ignoreEndOfText ? textTaskNoEos : textTask)->CreateSession(configuration);
            chatSession = chatTask->CreateSession(textSession);
        }
        catch
        {
            CloseSessions();
            throw;
        }
        finally { Release(configuration); }
    }

    private unsafe void CloseSessions()
    {
        try { if (chatSession != null) chatSession->Close(); }
        finally
        {
            Release(chatSession); chatSession = null;
            try { if (textSession != null) textSession->Close(); }
            finally { Release(textSession); textSession = null; }
        }
    }

    private static unsafe SpeculativeStats? ReadSpeculativeStats(void* result)
    {
        if (result == null) return null;
        Guid iid = typeof(IWinMLTextGenerationSpeculativeResult).GUID;
        void* found = null;
        // Index 0 of every COM vtable is QueryInterface.
        HRESULT hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, HRESULT>)(*(void***)result)[0])(result, &iid, &found);
        if (hr.Failed || found == null) return null;
        var speculative = (IWinMLTextGenerationSpeculativeResult*)found;
        try
        {
            var stats = speculative->GetSpeculativeStatistics();
            return new(ShortName(stats.method, "_METHOD_"), stats.verificationStepCount, stats.draftTokenCount, stats.acceptedDraftTokenCount);
        }
        finally { Release(speculative); }
    }

    // Runs each prompt greedily as a Completion with speculation off, then with the loaded speculation, recreating the
    // sessions in between (never two at once). With ignoreEndOfText every prompt generates maxTokens tokens. Chat
    // history is cleared; the loaded speculation is restored at the end.
    public unsafe Task<IReadOnlyList<SpeculativeComparisonRow>> CompareSpeculationAsync(IReadOnlyList<BenchmarkPrompt> prompts, uint maxTokens,
        bool ignoreEndOfText, IProgress<string> progress, CancellationToken cancellation) => Run<IReadOnlyList<SpeculativeComparisonRow>>(() =>
    {
        if (textSession == null || textModel == null) throw new InvalidOperationException("Load a model first.");
        if (loadedSpeculation.Mode == SpeculativeMode.Off)
            throw new InvalidOperationException("Load the model with a speculative decoding method to compare it with Off.");
        var rows = new List<SpeculativeComparisonRow>();
        try
        {
            foreach (SpeculativeMode mode in new[] { SpeculativeMode.Off, loadedSpeculation.Mode })
            {
                progress.Report($"Preparing {Describe(mode)}...");
                CloseSessions();
                history.Clear();
                CreateSessions(mode, loadedSpeculation.DraftTokens, ignoreEndOfText: ignoreEndOfText);
                // One untimed warm-up so the first timed prompt is not paying for program loading.
                RunCompletion(prompts[0].Text, Math.Min(maxTokens, 16u), cancellation);
                for (int i = 0; i < prompts.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    progress.Report($"{Describe(mode)}: {prompts[i].Label} ({i + 1} of {prompts.Count})");
                    var result = RunCompletion(prompts[i].Text, maxTokens, cancellation);
                    rows.Add(new(prompts[i].Label, mode, result.Text, result.Timing!, result.Speculation, result.FinishReason, result.PromptTokens));
                }
            }
            return rows;
        }
        finally
        {
            CloseSessions();
            history.Clear();
            CreateSessions(loadedSpeculation.Mode, loadedSpeculation.DraftTokens);
        }
    });

    private unsafe GenerationResult RunCompletion(string prompt, uint maxTokens, CancellationToken cancellation)
    {
        IWinMLTextGenerationOptions* options = null;
        IWinMLCancellationSource* source = null;
        try
        {
            options = CreateGreedyOptions(maxTokens);
            source = tasks->CreateCancellationSource();
            nint sourceAddress = (nint)source;
            using var registration = cancellation.Register(() => ((IWinMLCancellationSource*)sourceAddress)->Cancel());
            var result = GenerateCompletion(prompt, options, source, NoProgress.Instance, Stopwatch.StartNew());
            if (result.Canceled) throw new OperationCanceledException(cancellation);
            return result;
        }
        finally { Release(source); Release(options); }
    }

    private sealed class NoProgress : IProgress<GenerationUpdate>
    {
        public static NoProgress Instance { get; } = new();
        public void Report(GenerationUpdate value) { }
    }
}
