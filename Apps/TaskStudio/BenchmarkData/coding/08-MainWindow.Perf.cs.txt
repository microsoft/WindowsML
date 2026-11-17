// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskStudio.Runtime;
using Windows.ApplicationModel.DataTransfer;

namespace TaskStudio;

public sealed class PerfRunItem
{
    public string Time { get; set; } = "";
    public string Source { get; set; } = "";
    public string Model { get; set; } = "";
    public string PromptTokens { get; set; } = "";
    public string GeneratedTokens { get; set; } = "";
    public string Ttft { get; set; } = "";
    public string Decode { get; set; } = "";
    public string MsPerToken { get; set; } = "";
    public string Total { get; set; } = "";
    public string Note { get; set; } = "";
    internal string Csv { get; set; } = "";
}

public sealed class BenchmarkRowItem
{
    public string Model { get; set; } = "";
    public string Test { get; set; } = "";
    public string Rate { get; set; } = "";
    public string Milliseconds { get; set; } = "";
    public string Note { get; set; } = "";
    internal string Markdown { get; set; } = "";
}

public sealed partial class MainWindow
{
    private bool IsPerf => TaskMode.SelectedIndex == 4;
    private CancellationTokenSource? benchmarkCancellation;
    private TimeSpan? lastLoadTime;
    private string loadedContext = "";
    public ObservableCollection<PerfRunItem> PerfRuns { get; } = new();

    private static string Ms(double? value) => value is double ms ? ms.ToString(ms < 100 ? "F1" : "F0", CultureInfo.CurrentCulture) : "-";
    private static string Rate(double? value) => value is double rate ? rate.ToString("F1", CultureInfo.CurrentCulture) : "-";

