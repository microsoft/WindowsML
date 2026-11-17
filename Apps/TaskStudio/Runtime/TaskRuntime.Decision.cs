// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;
using WinMLDecisionOption = WinMLSamples.Runtime.Tasks.DecisionOption;
using WinMLDecisionQuestion = WinMLSamples.Runtime.Tasks.DecisionQuestion;

namespace TaskStudio.Runtime;

internal enum DecisionKind { Choice, Score, YesNo }
internal sealed record DecisionOption(string? Label, string? Description);
internal sealed record DecisionQuestion(DecisionKind Kind, string Instructions, IReadOnlyList<DecisionOption> Options, float? ActThreshold = null);
internal sealed record DecisionAnswer(DecisionKind Kind, uint Selected, float[] Probabilities, float Confidence,
    float DistributionConfidence, float ExpectedScore, float ActProbability, bool Act);
internal sealed record DecisionOutcome(IReadOnlyList<DecisionAnswer> Answers, ulong InputTokens, TimeSpan Elapsed, bool Canceled);
internal sealed record DecisionCapabilities(uint MaximumOptions, bool Calibrated, GpuDevice Device);

internal sealed partial class TaskRuntime
{
    private WinMLRuntime? decisionRuntime;
    private WinMLTasks? decisionTasks;
    private DecisionSession? decisionSession;

    public Task<DecisionCapabilities> LoadDecisionAsync(string modelPath, GpuDevice device) => Run(() =>
    {
        if (!Directory.Exists(modelPath) || !File.Exists(Path.Combine(modelPath, "config.json")))
            throw new ArgumentException("Choose a typed-decision model directory containing config.json, such as a Laya checkpoint.");
        if (decisionRuntime != null) throw new InvalidOperationException("Release the decision session before loading another model.");
        ExecutionTarget? target = null;
        Model? model = null;
        Tokenizer? tokenizer = null;
        DecisionTask? task = null;
        try
        {
            decisionRuntime = WinMLRuntime.Create();
            decisionTasks = WinMLTasks.Create(decisionRuntime);
            target = CreateGpuTarget(decisionRuntime, device);
            GpuDevice actualDevice = DescribeGpuTarget(target);
            model = decisionRuntime.LoadModelFromPath(modelPath);
            // Typed-decision checkpoints publish their tokenizer in a tokenizer folder.
            string tokenizerDirectory = Path.Combine(modelPath, "tokenizer");
            tokenizer = Tokenizer.CreateFromFile(Directory.Exists(tokenizerDirectory) ? tokenizerDirectory : modelPath);
            task = decisionTasks.Decision.CreateDecisionTaskFromModel(target, model, tokenizer, new DecisionModelOptions
            {
                CompiledProgramDirectory = CompiledProgramDirectory,
                CompiledProgramStorage = CompiledProgramStorage.Programs
            });
            decisionSession = task.CreateSession();
            var capabilities = decisionSession.Capabilities;
            return new DecisionCapabilities(capabilities.MaximumOptionsPerQuestion, capabilities.Calibrated, actualDevice);
        }
        catch
        {
            UnloadDecision();
            throw;
        }
        finally { task?.Dispose(); tokenizer?.Dispose(); model?.Dispose(); target?.Dispose(); }
    });

    public Task<DecisionOutcome> DecideAsync(string state, IReadOnlyList<DecisionQuestion> questions, CancellationToken cancellation)
    {
        // Snapshot caller-owned input before queuing work on the native thread.
        var snapshot = questions.Select(q => q with { Options = q.Options.ToArray() }).ToArray();
        return Run(() =>
        {
            if (decisionSession == null) throw new InvalidOperationException("Create a decision session first.");
            if (snapshot.Length == 0) throw new ArgumentException("Add at least one question.");
            var request = new DecisionRequest
            {
                State = state,
                Questions = snapshot.Select(question => new WinMLDecisionQuestion
                {
                    Kind = question.Kind switch
                    {
                        DecisionKind.Choice => DecisionQuestionKind.Choice,
                        DecisionKind.Score => DecisionQuestionKind.Score,
                        _ => DecisionQuestionKind.YesNo
                    },
                    Instructions = question.Instructions,
                    Options = question.Options.Select(option => new WinMLDecisionOption(option.Label, option.Description)).ToArray(),
                    ActThreshold = question.ActThreshold
                }).ToArray()
            };
            using CancellationSource source = decisionTasks!.CreateCancellationSource();
            using var registration = cancellation.Register(source.Cancel);
            var stopwatch = Stopwatch.StartNew();
            DecisionResult result;
            try { result = decisionSession.Decide(request, source); }
            catch (Exception error) when (error.HResult == CanceledHResult)
            {
                return new DecisionOutcome([], 0, stopwatch.Elapsed, true);
            }
            using (result)
            {
                TimeSpan elapsed = stopwatch.Elapsed;
                uint count = result.AnswerCount;
                var answers = new DecisionAnswer[count];
                for (uint i = 0; i < count; i++)
                {
                    var answer = result.GetAnswer(i);
                    answers[i] = new DecisionAnswer(snapshot[i].Kind, answer.SelectedOption, result.GetProbabilities(i), answer.Confidence,
                        answer.DistributionConfidence, answer.ExpectedScore, answer.ActProbability,
                        answer.Recommendation == DecisionRecommendation.Act);
                }
                return new DecisionOutcome(answers, result.InputTokenCount, elapsed, false);
            }
        });
    }

    public Task UnloadDecisionAsync() => Run(() => { UnloadDecision(); return true; });

    private void UnloadDecision()
    {
        try { decisionSession?.Close(); }
        finally
        {
            Release(ref decisionSession);
            Release(ref decisionTasks);
            Release(ref decisionRuntime);
        }
    }
}
