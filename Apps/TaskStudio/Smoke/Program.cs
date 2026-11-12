// Copyright (C) Microsoft Corporation. All rights reserved.

using TaskStudio.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;

if (args.Length >= 2 && args[0] == "--bench")
{
    await using var bench = new TaskRuntime();
    var gpu = (await bench.GetGpuDevicesAsync()).First();
    await bench.LoadAsync(args[1], args.Length > 2 ? args[2] : "", null, gpu);
    Console.WriteLine($"| model | backend | test | t/s | ms |\n| --- | --- | --- | ---: | ---: |");
    foreach (var test in new BenchmarkTest[] { new(512, 0, 0), new(0, 128, 0), new(512, 0, 512), new(0, 128, 512) })
    {
        await bench.RunBenchmarkAsync(test, CancellationToken.None);
        var samples = new List<BenchmarkSample>();
        for (int i = 0; i < 3; i++) samples.Add(await bench.RunBenchmarkAsync(test, CancellationToken.None));
        if (samples.Any(s => s.Canceled || s.GeneratedTokens == 0)) throw new InvalidOperationException($"{test.Name} produced no tokens.");
        double mean = samples.Average(s => s.TokensPerSecond);
        double sd = Math.Sqrt(samples.Sum(s => Math.Pow(s.TokensPerSecond - mean, 2)) / (samples.Count - 1));
        Console.WriteLine($"| {Path.GetFileName(args[1])} | CGC {gpu.Name} | {test.Name} | {mean:F2} \u00B1 {sd:F2} | " +
            $"{samples.Average(s => s.Duration.TotalMilliseconds):F1} |" + (test.GeneratedTokens > 0 ? $" generated {samples[0].GeneratedTokens}, {samples[0].FinishReason}" : ""));
    }
    Console.WriteLine("PASS: llama-bench style pp/tg tests, including depth via ContinueTokens.");
    return 0;
}

if (args.Length == 2 && args[0] == "--conversation")
    return ConversationSmoke.Run(args[1]);
// --tokparity <model.gguf> <publisher tokenizer_config.json>
if (args.Length == 3 && args[0] == "--tokparity")
    return TokenizerParitySmoke.Run(args[1], args[2]);
// --encode <tokenizer_config.json or .gguf> <text>: prints the token IDs without special tokens.
if (args.Length == 3 && args[0] == "--encode")
    return TokenizerParitySmoke.PrintIds(args[1], args[2]);

if (args.Length == 1 && args[0] == "--defaults")
    return GenerationDefaultsSmoke.Run();

if (args.Length == 1 && args[0] == "--devices")
{
    await using var probe = new TaskRuntime();
    var available = await probe.GetGpuDevicesAsync();
    foreach (var gpu in available) Console.WriteLine($"{gpu.Name} / {gpu.Identity} / NVIDIA={gpu.IsNvidia}");
    if (available.Count == 0) throw new InvalidOperationException("No hardware GPU was found.");
    return 0;
}

if (args.Length > 0 && args[0] == "--audio")
    return await SpeechSmoke.CheckAudioAsync(args.Length > 1 && args[1] == "--microphone");
if (args.Length == 4 && args[0] == "--speech")
    return await SpeechSmoke.CheckRecognitionAsync(args[1], args[2], args[3]);
if (args.Length == 2 && args[0] == "--decision")
    return await DecisionSmoke.RunAsync(args[1]);
// --spec <model> <tokenizer_config.json or ""> <lookup|layers|companion> [drafter] [--context N] [--tokens N] [--no-eos]
if (args.Length >= 4 && args[0] == "--spec")
{
    var mode = args[3] switch
    {
        "lookup" => SpeculativeMode.PromptLookup,
        "layers" => SpeculativeMode.ModelLayers,
        "companion" => SpeculativeMode.CompanionDrafter,
        _ => throw new ArgumentException("Method must be lookup, layers or companion.")
    };
    var rest = args.Skip(4).ToList();
    string? drafter = rest.Count > 0 && !rest[0].StartsWith("--", StringComparison.Ordinal) ? rest[0] : null;
    uint Option(string name, uint fallback) => rest.IndexOf(name) is int at and >= 0 ? uint.Parse(rest[at + 1]) : fallback;
    return await SpeculativeSmoke.RunAsync(args[1], args[2], mode, drafter, Option("--context", 0), Option("--tokens", 128), rest.Contains("--no-eos"));
}

