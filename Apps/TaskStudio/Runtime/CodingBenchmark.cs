// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;

namespace TaskStudio.Runtime;

internal sealed record BenchmarkPrompt(string Label, string Text);

// Prompt sets for the speculative decoding comparison.
internal static class CodingBenchmark
{
    // Greedy prompts: one that repeats its input, one of free prose, one predictable list.
    public static IReadOnlyList<BenchmarkPrompt> ShortPrompts { get; } =
    [
        new("Rewrite code", "Here is a Python function:\n\ndef area(width, height):\n    return width * height\n\n" +
            "Rewrite the function with type hints and a docstring. Show the complete function.\n\n"),
        new("Explain", "Explain in one paragraph what a GPU is and why it is good at running neural networks.\n\n"),
        new("Count", "Count from 1 to 40, separated by commas: 1, 2, 3,")
    ];

    // Coding tasks asked about a large source context, as a coding assistant would see it. The context is a frozen
    // snapshot of this app's own C# source (BenchmarkData\coding); the Edit task targets its last file.
    public static IReadOnlyList<(string Label, string Task)> CodingTasks { get; } =
    [
        ("Edit file", "Add an XML documentation comment (///) to every type and method in Runtime/TaskRuntime.Benchmark.cs. " +
            "Output the complete updated file in one C# code block."),
        ("Explain", "Explain how TaskRuntime loads a text model and creates its Chat and Completion sessions. " +
            "Name the methods involved, in order."),
        ("Write tests", "Write xUnit tests for BenchmarkTest.Name and BenchmarkSample.TokensPerSecond " +
            "in Runtime/TaskRuntime.Benchmark.cs.")
    ];

    public const string Header = "You are an expert C# engineer working in the repository below.\n\n";

    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "BenchmarkData", "coding");

    // The snapshot's files in order, with their original repository paths.
    public static IReadOnlyList<(string Path, string Text)> LoadFiles()
    {
        string manifest = Path.Combine(DataDirectory, "files.txt");
        if (!File.Exists(manifest)) throw new FileNotFoundException("The coding benchmark data is missing next to the app.", manifest);
        return File.ReadAllLines(manifest)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split('\t'))
            .Select(parts => (parts[1], File.ReadAllText(Path.Combine(DataDirectory, parts[0]))))
            .ToList();
    }

    public static string Section(string path, string text) => $"### File: {path}\n```csharp\n{text.TrimEnd()}\n```\n\n";

    public static string Ask(string context, string task) => $"{context}### Task\n{task}\n\n### Answer\n";
}

internal sealed partial class TaskRuntime
{
    // Builds the coding prompts with about targetTokens of source context, counted with the loaded model's tokenizer.
    // Whole files are added in order; the file that would overflow is cut at a line boundary. Returns the prompts
    // and the token count of the longest one.
    public unsafe Task<(IReadOnlyList<BenchmarkPrompt> Prompts, uint PromptTokens)> BuildCodingPromptsAsync(uint targetTokens) => Run(() =>
    {
        if (tokenizer == null) throw new InvalidOperationException("Load a text model first.");
        var files = CodingBenchmark.LoadFiles();
        var (targetPath, targetText) = files[^1];
        string targetSection = CodingBenchmark.Section(targetPath, targetText);
        uint reserved = CountTokens(CodingBenchmark.Header) + CountTokens(targetSection) +
            CodingBenchmark.CodingTasks.Max(t => CountTokens(CodingBenchmark.Ask("", t.Task)));
        long budget = (long)targetTokens - reserved;
        var context = new System.Text.StringBuilder(CodingBenchmark.Header);
        foreach (var (path, text) in files.Take(files.Count - 1))
        {
            if (budget <= 0) break;
            string section = CodingBenchmark.Section(path, text);
            uint count = CountTokens(section);
            if (count <= budget)
            {
                context.Append(section);
                budget -= count;
                continue;
            }
            // Cut the overflowing file at a line boundary so the context stays real code.
            string[] lines = text.Split('\n');
            int keep = (int)(lines.Length * (double)budget / count);
            string partial;
            while (true)
            {
                partial = CodingBenchmark.Section(path, string.Join('\n', lines.Take(keep)) + "\n// ... (file continues)");
                if (keep <= 0 || CountTokens(partial) <= budget) break;
                keep = Math.Max(0, keep - Math.Max(1, keep / 20));
            }
            if (keep > 0) context.Append(partial);
            break;
        }
        context.Append(targetSection);
        var prompts = CodingBenchmark.CodingTasks.Select(t => new BenchmarkPrompt(t.Label, CodingBenchmark.Ask(context.ToString(), t.Task))).ToList();
        return ((IReadOnlyList<BenchmarkPrompt>)prompts, prompts.Max(p => CountTokens(p.Text)));
    });

    private unsafe uint CountTokens(string text)
    {
        uint* ids = null;
        try
        {
            tokenizer->Encode(text, (WINML_TOKENIZER_ENCODE_FLAGS)0, out uint count, &ids);
            return count;
        }
        finally { Marshal.FreeCoTaskMem((nint)ids); }
    }
}
