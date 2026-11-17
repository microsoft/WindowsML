// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using TaskStudio.Runtime;

namespace TaskStudio;

public sealed class DecisionQuestionItem
{
    public string Name { get; set; } = "";
    public int KindIndex { get; set; }
    public string Instructions { get; set; } = "";
    public string OptionsText { get; set; } = "";
    public double Threshold { get; set; } = double.NaN;

    internal DecisionKind Kind => (DecisionKind)KindIndex;

    internal DecisionQuestion ToQuestion(uint maximumOptions)
    {
        string title = string.IsNullOrWhiteSpace(Name) ? "A question" : $"'{Name.Trim()}'";
        if (string.IsNullOrWhiteSpace(Instructions)) throw new ArgumentException($"{title} needs instructions.");
        string[] lines = OptionsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DecisionOption[] options = Kind switch
        {
            DecisionKind.Choice => lines.Select(line =>
            {
                int colon = line.IndexOf(':');
                return colon < 0 ? new DecisionOption(line, null)
                    : new DecisionOption(line[..colon].Trim(), NullIfEmpty(line[(colon + 1)..].Trim()));
            }).ToArray(),
            DecisionKind.Score => lines.Select(line => new DecisionOption(null, line)).ToArray(),
            _ => lines.Length is 0 or 2 ? lines.Select(line => new DecisionOption(null, line)).ToArray()
                : throw new ArgumentException($"{title}: a yes/no question takes no options, or two lines - the 'no' description, then the 'yes' description.")
        };
        if (Kind != DecisionKind.YesNo && options.Length == 0)
            throw new ArgumentException($"{title} needs at least one option, one per line.");
        if (options.Length > maximumOptions)
            throw new ArgumentException($"{title} has {options.Length} options; this model accepts up to {maximumOptions}.");
        if (!double.IsNaN(Threshold) && Threshold is < 0 or > 1)
            throw new ArgumentException($"{title}: the act threshold must be between 0 and 1.");
        return new DecisionQuestion(Kind, Instructions.Trim(), options, double.IsNaN(Threshold) ? null : (float)Threshold);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

public sealed class ProbabilityRow
{
    public string Label { get; set; } = "";
    public string Detail { get; set; } = "";
    public double Percent { get; set; }
    public string PercentText { get; set; } = "";
    public SolidColorBrush BarBrush { get; set; } = null!;
}

public sealed class DecisionResultItem
{
    public string Name { get; set; } = "";
    public string KindLabel { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string AnswerText { get; set; } = "";
    public string RecommendationText { get; set; } = "";
    public SolidColorBrush RecommendationBackground { get; set; } = null!;
    public SolidColorBrush RecommendationForeground { get; set; } = null!;
    public string MetricsText { get; set; } = "";
    public IReadOnlyList<ProbabilityRow> Probabilities { get; set; } = [];

    internal static DecisionResultItem Create(DecisionQuestionItem question, DecisionQuestion request, DecisionAnswer answer)
    {
        var rows = answer.Probabilities.Select((probability, index) => new ProbabilityRow
        {
            Label = request.Kind switch
            {
                DecisionKind.YesNo => index == 1 ? "Yes" : "No",
                DecisionKind.Score => $"Level {index}",
                _ => request.Options[index].Label ?? $"Option {index}"
            },
            Detail = index < request.Options.Count ? request.Options[index].Description ?? "" : "",
            Percent = probability * 100,
            PercentText = probability.ToString("P1", CultureInfo.CurrentCulture),
            BarBrush = Brush(index == answer.Selected ? 0x79B4FFu : 0x3A4A5Cu)
        }).ToList();
        string answerText = rows[(int)answer.Selected].Label;
        if (request.Kind == DecisionKind.Score && request.Options.Count > answer.Selected)
            answerText += $"  /  {request.Options[(int)answer.Selected].Description}";
        string metrics = $"Confidence {answer.Confidence:P1}   Distribution {answer.DistributionConfidence:P0}   Act estimate {answer.ActProbability:P0}";
        if (request.Kind == DecisionKind.Score) metrics += $"   Expected level {answer.ExpectedScore:F2}";
        if (request.ActThreshold is float threshold) metrics += $"   Threshold {threshold:P0}";
        return new DecisionResultItem
        {
            Name = string.IsNullOrWhiteSpace(question.Name) ? "question" : question.Name.Trim(),
            KindLabel = request.Kind switch { DecisionKind.Choice => "CHOICE", DecisionKind.Score => "SCORE", _ => "YES / NO" },
            Instructions = request.Instructions,
            AnswerText = answerText,
            RecommendationText = answer.Act ? "ACT" : "ESCALATE",
            RecommendationBackground = Brush(answer.Act ? 0x1C3A2Bu : 0x3D2E14u),
            RecommendationForeground = Brush(answer.Act ? 0x79D5A1u : 0xF2C46Bu),
            MetricsText = metrics,
            Probabilities = rows
        };
    }

    private static SolidColorBrush Brush(uint rgb) =>
        new(ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}

internal static class DecisionExamples
{
    public static readonly string[] Names = ["Support ticket", "Product review", "Email triage"];

    public static (string State, DecisionQuestionItem[] Questions) Get(int index) => index switch
    {
        1 => ("I've used this blender daily for three months. It crushes ice well, but the lid started leaking last week and support hasn't replied.",
        [
            new() { Name = "sentiment", KindIndex = 0, Instructions = "What is the overall sentiment of the review?",
                OptionsText = "positive: the customer is satisfied\nmixed: both praise and complaints\nnegative: the customer is dissatisfied" },
            new() { Name = "quality", KindIndex = 1, Instructions = "How would the reviewer rate product quality?",
                OptionsText = "poor\nfair\ngood\nexcellent" },
            new() { Name = "needs_follow_up", KindIndex = 2, Instructions = "Does the customer need a follow-up from support?" }
        ]),
        2 => ("Subject: Quarterly security review\nPlease send the signed vendor risk assessment by Friday so we can close the audit. Legal is copied.",
        [
            new() { Name = "category", KindIndex = 0, Instructions = "Which folder should this email go to?",
                OptionsText = "action: needs a reply or task\nfyi: informational only\nnewsletter: marketing or bulk mail\nspam: unsolicited or malicious" },
            new() { Name = "priority", KindIndex = 1, Instructions = "How time-sensitive is this email?",
                OptionsText = "whenever\nthis week\ntoday" },
            new() { Name = "has_deadline", KindIndex = 2, Instructions = "Does the email state an explicit deadline?", Threshold = 0.8 }
        ]),
        _ => ("Hi, we were billed twice for March. Please refund the duplicate today or we will cancel our plan.",
        [
            new() { Name = "department", KindIndex = 0, Instructions = "Which department should handle this?",
                OptionsText = "billing: invoices, payments, refunds\ntechnical: bugs, outages, system errors\nother: everything else" },
            new() { Name = "urgency", KindIndex = 1, Instructions = "How urgent is this?", OptionsText = "not urgent\nsoon\nblocking" },
            new() { Name = "churn_risk", KindIndex = 2, Instructions = "Does the user threaten to cancel or leave?" }
        ])
    };
}