// A GGUF file may omit the tokenizer argument and use the file's own tokenizer.
if (args.Length == 1 && TaskRuntime.IsGgufFile(args[0]))
    args = [args[0], ""];

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Smoke <model-directory> <tokenizer_config.json> | <model.gguf> [tokenizer_config.json] | --conversation <tokenizer_config.json> | --defaults | --devices | --audio [--microphone] | --speech <model-directory> <tokenizer_config.json> <audio-file> | --decision <model-directory>");
    return 2;
}

await using var runtime = new TaskRuntime();
var devices = await runtime.GetGpuDevicesAsync();
var device = devices.First();
Console.WriteLine($"Selected GPU: {device.Name} ({device.Identity}), CGC backend.");
var actual = await runtime.LoadAsync(args[0], args[1], 2048, device);
if (actual.Identity != device.Identity) throw new InvalidOperationException("The Runtime target did not use the selected GPU.");
Console.WriteLine($"Actual Runtime GPU: {actual.Name} ({actual.Identity}).");
var updates = new InlineProgress();
var modelDefaults = GenerationDefaults.Load(args[0]);
var recipe = await runtime.GenerateAsync(true, "Give me a banana bread recipe please", "You are a helpful assistant.",
    modelDefaults.Settings with { MaxTokens = 64 }, modelDefaults.StopTokenIds, updates, CancellationToken.None);
if (string.IsNullOrWhiteSpace(recipe.Text) || recipe.Canceled)
    throw new InvalidOperationException("Chat with the model's template and generation defaults returned no response.");
Console.WriteLine("\nPASS: banana bread Chat request with model defaults and the default conversation formatter.");
await runtime.ResetAsync();
var configured = await runtime.GenerateAsync(false, "The three primary colors are", "",
    modelDefaults.Settings with { MaxTokens = 16 }, modelDefaults.StopTokenIds, updates, CancellationToken.None);
if (string.IsNullOrWhiteSpace(configured.Text) || configured.Canceled)
    throw new InvalidOperationException("Generation using model defaults failed.");
Console.WriteLine($"\nPASS: generation using {modelDefaults.Source}.");
foreach (bool chat in new[] { false, true, true })
{
    var result = await runtime.GenerateAsync(chat,
        chat ? "Name three primary colors. Reply briefly." : "The three primary colors are",
        "You are a helpful assistant.", new(32, 0), [], updates, CancellationToken.None);
    if (string.IsNullOrWhiteSpace(result.Text) || result.Canceled || result.PromptTokens == 0)
        throw new InvalidOperationException("Generation did not produce a successful nonempty result.");
    var timing = result.Timing ?? throw new InvalidOperationException("Generation returned no timing.");
    if (timing.GeneratedTokens == 0 || timing.TtftMs is not > 0 || timing.Total.TotalMilliseconds < timing.TtftMs)
        throw new InvalidOperationException("Generation timing is inconsistent.");
    if (!chat && timing.GeneratedTokens > 1 && timing.DecodeTokensPerSecond is not > 0)
        throw new InvalidOperationException("Completion timing has no decode rate.");
    if (chat && timing.Cache is null) throw new InvalidOperationException("Chat result reported no prompt cache use.");
    Console.WriteLine($"\n{result.FinishReason}: {result.PromptTokens} prompt tokens, {timing.GeneratedTokens} generated, " +
        $"TTFT {timing.TtftMs:F1} ms, decode {timing.DecodeTokensPerSecond:F1} tok/s" +
        (timing.Cache is { } cache ? $", cache {cache.Mode} {cache.CachedTokens}/{cache.PromptTokens}" : ""));
}
Console.WriteLine($"PASS: app-measured timing. GPU memory in use: {await runtime.GetGpuMemoryUsageAsync(device) / (1 << 20):N0} MiB.");
await runtime.ResetAsync();
using (var cancelDuringStream = new CancellationTokenSource())
{
    var cancelingProgress = new InlineProgress(() => cancelDuringStream.Cancel());
    // Chat parsers may buffer deltas, so cancel on the unbuffered Text stream.
    var result = await runtime.GenerateAsync(false, "Count from one to one hundred.", "", new(128, 0), [],
        cancelingProgress, cancelDuringStream.Token);
    if (!result.Canceled) throw new InvalidOperationException("Mid-stream cancellation did not stop generation.");
}
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    var result = await runtime.GenerateAsync(true, "Hello", "", new(16, 0), [], updates, canceled.Token);
    if (!result.Canceled) throw new InvalidOperationException("Pre-canceled generation did not report cancellation.");
}
var afterCancel = await runtime.GenerateAsync(true, "Say hello.", "", new(16, 0), [], updates, CancellationToken.None);
if (afterCancel.Canceled || string.IsNullOrWhiteSpace(afterCancel.Text))
    throw new InvalidOperationException("The session did not recover after cancellation.");

