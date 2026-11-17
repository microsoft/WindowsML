// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace TaskStudio.Runtime;

// One llama-bench style test: pp<N> evaluates N prompt tokens, tg<N> generates N tokens,
// optionally after Depth tokens already in the sequence (llama-bench -d).
internal sealed record BenchmarkTest(uint PromptTokens, uint GeneratedTokens, uint Depth)
{
    public string Name => (PromptTokens > 0 ? $"pp{PromptTokens}" : $"tg{GeneratedTokens}") + (Depth > 0 ? $" @ d{Depth}" : "");
}

// Times run from the test's GenerateTokens/ContinueTokens call; depth tokens are evaluated before it, untimed.
internal sealed record BenchmarkSample(BenchmarkTest Test, uint GeneratedTokens, TimeSpan FirstToken, TimeSpan LastToken, string FinishReason, bool Canceled)
{
    // pp: prompt tokens over time to the first token. tg: tokens over time to the last token (one decode step each).
    public double TokensPerSecond => Test.PromptTokens > 0
        ? Test.PromptTokens / FirstToken.TotalSeconds
        : GeneratedTokens / LastToken.TotalSeconds;
    public TimeSpan Duration => Test.PromptTokens > 0 ? FirstToken : LastToken;
}

internal sealed partial class TaskRuntime
{
    private const string BenchmarkText =
        "The lighthouse keeper climbed the spiral stairs each evening, counting steps and listening to the sea. " +
        "Ships passed far offshore, their lights blinking like slow stars, while gulls settled on the rocks below. " +
        "In the morning she wrote the weather in a leather book, mended nets and watched the tide uncover the reef. ";

    public Task<BenchmarkSample> RunBenchmarkAsync(BenchmarkTest test, CancellationToken cancellation) => Run(() =>
    {
        if (textSession == null || tokenizer == null) throw new InvalidOperationException("Create task sessions first.");
        // tg starts from a one-token prompt, as llama-bench does.
        uint promptCount = Math.Max(test.PromptTokens, 1);
        uint[] tokens = SyntheticTokens(test.Depth + promptCount);
        using CancellationSource source = tasks!.CreateCancellationSource();
        try
        {
            using var registration = cancellation.Register(source.Cancel);
            textSession.Reset();
            // Greedy, so every repetition does the same work.
            using TextGenerationOptions options = CreateGreedyOptions(test.PromptTokens > 0 ? 1 : test.GeneratedTokens);
            if (test.Depth > 0)
            {
                using TextGenerationOptions primeOptions = CreateGreedyOptions(1);
                using (TextGenerationPullStream prime = textSession.GenerateTokens(tokens.AsSpan(0, (int)test.Depth), primeOptions, source))
                {
                    try { DrainUntimed(prime); }
                    finally { prime.Close(); }
                }
                if (cancellation.IsCancellationRequested)
                    return new BenchmarkSample(test, 0, TimeSpan.Zero, TimeSpan.Zero, "CANCELED", true);
            }
            long start = Stopwatch.GetTimestamp();
            ReadOnlySpan<uint> testTokens = tokens.AsSpan((int)test.Depth, (int)promptCount);
            using TextGenerationPullStream stream = test.Depth > 0
                ? textSession.ContinueTokens(testTokens, options, source)
                : textSession.GenerateTokens(testTokens, options, source);
            TimeSpan? first = null, last = null;
            uint generated = 0;
            while (stream.TryReadNext(out _, out _))
            {
                last = Stopwatch.GetElapsedTime(start);
                first ??= last;
                generated++;
            }
            using TextGenerationResult result = stream.GetResult();
            var reason = result.FinishReason;
            bool canceled = reason == TextGenerationFinishReason.Canceled;
            if (!canceled) ThrowOnError(result.ErrorCode);
            var sample = new BenchmarkSample(test, generated, first ?? TimeSpan.Zero, last ?? TimeSpan.Zero,
                EnumNames.Constant(reason), canceled || first is null);
            stream.Close();
            return sample;
        }
        catch (Exception error)
        {
            textSession.Reset();
            if (error.HResult == ContextWindowExceeded) throw ContextWindowError(error);
            throw;
        }
    });

    private TextGenerationOptions CreateGreedyOptions(uint maxNewTokens) =>
        tasks!.TextGeneration.CreateTextGenerationOptions(new TextGenerationOptionValues { MaxNewTokens = maxNewTokens, Temperature = 0 });

    private static void DrainUntimed(TextGenerationPullStream stream)
    {
        while (stream.TryReadNext(out _, out _)) { }
        using TextGenerationResult result = stream.GetResult();
        if (result.FinishReason != TextGenerationFinishReason.Canceled) ThrowOnError(result.ErrorCode);
    }

    // Ordinary text tokens from the model's own tokenizer, repeated to the requested length.
    private uint[] SyntheticTokens(uint count)
    {
        uint[] source = tokenizer!.Encode(BenchmarkText);
        if (source.Length == 0) throw new InvalidOperationException("The tokenizer produced no tokens for the benchmark text.");
        var tokens = new uint[count];
        for (int i = 0; i < tokens.Length; i++) tokens[i] = source[i % source.Length];
        return tokens;
    }
}
