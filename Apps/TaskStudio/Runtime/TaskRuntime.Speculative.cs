// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

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
    private ExecutionTarget? textTarget;
    private Model? textModel;
    private Model? textDraftModel;
    private uint? textContextTokens;
    private PrefillProfile textPrefill;
    private TextGenerationTask? textTask;
    private TextGenerationTask? textTaskNoEos;
    private ChatCompletionTask? chatTask;
    private SpeculativeChoice loadedSpeculation = SpeculativeChoice.Off;

    public SpeculativeChoice LoadedSpeculation => loadedSpeculation;
    public SpeculativeSupportReport? SpeculativeSupport { get; private set; }

    private static TextGenerationSpeculativeMethod ToMethod(SpeculativeMode mode) => mode switch
    {
        SpeculativeMode.PromptLookup => TextGenerationSpeculativeMethod.PromptLookup,
        SpeculativeMode.ModelLayers or SpeculativeMode.CompanionDrafter => TextGenerationSpeculativeMethod.Model,
        _ => TextGenerationSpeculativeMethod.None
    };

    public static string Describe(SpeculativeMode mode) => mode switch
    {
        SpeculativeMode.PromptLookup => "prompt lookup",
        SpeculativeMode.ModelLayers => "model draft layers (MTP)",
        SpeculativeMode.CompanionDrafter => "drafter model",
        _ => "off"
    };

    // Reads what the bound model supports. A path that reports only through CreateSession returns null entries.
    private static SpeculativeSupportReport ReadSpeculativeSupport(TextGenerationConfiguration configuration)
    {
        if (!configuration.TryAs<TextGenerationSpeculativeSupport>(out var support)) return new(null, null);
        using (support)
        {
            SpeculativeMethodSupport? Query(TextGenerationSpeculativeMethod method)
            {
                try
                {
                    var value = support.GetSpeculativeMethodSupport(method);
                    return new(value.Supported, EnumNames.Words(value.DraftSource), value.DefaultDraftTokenCount,
                        value.MaxDraftTokenCount, value.ModelDraftLayersPresent);
                }
                catch (Exception error) when (error.HResult == NotSupportedHResult) { return null; }
            }
            return new(Query(TextGenerationSpeculativeMethod.PromptLookup), Query(TextGenerationSpeculativeMethod.Model));
        }
    }

    private static void ApplySpeculation(TextGenerationConfiguration configuration, SpeculativeMode mode, uint draftTokens)
    {
        using var speculative = configuration.As<TextGenerationSpeculativeConfiguration>();
        speculative.SetSpeculativeOptions(new TextGenerationSpeculativeOptions
        {
            Method = ToMethod(mode),
            DraftTokenCount = mode == SpeculativeMode.Off ? 0 : draftTokens
        });
    }

    // Creates the text and chat sessions with the given speculation, from a configuration bound for it. The
    // configuration is consumed (released) whether or not the sessions are created.
    private void CreateSessions(SpeculativeMode mode, uint draftTokens, TextGenerationConfiguration? configuration = null,
        bool ignoreEndOfText = false)
    {
        // An EXPLICIT policy with no tokens never stops at end of text, so a benchmark generates its full count.
        if (ignoreEndOfText && textTaskNoEos == null)
            textTaskNoEos = tasks!.TextGeneration.CreateTextGenerationTask(tokenizer!, ReadOnlySpan<uint>.Empty);
        // Only the companion method takes the drafter; the Runtime refuses one with Off or prompt lookup.
        configuration ??= BindConfiguration(TextProgramCacheUsed, bindDrafter: mode == SpeculativeMode.CompanionDrafter);
        try
        {
            ApplySpeculation(configuration, mode, draftTokens);
            textSession = (ignoreEndOfText ? textTaskNoEos : textTask)!.CreateSession(configuration);
            chatSession = chatTask!.CreateSession(textSession);
        }
        catch
        {
            CloseSessions();
            throw;
        }
        finally { configuration.Dispose(); }
    }

    private void CloseSessions()
    {
        try { chatSession?.Close(); }
        finally
        {
            Release(ref chatSession);
            try { textSession?.Close(); }
            finally { Release(ref textSession); }
        }
    }

    private static SpeculativeStats? ReadSpeculativeStats(WinMLObject result)
    {
        if (!result.TryAs<TextGenerationSpeculativeResult>(out var speculative)) return null;
        using (speculative)
        {
            var stats = speculative.SpeculativeStatistics;
            return new(EnumNames.Words(stats.Method), stats.VerificationStepCount, stats.DraftTokenCount, stats.AcceptedDraftTokenCount);
        }
    }

    // Runs each prompt greedily as a Completion with speculation off, then with the loaded speculation, recreating the
    // sessions in between (never two at once). With ignoreEndOfText every prompt generates maxTokens tokens. Chat
    // history is cleared; the loaded speculation is restored at the end.
    public Task<IReadOnlyList<SpeculativeComparisonRow>> CompareSpeculationAsync(IReadOnlyList<BenchmarkPrompt> prompts, uint maxTokens,
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
                RunCompletion(prompts[0], Math.Min(maxTokens, 16u), cancellation);
                for (int i = 0; i < prompts.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    progress.Report($"{Describe(mode)}: {prompts[i].Label} ({i + 1} of {prompts.Count})");
                    var result = RunCompletion(prompts[i], maxTokens, cancellation);
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

    private GenerationResult RunCompletion(BenchmarkPrompt prompt, uint maxTokens, CancellationToken cancellation)
    {
        using TextGenerationOptions options = CreateGreedyOptions(maxTokens);
        using CancellationSource source = tasks!.CreateCancellationSource();
        using var registration = cancellation.Register(source.Cancel);
        try
        {
            var result = GenerateCompletion(prompt.Text, options, source, NoProgress.Instance, Stopwatch.StartNew(), prompt.TokenIds);
            if (result.Canceled) throw new OperationCanceledException(cancellation);
            return result;
        }
        catch (Exception error) when (error.HResult == ContextWindowExceeded) { throw ContextWindowError(error); }
    }

    private sealed class NoProgress : IProgress<GenerationUpdate>
    {
        public static NoProgress Instance { get; } = new();
        public void Report(GenerationUpdate value) { }
    }
}
