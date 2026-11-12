// Copyright (C) Microsoft Corporation. All rights reserved.

using TaskStudio.Runtime;

// Checks speculative decoding through the Task API: what the Runtime reports for the model, that the loaded method
// actually verifies drafts, and an Off vs speculative comparison on the same greedy prompts.
internal static class SpeculativeSmoke
{
    public static async Task<int> RunAsync(string modelPath, string tokenizerPath, SpeculativeMode mode, string? drafterPath,
        uint contextTokens = 0, uint maxTokens = 128, bool ignoreEndOfText = false)
    {
        await using var runtime = new TaskRuntime();
        var device = (await runtime.GetGpuDevicesAsync()).First();
        var choice = new SpeculativeChoice(mode, drafterPath);
        // The window holds the coding context plus the generated tokens.
        uint window = Math.Max(4096u, contextTokens + maxTokens + 1024);
        await runtime.LoadAsync(modelPath, tokenizerPath, window, device, choice);
        var support = runtime.SpeculativeSupport;
        Console.WriteLine($"Model: {Path.GetFileName(modelPath.TrimEnd('\\'))}  method: {TaskRuntime.Describe(mode)}" +
            (drafterPath is null ? "" : $"  drafter: {Path.GetFileName(drafterPath.TrimEnd('\\'))}"));
        Console.WriteLine($"Support: prompt lookup {Format(support?.PromptLookup)}; model {Format(support?.Model)}");

        var chat = await runtime.GenerateAsync(true, "Name three primary colors.", "", new GenerationSettings(MaxTokens: 48),
            [], new Progress<GenerationUpdate>(), CancellationToken.None);
        Console.WriteLine($"Chat: {chat.Text.Trim().Replace('\n', ' ')}");
        Console.WriteLine($"Chat speculation: {chat.Speculation?.Describe() ?? "not reported by the Chat result"}");

        IReadOnlyList<BenchmarkPrompt> prompts = CodingBenchmark.ShortPrompts;
        if (contextTokens > 0)
        {
            (prompts, uint promptTokens) = await runtime.BuildCodingPromptsAsync(contextTokens);
            Console.WriteLine($"Coding prompts: {contextTokens:N0}-token target, longest {promptTokens:N0} tokens; generate {maxTokens:N0}" +
                (ignoreEndOfText ? " (end of text ignored)" : ""));
        }
        var rows = await runtime.CompareSpeculationAsync(prompts, maxTokens, ignoreEndOfText, new Progress<string>(), CancellationToken.None);
        Console.WriteLine("| prompt | prompt tok | method | generated | decode t/s | drafted | accepted | accept % | tokens/step |");
        Console.WriteLine("| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            var s = row.Stats;
            Console.WriteLine($"| {row.Prompt} | {row.PromptTokens:N0} | {TaskRuntime.Describe(row.Mode)} | {row.Timing.GeneratedTokens} | " +
                $"{row.Timing.DecodeTokensPerSecond:F1} | {s?.DraftTokens} | {s?.AcceptedTokens} | {s?.AcceptRate:P0} | {s?.TokensPerStep:F2} |");
        }

        int failures = 0;
        var off = rows.Where(r => r.Mode == SpeculativeMode.Off).ToList();
        var on = rows.Where(r => r.Mode == mode).ToList();
        if (on.Any(r => r.Stats is not { Active: true }))
        {
            Console.WriteLine("FAIL: the speculative run reported no verification steps.");
            failures++;
        }
        if (off.Any(r => r.Stats is { Active: true }))
        {
            Console.WriteLine("FAIL: the Off run reported verification steps.");
            failures++;
        }
        int same = off.Zip(on).Count(pair => pair.First.Text == pair.Second.Text);
        Console.WriteLine($"{(same == off.Count ? "PASS" : "WARN")}: identical greedy text on {same} of {off.Count} prompts " +
            "(a multi-position step can pick differently on a near tie).");
        foreach (var (a, b) in off.Zip(on).Where(pair => pair.First.Text != pair.Second.Text))
            Console.WriteLine($"  {a.Prompt}: same for the first {TextComparison.CommonPrefix(a.Text, b.Text):N0} of {a.Text.Length:N0} characters.");
        double offRate = off.Sum(r => r.Timing.GeneratedTokens) / off.Sum(r => (r.Timing.DecodeMs ?? 0) / 1000);
        double onRate = on.Sum(r => r.Timing.GeneratedTokens) / on.Sum(r => (r.Timing.DecodeMs ?? 0) / 1000);
        Console.WriteLine($"Overall decode: off {offRate:F1} t/s, {TaskRuntime.Describe(mode)} {onRate:F1} t/s ({onRate / offRate:F2}x).");

        // The counting prompt repeats itself from the first step, so every method drafts within a few tokens.
        var after = await runtime.GenerateAsync(false, CodingBenchmark.ShortPrompts[2].Text, "", new GenerationSettings(MaxTokens: 32),
            [], new Progress<GenerationUpdate>(), CancellationToken.None);
        if (after.Speculation is not { Active: true })
        {
            Console.WriteLine($"FAIL: the loaded method was not restored after the comparison ({after.Speculation?.Describe() ?? "no statistics"}).");
            failures++;
        }
        if (failures == 0) Console.WriteLine("PASS: speculative decoding load, statistics and Off comparison.");
        return failures == 0 ? 0 : 1;
    }

    private static string Format(SpeculativeMethodSupport? support) => support is null ? "not reported"
        : $"{(support.Supported ? "supported" : "unsupported")} (source {support.DraftSource}, default {support.DefaultDraftTokens}, " +
          $"max {support.MaxDraftTokens}, draft layers {(support.ModelDraftLayersPresent ? "yes" : "no")})";
}