foreach (var invalid in new GenerationSettings[]
{
    new(TopP: 0), new(TopP: float.NaN), new(MinP: -0.1f), new(MinP: 1.1f),
    new(RepetitionPenalty: 0), new(PresencePenalty: float.PositiveInfinity),
    new(FrequencyPenalty: float.NaN), new(MaxTokens: 0)
})
{
    bool rejected = false;
    try { invalid.Validate(); }
    catch (ArgumentException) { rejected = true; }
    if (!rejected) throw new InvalidOperationException($"Invalid generation settings were accepted: {invalid}");
}
foreach (bool chat in new[] { false, true })
{
    var tuned = new GenerationSettings(16, 0.7f, TopK: 20, TopP: 0.9f, MinP: 0.05f,
        RepetitionPenalty: 1.1f, RepetitionWindow: 64, PresencePenalty: 0.2f,
        FrequencyPenalty: 0.1f, Seed: ulong.MaxValue);
    // A fixed seed repeats the random draws, but a GPU can compute slightly different scores, which can change
    // the sampled token at a near tie. Two of three runs must match, which an ignored seed almost never does.
    var texts = new List<string>();
    for (int run = 0; run < 3; run++)
    {
        await runtime.ResetAsync();
        var seeded = await runtime.GenerateAsync(chat, "Explain what a GPU is in a sentence.", "", tuned, [], updates, CancellationToken.None);
        if (seeded.Canceled || string.IsNullOrWhiteSpace(seeded.Text))
            throw new InvalidOperationException("Seeded advanced sampling did not complete.");
        texts.Add(seeded.Text);
    }
    int distinct = texts.Distinct(StringComparer.Ordinal).Count();
    if (distinct == texts.Count)
        throw new InvalidOperationException("Seeded advanced sampling was not reproducible after reset.");
    if (distinct > 1)
        Console.WriteLine($"\nWARN: one of three seeded {(chat ? "Chat" : "Completion")} runs differed (a near tie can sample differently).");
    Console.WriteLine($"\nPASS: advanced sampling and full-width seed in {(chat ? "Chat" : "Completion")}.");
}
const string stopPrompt = "The three primary colors are";
var firstToken = await runtime.GenerateAsync(false, stopPrompt, "", new(1, 0), [], updates, CancellationToken.None);
// A GGUF file holds its own tokenizer, so the helper can read it from the model file.
uint[] stopIds = EncodeStopIds(string.IsNullOrEmpty(args[1]) ? args[0] : args[1], firstToken.Text);
if (stopIds.Length == 0) throw new InvalidOperationException("Stop-token test could not encode the first output token.");
var stopped = await runtime.GenerateAsync(false, stopPrompt, "", new(16, 0), stopIds, updates, CancellationToken.None);
if (!stopped.FinishReason.EndsWith("_STOP_TOKEN", StringComparison.Ordinal) || stopped.Canceled)
    throw new InvalidOperationException("Configured stop-token IDs did not stop generation.");
Console.WriteLine("\nPASS: explicit stop-token IDs and invalid option rejection.");
await runtime.UnloadAsync();
await runtime.LoadAsync(args[0], args[1], 2048, device);
Console.WriteLine("PASS: completion, two chat turns, reset, mid-stream/pre-cancellation, recovery, unload/reload.");
return 0;

static unsafe uint[] EncodeStopIds(string tokenizerPath, string text)
{
    IWinMLTokenizer* tokenizer = null;
    uint* ids = null;
    try
    {
        PInvoke.WinMLCreateTokenizerFromFile(tokenizerPath, &tokenizer).ThrowOnFailure();
        uint count;
        tokenizer->Encode(text, (WINML_TOKENIZER_ENCODE_FLAGS)0, out count, &ids);
        return new ReadOnlySpan<uint>(ids, checked((int)count)).ToArray();
    }
    finally
    {
        Marshal.FreeCoTaskMem((nint)ids);
        if (tokenizer != null) tokenizer->Release();
    }
}

sealed class InlineProgress : IProgress<GenerationUpdate>
{
    private readonly Action? onUpdate;
    public InlineProgress(Action? onUpdate = null) => this.onUpdate = onUpdate;
    public void Report(GenerationUpdate value)
    {
        Console.Write(value.Text);
        onUpdate?.Invoke();
    }
}
