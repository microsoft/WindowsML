// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskStudio.Runtime;
using Windows.ApplicationModel.DataTransfer;

namespace TaskStudio;

public sealed class SpeculativeRowItem
{
    public string PromptLabel { get; set; } = "";
    public string Method { get; set; } = "";
    public string Generated { get; set; } = "";
    public string Decode { get; set; } = "";
    public string Drafted { get; set; } = "";
    public string Accepted { get; set; } = "";
    public string AcceptRate { get; set; } = "";
    public string TokensPerStep { get; set; } = "";
    internal string Markdown { get; set; } = "";
}

public sealed partial class MainWindow
{
    public ObservableCollection<SpeculativeRowItem> SpeculativeRows { get; } = new();
    private string speculativeSummary = "";

    private SpeculativeChoice SelectedSpeculation()
    {
        var mode = ((SpeculativeMethod.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "lookup" => SpeculativeMode.PromptLookup,
            "layers" => SpeculativeMode.ModelLayers,
            "companion" => SpeculativeMode.CompanionDrafter,
            _ => SpeculativeMode.Off
        };
        return new SpeculativeChoice(mode, mode == SpeculativeMode.CompanionDrafter ? DrafterPath.Text.Trim() : null);
    }

    private void SpeculativeMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DrafterRow is null) return;
        DrafterRow.Visibility = SelectedSpeculation().Mode == SpeculativeMode.CompanionDrafter ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowseDrafter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The drafter is loaded the same way as the model: a folder for safetensors, a file for GGUF.
            string? path = TaskRuntime.IsGgufFile(ModelPath.Text.Trim()) ? await PickFileAsync(".gguf") : await PickFolderAsync();
            if (path is not null) DrafterPath.Text = path;
        }
        catch (Exception error) { ShowError(error); }
    }

    private static string SpeculationLabel(SpeculativeMode mode) => mode switch
    {
        SpeculativeMode.PromptLookup => "Prompt lookup",
        SpeculativeMode.ModelLayers => "Draft layers (MTP)",
        SpeculativeMode.CompanionDrafter => "Drafter model",
        _ => "Off"
    };

    // After a load, tells the user which methods the Runtime reports for this model.
    private void ShowSpeculativeSupport()
    {
        var report = runtime.SpeculativeSupport;
        var loadedChoice = runtime.LoadedSpeculation;
        if (!loaded || report is null)
        {
            SpeculativeHint.Visibility = Visibility.Collapsed;
            return;
        }
        bool drafter = loadedChoice.Mode == SpeculativeMode.CompanionDrafter;
        var supported = new[] { SpeculativeMode.PromptLookup, SpeculativeMode.ModelLayers, SpeculativeMode.CompanionDrafter }
            .Where(mode => report.IsSupported(mode, drafter) == true).Select(TaskRuntime.Describe).ToList();
        string draftCount = report.Model is { Supported: true } model && loadedChoice.Mode is SpeculativeMode.ModelLayers or SpeculativeMode.CompanionDrafter
            ? $", up to {model.DefaultDraftTokens} tokens per step" : "";
        SpeculativeHint.Text = (loadedChoice.Mode == SpeculativeMode.Off ? "Speculative decoding off." : $"Speculative decoding: {TaskRuntime.Describe(loadedChoice.Mode)}{draftCount}.") +
            (supported.Count > 0 ? $" Supported: {string.Join(", ", supported)}." : " No speculative method reported for this model.") +
            (loadedChoice.Mode == SpeculativeMode.Off ? "" : " Completion and Perf show accepted drafts.");
        SpeculativeHint.Visibility = Visibility.Visible;
    }

    private static string SpeculationNote(SpeculativeStats? stats) => stats is { Active: true } s ? $" / {s.Describe()}" : "";

    private async void SpeculativeCompare_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !loaded || runtime.LoadedSpeculation.Mode == SpeculativeMode.Off) return;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        benchmarkCancellation = cancellation;
        StopBenchmarkButton.Visibility = Visibility.Visible;
        StopBenchmarkButton.IsEnabled = true;
        try
        {
            var mode = runtime.LoadedSpeculation.Mode;
            uint maxTokens = ComboTag(SpeculativeTokens);
            uint contextTokens = ComboTag(SpeculativePromptSet);
            bool ignoreEndOfText = (string)((ComboBoxItem)SpeculativeEndOfText.SelectedItem).Tag == "ignore";
            IReadOnlyList<BenchmarkPrompt> prompts = CodingBenchmark.ShortPrompts;
            if (contextTokens > 0)
            {
                SessionStatus.Text = "Building the coding prompts...";
                (prompts, uint promptTokens) = await runtime.BuildCodingPromptsAsync(contextTokens);
                SpeculativePromptList.Text = DescribePrompts(contextTokens, promptTokens);
            }
            var progress = new Progress<string>(text => SessionStatus.Text = text);
            var rows = await runtime.CompareSpeculationAsync(prompts, maxTokens, ignoreEndOfText, progress, cancellation.Token);
            // The comparison recreated the sessions, so the chat conversation starts over.
            ClearOutput();
            string model = System.IO.Path.GetFileName(ModelPath.Text.Trim().TrimEnd('\\'));
            SpeculativeRows.Clear();
            foreach (var row in rows)
            {
                string label = contextTokens > 0 ? $"{row.Prompt} ({row.PromptTokens:N0} tok)" : row.Prompt;
                var s = row.Stats;
                bool on = row.Mode != SpeculativeMode.Off;
                var item = new SpeculativeRowItem
                {
                    PromptLabel = label,
                    Method = SpeculationLabel(row.Mode),
                    Generated = row.Timing.GeneratedTokens.ToString("N0", CultureInfo.CurrentCulture),
                    Decode = Rate(row.Timing.DecodeTokensPerSecond),
                    Drafted = on && s is not null ? s.DraftTokens.ToString("N0", CultureInfo.CurrentCulture) : "-",
                    Accepted = on && s is not null ? s.AcceptedTokens.ToString("N0", CultureInfo.CurrentCulture) : "-",
                    AcceptRate = on && s?.AcceptRate is double rate ? rate.ToString("P0", CultureInfo.CurrentCulture) : "-",
                    TokensPerStep = on && s?.TokensPerStep is double step ? step.ToString("F2", CultureInfo.CurrentCulture) : "-"
                };
                item.Markdown = $"| {model} | {label} | {item.Method} | {item.Generated} | {item.Decode} | {item.Drafted} | {item.Accepted} | {item.AcceptRate} | {item.TokensPerStep} |";
                SpeculativeRows.Add(item);
                RecordPerf($"A/B {(on ? "spec" : "off")}", new GenerationResult(row.Text, row.PromptTokens, row.FinishReason, false, row.Timing.Total, row.Timing, row.Stats));
            }

            var off = rows.Where(r => r.Mode == SpeculativeMode.Off).ToList();
            var spec = rows.Where(r => r.Mode == mode).ToList();
            static double? Overall(IEnumerable<SpeculativeComparisonRow> set)
            {
                var measured = set.Where(r => r.Timing.DecodeMs is > 0).ToList();
                return measured.Count == 0 ? null
                    : measured.Sum(r => (double)r.Timing.GeneratedTokens - 1) / measured.Sum(r => r.Timing.DecodeMs!.Value / 1000);
            }
            double? offRate = Overall(off), specRate = Overall(spec);
            int identical = off.Zip(spec).Count(pair => pair.First.Text == pair.Second.Text);
            string divergence = string.Join("; ", off.Zip(spec).Where(pair => pair.First.Text != pair.Second.Text)
                .Select(pair => $"{pair.First.Prompt} matches for the first {TextComparison.CommonPrefix(pair.First.Text, pair.Second.Text):N0} of {pair.First.Text.Length:N0} characters"));
            uint drafted = (uint)spec.Sum(r => r.Stats?.DraftTokens ?? 0), accepted = (uint)spec.Sum(r => r.Stats?.AcceptedTokens ?? 0);
            string speedup = offRate is double offValue && specRate is double specValue && offValue > 0
                ? $"{specValue / offValue:F2}x decode" : "Speedup unavailable";
            SpeculativeSpeedup.Text = $"{speedup}  ({offRate?.ToString("F1", CultureInfo.CurrentCulture) ?? "unavailable"} \u2192 " +
                $"{specRate?.ToString("F1", CultureInfo.CurrentCulture) ?? "unavailable"} tok/s)";
            SpeculativeDetail.Text = $"{SpeculationLabel(mode)} on {model}. Identical greedy text on {identical} of {off.Count} prompts" +
                (identical == off.Count ? "." : $" (a multi-position verify step can pick differently on a near tie: {divergence}).") +
                $" Overall: drafted {drafted:N0}, accepted {accepted:N0}" + (drafted > 0 ? $" ({(double)accepted / drafted:P0})." : ".") +
                (ignoreEndOfText ? $" {maxTokens:N0} tokens per prompt (end of text ignored)" : $" Up to {maxTokens:N0} tokens per prompt") +
                ", greedy, measured by the app on the Task stream.";
            speculativeSummary = $"{SpeculativeSpeedup.Text}\n\n{SpeculativeDetail.Text}";
            SpeculativeResultsPanel.Visibility = Visibility.Visible;
            await RefreshGpuMemoryAsync();
            ShowStatus("Comparison complete", "The chat conversation was reset because the comparison recreated the sessions.",
                InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) { ShowStatus("Comparison stopped", "The loaded method was restored.", InfoBarSeverity.Informational); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            benchmarkCancellation = null;
            StopBenchmarkButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void CopySpeculative_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var markdown = new StringBuilder("| model | prompt | method | out | decode t/s | drafted | accepted | accept | tok/step |\n" +
                "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |\n");
            foreach (var row in SpeculativeRows) markdown.AppendLine(row.Markdown);
            markdown.AppendLine().AppendLine(speculativeSummary);
            var data = new DataPackage();
            data.SetText(markdown.ToString());
            Clipboard.SetContent(data);
            ShowStatus("Copied as Markdown", $"{SpeculativeRows.Count} row(s).", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
    }

    private static uint ComboTag(ComboBox box) => uint.Parse((string)((ComboBoxItem)box.SelectedItem).Tag, CultureInfo.InvariantCulture);

    private void SpeculativePromptSet_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeculativePromptList is not null) SpeculativePromptList.Text = DescribePrompts(ComboTag(SpeculativePromptSet), null);
    }

    private static string DescribePrompts(uint contextTokens, uint? measured)
    {
        if (contextTokens == 0)
            return string.Join("\n\n", CodingBenchmark.ShortPrompts.Select((p, i) => $"{i + 1}. {p.Label}\n{p.Text.TrimEnd()}"));
        string files = string.Join(", ", CodingBenchmark.LoadFiles().Select(f => f.Path));
        return $"Context: about {contextTokens:N0} tokens{(measured is uint m ? $" ({m:N0} with the longest task, counted by the model's tokenizer)" : "")} " +
            $"of a frozen snapshot of this app's C# source, as \"### File: <path>\" + a C# code block per file, in this order " +
            $"(the file that does not fit is cut at a line; Runtime/TaskRuntime.Benchmark.cs is always included last):\n{files}\n\n" +
            "Then \"### Task\" with one of:\n\n" +
            string.Join("\n\n", CodingBenchmark.CodingTasks.Select((t, i) => $"{i + 1}. {t.Label}\n{t.Task}")) +
            "\n\nand \"### Answer\".";
    }

    private void UpdateSpeculativeControls()
    {
        bool active = loaded && runtime.LoadedSpeculation.Mode != SpeculativeMode.Off;
        SpeculativeCompareButton.IsEnabled = !busy && active;
        SpeculativeTokens.IsEnabled = !busy;
        SpeculativePromptSet.IsEnabled = SpeculativeEndOfText.IsEnabled = !busy;
        if (string.IsNullOrEmpty(SpeculativePromptList.Text)) SpeculativePromptList.Text = DescribePrompts(ComboTag(SpeculativePromptSet), null);
        CopySpeculativeButton.IsEnabled = !busy && SpeculativeRows.Count > 0;
        SpeculativeCompareHint.Text = !loaded ? "Load a text model with a speculative decoding method to compare it."
            : active ? $"Compares Off with {TaskRuntime.Describe(runtime.LoadedSpeculation.Mode)}. Reloads the sessions twice and resets the chat conversation."
            : "Choose a speculative decoding method in the sidebar and create task sessions to compare it with Off.";
    }
}