    private void RecordPerf(string source, GenerationResult result)
    {
        if (result.Timing is not { } timing) return;
        string model = System.IO.Path.GetFileName(ModelPath.Text.Trim().TrimEnd('\\'));
        string note = FriendlyFinishReason(result.FinishReason);
        // The model-backed route reports UNKNOWN; show cache use only when the Runtime reports it.
        if (timing.Cache is { Mode: not "unknown" } cache)
            note += $" / cache {cache.Mode}, {cache.CachedTokens:N0} of {cache.PromptTokens:N0} reused";
        if (!timing.TokenEvents && timing.DecodeTokensPerSecond is null && timing.GeneratedTokens > 1)
            note += " / text arrived in one delta";
        note += SpeculationNote(result.Speculation);
        double overall = timing.GeneratedTokens / Math.Max(timing.Total.TotalSeconds, 1e-9);
        var item = new PerfRunItem
        {
            Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            Source = source,
            Model = model,
            PromptTokens = result.PromptTokens.ToString("N0", CultureInfo.CurrentCulture),
            GeneratedTokens = timing.GeneratedTokens.ToString("N0", CultureInfo.CurrentCulture),
            Ttft = Ms(timing.TtftMs),
            Decode = Rate(timing.DecodeTokensPerSecond),
            MsPerToken = Ms(timing.MsPerToken),
            Total = $"{Ms(timing.Total.TotalMilliseconds)} ({Rate(overall)} tok/s)",
            Note = note,
            Csv = string.Join(',', DateTime.Now.ToString("O", CultureInfo.InvariantCulture), source, model, result.PromptTokens,
                timing.GeneratedTokens, F(timing.TtftMs), F(timing.DecodeTokensPerSecond), F(timing.MsPerToken),
                F(timing.Total.TotalMilliseconds), F(overall), result.FinishReason, timing.Cache?.Mode ?? "",
                timing.Cache?.CachedTokens.ToString(CultureInfo.InvariantCulture) ?? "",
                result.Speculation?.Method ?? "", result.Speculation?.VerificationSteps.ToString(CultureInfo.InvariantCulture) ?? "",
                result.Speculation?.DraftTokens.ToString(CultureInfo.InvariantCulture) ?? "",
                result.Speculation?.AcceptedTokens.ToString(CultureInfo.InvariantCulture) ?? "", F(result.Speculation?.AcceptRate))
        };
        PerfRuns.Insert(0, item);
        PerfHistoryEmpty.Visibility = Visibility.Collapsed;
        static string F(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "";
    }

    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !loaded) return;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        benchmarkCancellation = cancellation;
        StopBenchmarkButton.Visibility = Visibility.Visible;
        StopBenchmarkButton.IsEnabled = true;
        try
        {
            static uint Tag(ComboBox box) => uint.Parse((string)((ComboBoxItem)box.SelectedItem).Tag, CultureInfo.InvariantCulture);
            uint pp = Tag(BenchmarkPromptTokens), tg = Tag(BenchmarkTokens), depth = Tag(BenchmarkDepth);
            int runs = (int)Tag(BenchmarkRuns), warmups = (int)Tag(BenchmarkWarmup);
            var tests = new List<BenchmarkTest>();
            if (pp > 0) tests.Add(new BenchmarkTest(pp, 0, depth));
            if (tg > 0) tests.Add(new BenchmarkTest(0, tg, depth));
            if (tests.Count == 0) throw new ArgumentException("Choose a prompt (pp) or generate (tg) size.");
            string model = System.IO.Path.GetFileName(ModelPath.Text.Trim().TrimEnd('\\'));
            BenchmarkRows.Clear();
            benchmarkResults.Clear();
            foreach (var test in tests)
            {
                var samples = new List<BenchmarkSample>();
                for (int run = 1; run <= warmups + runs && !cancellation.IsCancellationRequested; run++)
                {
                    bool warmup = run <= warmups;
                    SessionStatus.Text = warmup ? $"{test.Name}: warm-up {run} of {warmups}..." : $"{test.Name}: run {run - warmups} of {runs}...";
                    var sample = await runtime.RunBenchmarkAsync(test, cancellation.Token);
                    if (sample.Canceled) break;
                    RecordSample(warmup ? $"{test.Name} warm-up" : $"{test.Name} {run - warmups}/{runs}", model, sample);
                    if (!warmup) samples.Add(sample);
                }
                if (samples.Count > 0) AddBenchmarkResult(model, test, samples);
            }
            if (benchmarkResults.Count == 0)
            {
                ShowStatus("Benchmark stopped", "No measured runs completed.", InfoBarSeverity.Informational);
                return;
            }
            ShowBenchmarkSummary(runs, warmups);
            await RefreshGpuMemoryAsync();
            ShowStatus(cancellation.IsCancellationRequested ? "Benchmark stopped" : "Benchmark complete",
                "Times are measured by the app on the Task stream.", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            benchmarkCancellation = null;
            StopBenchmarkButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void RecordSample(string source, string model, BenchmarkSample sample)
    {
        bool pp = sample.Test.PromptTokens > 0;
        var item = new PerfRunItem
        {
            Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            Source = source,
            Model = model,
            PromptTokens = ((pp ? sample.Test.PromptTokens : 1) + sample.Test.Depth).ToString("N0", CultureInfo.CurrentCulture),
            GeneratedTokens = sample.GeneratedTokens.ToString("N0", CultureInfo.CurrentCulture),
            Ttft = Ms(sample.FirstToken.TotalMilliseconds),
            Decode = pp ? "-" : Rate(sample.TokensPerSecond),
            MsPerToken = pp ? "-" : Ms(sample.LastToken.TotalMilliseconds / Math.Max(sample.GeneratedTokens, 1)),
            Total = $"{Ms(sample.LastToken.TotalMilliseconds)} ({Rate(sample.TokensPerSecond)} t/s)",
            Note = pp ? $"pp: {Rate(sample.TokensPerSecond)} t/s" : sample.FinishReason.Replace('_', ' ').ToLowerInvariant(),
            Csv = string.Join(',', DateTime.Now.ToString("O", CultureInfo.InvariantCulture), source.Replace(',', ' '), model,
                (pp ? sample.Test.PromptTokens : 1) + sample.Test.Depth, sample.GeneratedTokens,
                sample.FirstToken.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                pp ? "" : sample.TokensPerSecond.ToString("F3", CultureInfo.InvariantCulture), "",
                sample.LastToken.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                sample.TokensPerSecond.ToString("F3", CultureInfo.InvariantCulture), sample.FinishReason, "", "")
        };
        PerfRuns.Insert(0, item);
        PerfHistoryEmpty.Visibility = Visibility.Collapsed;
    }

    private readonly List<(BenchmarkTest Test, double Mean, double Deviation, double Milliseconds, double MsDeviation, int Runs)> benchmarkResults = new();
    public ObservableCollection<BenchmarkRowItem> BenchmarkRows { get; } = new();

    private void AddBenchmarkResult(string model, BenchmarkTest test, List<BenchmarkSample> samples)
    {
        static (double Mean, double Deviation) Stats(IEnumerable<double> values)
        {
            var array = values.ToArray();
            double mean = array.Average();
            // Sample standard deviation, as llama-bench reports.
            double deviation = array.Length > 1 ? Math.Sqrt(array.Sum(v => (v - mean) * (v - mean)) / (array.Length - 1)) : 0;
            return (mean, deviation);
        }
        var (mean, deviation) = Stats(samples.Select(s => s.TokensPerSecond));
        var (ms, msDeviation) = Stats(samples.Select(s => s.Duration.TotalMilliseconds));
        benchmarkResults.Add((test, mean, deviation, ms, msDeviation, samples.Count));
        uint shortest = samples.Min(s => s.GeneratedTokens);
        string note = test.GeneratedTokens > 0 && shortest < test.GeneratedTokens
            ? $"stopped early at end of sequence ({shortest} of {test.GeneratedTokens} tokens)"
            : $"{samples.Count} runs";
        BenchmarkRows.Add(new BenchmarkRowItem
        {
            Model = model,
            Test = test.Name,
            Rate = $"{mean:F2} \u00B1 {deviation:F2}",
            Milliseconds = ms.ToString("F1", CultureInfo.CurrentCulture),
            Note = note,
            Markdown = $"| {model} | CGC | {selectedGpu?.Name} | {test.Name} | {mean:F2} \u00B1 {deviation:F2} |"
        });
    }

    private void StopBenchmark_Click(object sender, RoutedEventArgs e)
    {
        benchmarkCancellation?.Cancel();
        StopBenchmarkButton.IsEnabled = false;
        SessionStatus.Text = "Stopping benchmark...";
    }

    private void ShowBenchmarkSummary(int runs, int warmups)
    {
        var pp = benchmarkResults.FirstOrDefault(r => r.Test.PromptTokens > 0);
        var tg = benchmarkResults.FirstOrDefault(r => r.Test.GeneratedTokens > 0);
        PerfPromptLabel.Text = pp.Runs > 0 ? $"Prompt processing ({pp.Test.Name})" : "Prompt processing";
        PerfPrompt.Text = pp.Runs > 0 ? Rate(pp.Mean) : "-";
        PerfPromptRange.Text = pp.Runs > 0 ? $"\u00B1 {Rate(pp.Deviation)}" : "Off";
        PerfDecodeLabel.Text = tg.Runs > 0 ? $"Text generation ({tg.Test.Name})" : "Text generation";
        PerfDecode.Text = tg.Runs > 0 ? Rate(tg.Mean) : "-";
        PerfDecodeRange.Text = tg.Runs > 0 ? $"\u00B1 {Rate(tg.Deviation)}" : "Off";
        PerfTtft.Text = pp.Runs > 0 ? $"{Ms(pp.Milliseconds)} ms" : "-";
        PerfTtftRange.Text = pp.Runs > 0 ? $"\u00B1 {Ms(pp.MsDeviation)} ms to evaluate {pp.Test.PromptTokens} tokens" : "";
        PerfMsPerToken.Text = tg.Runs > 0 ? $"{Ms(1000.0 / tg.Mean)} ms" : "-";
        PerfMsPerTokenRange.Text = tg.Runs > 0 ? $"{tg.Test.GeneratedTokens} tokens in {Ms(tg.Milliseconds)} ms" : "";
        PerfSummaryDetail.Text = $"Mean \u00B1 sample standard deviation of {runs} repetition(s) per test after {warmups} warm-up run(s). " +
            "Prompts are ordinary text tokens from the model's tokenizer; pp is timed to its first token and tg from a one-token prompt to its last token" +
            (benchmarkResults[0].Test.Depth > 0 ? $", after {benchmarkResults[0].Test.Depth} untimed context tokens (ContinueTokens)." : ".");
        PerfSummaryPanel.Visibility = Visibility.Visible;
    }

    private void CopyBenchmark_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var markdown = new StringBuilder("| model | backend | device | test | t/s |\n| --- | --- | --- | --: | --: |\n");
            foreach (var row in BenchmarkRows) markdown.AppendLine(row.Markdown);
            var data = new DataPackage();
            data.SetText(markdown.ToString());
            Clipboard.SetContent(data);
            ShowStatus("Copied as Markdown", $"{BenchmarkRows.Count} test(s).", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
    }

    private async Task RefreshGpuMemoryAsync()
    {
        if (selectedGpu is null) return;
        try
        {
            ulong bytes = await runtime.GetGpuMemoryUsageAsync(selectedGpu);
            PerfGpuMemory.Text = $"{bytes / (1024.0 * 1024.0):N0} MiB";
        }
        catch (Exception error) { PerfGpuMemory.Text = $"unavailable (0x{error.HResult:X8})"; }
    }

    private async void RefreshGpuMemory_Click(object sender, RoutedEventArgs e) => await RefreshGpuMemoryAsync();

    private async void UpdatePerfSession()
    {
        PerfModel.Text = loaded ? System.IO.Path.GetFileName(ModelPath.Text.Trim().TrimEnd('\\')) : "No text model loaded";
        PerfDevice.Text = selectedGpu is null ? "-" : $"{selectedGpu.Name} / CGC";
        PerfContext.Text = loaded ? loadedContext : "-";
        PerfLoadTime.Text = loaded && lastLoadTime is { } load ? $"{load.TotalSeconds:F1} s" : "-";
        await RefreshGpuMemoryAsync();
    }

    private void ClearPerf()
    {
        PerfRuns.Clear();
        BenchmarkRows.Clear();
        benchmarkResults.Clear();
        PerfHistoryEmpty.Visibility = Visibility.Visible;
        PerfSummaryPanel.Visibility = Visibility.Collapsed;
        SpeculativeRows.Clear();
        SpeculativeResultsPanel.Visibility = Visibility.Collapsed;
    }

    private void ClearPerf_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        ClearPerf();
        UpdatePerfControls();
        ShowStatus("Perf history cleared", "", InfoBarSeverity.Informational);
    }

    private void CopyPerf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var csv = new StringBuilder("time,source,model,prompt_tokens,generated_tokens,ttft_ms,decode_tok_per_s,ms_per_token,total_ms,overall_tok_per_s,finish_reason,prompt_cache_mode,cached_prompt_tokens,speculative_method,verify_steps,draft_tokens,accepted_tokens,accept_rate\n");
            foreach (var run in PerfRuns.Reverse()) csv.AppendLine(run.Csv);
            var data = new DataPackage();
            data.SetText(csv.ToString());
            Clipboard.SetContent(data);
            ShowStatus("Copied as CSV", $"{PerfRuns.Count} row(s).", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void UpdatePerfControls()
    {
        BenchmarkButton.IsEnabled = !busy && loaded;
        BenchmarkPromptTokens.IsEnabled = BenchmarkTokens.IsEnabled = BenchmarkDepth.IsEnabled = BenchmarkRuns.IsEnabled = BenchmarkWarmup.IsEnabled = !busy;
        CopyBenchmarkButton.IsEnabled = !busy && BenchmarkRows.Count > 0;
        CopyPerfButton.IsEnabled = ClearPerfButton.IsEnabled = !busy && PerfRuns.Count > 0;
        BenchmarkHint.Text = loaded ? "Runs on the loaded model. This resets the chat conversation. Context must hold depth + prompt + generated tokens."
            : "Load a text model in the sidebar to benchmark it.";
        UpdateSpeculativeControls();
    }
}
