// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;
using static Microsoft.Windows.AI.MachineLearning.Runtime.Tasks.WINML_TEXT_GENERATION_OPTION_FIELDS;

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

    public unsafe Task<BenchmarkSample> RunBenchmarkAsync(BenchmarkTest test, CancellationToken cancellation) => Run(() =>
    {
        if (textSession == null || tokenizer == null) throw new InvalidOperationException("Create task sessions first.");
        // tg starts from a one-token prompt, as llama-bench does.
        uint promptCount = Math.Max(test.PromptTokens, 1);
        uint[] tokens = SyntheticTokens(test.Depth + promptCount);
        IWinMLTextGenerationOptions* primeOptions = null;
        IWinMLTextGenerationOptions* options = null;
        IWinMLCancellationSource* source = tasks->CreateCancellationSource();
        IWinMLTextGenerationPullStream* stream = null;
        IWinMLTextGenerationResult* result = null;
        try
        {
            nint sourceAddress = (nint)source;
            using var registration = cancellation.Register(() => ((IWinMLCancellationSource*)sourceAddress)->Cancel());
            textSession->Reset();
            // Greedy, so every repetition does the same work.
            options = CreateGreedyOptions(test.PromptTokens > 0 ? 1 : test.GeneratedTokens);
            if (test.Depth > 0)
            {
                primeOptions = CreateGreedyOptions(1);
                fixed (uint* depthTokens = tokens)
                    stream = textSession->GenerateTokens(test.Depth, depthTokens, primeOptions, source);
                try { DrainUntimed(stream); }
                finally { stream->Close(); Release(stream); stream = null; }
                if (cancellation.IsCancellationRequested)
                    return new BenchmarkSample(test, 0, TimeSpan.Zero, TimeSpan.Zero, "CANCELED", true);
            }
            long start = Stopwatch.GetTimestamp();
            fixed (uint* testTokens = &tokens[test.Depth])
            {
                stream = test.Depth > 0
                    ? textSession->ContinueTokens(promptCount, testTokens, options, source)
                    : textSession->GenerateTokens(promptCount, testTokens, options, source);
            }
            TimeSpan? first = null, last = null;
            uint generated = 0;
            while (true)
            {
                WINML_TEXT_GENERATION_READ_STATUS status;
                uint token;
                PCWSTR fragment;
                stream->ReadNext(&status, &token, &fragment);
                if (status == WINML_TEXT_GENERATION_READ_STATUS.WINML_TEXT_GENERATION_READ_STATUS_COMPLETED) break;
                last = Stopwatch.GetElapsedTime(start);
                first ??= last;
                generated++;
            }
            result = stream->GetResult();
            var reason = result->GetFinishReason();
            bool canceled = reason == WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_CANCELED;
            if (!canceled) result->GetErrorCode().ThrowOnFailure();
            string name = reason.ToString();
            return new BenchmarkSample(test, generated, first ?? TimeSpan.Zero, last ?? TimeSpan.Zero,
                name[(name.LastIndexOf("_REASON_", StringComparison.Ordinal) + 8)..], canceled || first is null);
        }
        catch
        {
            textSession->Reset();
            throw;
        }
        finally
        {
            try { if (stream != null) stream->Close(); }
            finally { Release(result); Release(stream); Release(options); Release(primeOptions); Release(source); }
        }
    });

    private unsafe IWinMLTextGenerationOptions* CreateGreedyOptions(uint maxNewTokens)
    {
        var values = new WINML_TEXT_GENERATION_OPTIONS
        {
            presentFields = WINML_TEXT_GENERATION_OPTION_FIELD_MAX_NEW_TOKENS | WINML_TEXT_GENERATION_OPTION_FIELD_TEMPERATURE,
            maxNewTokens = maxNewTokens,
            temperature = 0
        };
        return textFactory->CreateTextGenerationOptions(&values, 0, null);
    }

    private static unsafe void DrainUntimed(IWinMLTextGenerationPullStream* stream)
    {
        WINML_TEXT_GENERATION_READ_STATUS status;
        do
        {
            uint token;
            PCWSTR fragment;
            stream->ReadNext(&status, &token, &fragment);
        }
        while (status != WINML_TEXT_GENERATION_READ_STATUS.WINML_TEXT_GENERATION_READ_STATUS_COMPLETED);
        IWinMLTextGenerationResult* result = stream->GetResult();
        try
        {
            if (result->GetFinishReason() != WINML_TEXT_GENERATION_FINISH_REASON.WINML_TEXT_GENERATION_FINISH_REASON_CANCELED)
                result->GetErrorCode().ThrowOnFailure();
        }
        finally { Release(result); }
    }

    // Ordinary text tokens from the model's own tokenizer, repeated to the requested length.
    private unsafe uint[] SyntheticTokens(uint count)
    {
        uint* ids = null;
        try
        {
            uint encoded;
            tokenizer->Encode(BenchmarkText, (WINML_TOKENIZER_ENCODE_FLAGS)0, out encoded, &ids);
            if (encoded == 0) throw new InvalidOperationException("The tokenizer produced no tokens for the benchmark text.");
            var source = new ReadOnlySpan<uint>(ids, checked((int)encoded));
            var tokens = new uint[count];
            for (int i = 0; i < tokens.Length; i++) tokens[i] = source[i % source.Length];
            return tokens;
        }
        finally { Marshal.FreeCoTaskMem((nint)ids); }
    }
}
