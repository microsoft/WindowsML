// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;
using static Microsoft.Windows.AI.MachineLearning.Runtime.Tasks.WINML_DECISION_QUESTION_KIND;

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
    private const int CanceledHResult = unchecked((int)0x800704C7);
    private unsafe IWinMLRuntime* decisionRuntime;
    private unsafe IWinMLTasks* decisionTasks;
    private unsafe IWinMLDecisionSession* decisionSession;

    public unsafe Task<DecisionCapabilities> LoadDecisionAsync(string modelPath, GpuDevice device) => Run(() =>
    {
        if (!Directory.Exists(modelPath) || !File.Exists(Path.Combine(modelPath, "config.json")))
            throw new ArgumentException("Choose a typed-decision model directory containing config.json, such as a Laya checkpoint.");
        if (decisionRuntime != null) throw new InvalidOperationException("Release the decision session before loading another model.");
        IWinMLExecutionTarget* target = null;
        IWinMLModel* model = null;
        IWinMLTokenizer* tokenizer = null;
        IWinMLDecisionTaskFactory* factory = null;
        IWinMLDecisionTask* task = null;
        try
        {
            Guid iid = typeof(IWinMLRuntime).GUID;
            IWinMLRuntime* createdRuntime = null;
            PInvoke.WinMLCreateRuntime(&iid, (void**)&createdRuntime).ThrowOnFailure();
            decisionRuntime = createdRuntime;
            iid = typeof(IWinMLTasks).GUID;
            IWinMLTasks* createdTasks = null;
            PInvoke.WinMLCreateTasks(decisionRuntime, &iid, (void**)&createdTasks).ThrowOnFailure();
            decisionTasks = createdTasks;
            target = CreateGpuTarget(decisionRuntime, device);
            GpuDevice actualDevice = DescribeGpuTarget(target);
            model = decisionRuntime->LoadModelFromPath(modelPath, null);
            // Typed-decision checkpoints publish their tokenizer in a tokenizer folder.
            string tokenizerDirectory = Path.Combine(modelPath, "tokenizer");
            PInvoke.WinMLCreateTokenizerFromFile(Directory.Exists(tokenizerDirectory) ? tokenizerDirectory : modelPath, &tokenizer)
                .ThrowOnFailure();
            decisionTasks->QueryInterface(out factory).ThrowOnFailure();
            fixed (char* directory = CompiledProgramDirectory)
            {
                var modelOptions = new WINML_DECISION_MODEL_OPTIONS
                {
                    presentFields = WINML_DECISION_MODEL_OPTION_FIELDS.WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY
                        | WINML_DECISION_MODEL_OPTION_FIELDS.WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE,
                    compiledProgramDirectory = new PCWSTR(directory),
                    compiledProgramStorage = WINML_COMPILED_PROGRAM_STORAGE.WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS
                };
                task = factory->CreateDecisionTaskFromModel(target, tokenizer, model, &modelOptions);
            }
            decisionSession = task->CreateSession();
            WINML_DECISION_CAPABILITIES capabilities;
            decisionSession->GetCapabilities(&capabilities);
            return new DecisionCapabilities(capabilities.maximumOptionsPerQuestion, capabilities.calibrated, actualDevice);
        }
        catch
        {
            UnloadDecision();
            throw;
        }
        finally { Release(task); Release(factory); Release(tokenizer); Release(model); Release(target); }
    });

    public unsafe Task<DecisionOutcome> DecideAsync(string state, IReadOnlyList<DecisionQuestion> questions, CancellationToken cancellation)
    {
        // Snapshot caller-owned input before queuing work on the native thread.
        var snapshot = questions.Select(q => q with { Options = q.Options.ToArray() }).ToArray();
        return Run(() =>
        {
            if (decisionSession == null) throw new InvalidOperationException("Create a decision session first.");
            if (snapshot.Length == 0) throw new ArgumentException("Add at least one question.");
            var allocations = new List<nint>();
            var optionArrays = new WINML_DECISION_OPTION[snapshot.Length][];
            var handles = new List<GCHandle>();
            var nativeQuestions = new WINML_DECISION_QUESTION[snapshot.Length];
            IWinMLCancellationSource* source = decisionTasks->CreateCancellationSource();
            IWinMLDecisionResult* result = null;
            try
            {
                PCWSTR Text(string? value)
                {
                    if (value is null) return default;
                    nint address = Marshal.StringToCoTaskMemUni(value);
                    allocations.Add(address);
                    return new PCWSTR((char*)address);
                }
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var question = snapshot[i];
                    optionArrays[i] = question.Options.Select(o => new WINML_DECISION_OPTION
                    {
                        label = Text(o.Label),
                        description = Text(o.Description)
                    }).ToArray();
                    var handle = GCHandle.Alloc(optionArrays[i], GCHandleType.Pinned);
                    handles.Add(handle);
                    nativeQuestions[i] = new WINML_DECISION_QUESTION
                    {
                        kind = question.Kind switch
                        {
                            DecisionKind.Choice => WINML_DECISION_QUESTION_KIND_CHOICE,
                            DecisionKind.Score => WINML_DECISION_QUESTION_KIND_SCORE,
                            _ => WINML_DECISION_QUESTION_KIND_YES_NO
                        },
                        instructions = Text(question.Instructions),
                        optionCount = (uint)optionArrays[i].Length,
                        options = optionArrays[i].Length == 0 ? null : (WINML_DECISION_OPTION*)handle.AddrOfPinnedObject(),
                        presentFields = question.ActThreshold.HasValue
                            ? WINML_DECISION_QUESTION_FIELDS.WINML_DECISION_QUESTION_FIELDS_ACT_THRESHOLD
                            : WINML_DECISION_QUESTION_FIELDS.WINML_DECISION_QUESTION_FIELDS_NONE,
                        actThreshold = question.ActThreshold ?? 0
                    };
                }
                nint sourceAddress = (nint)source;
                using var registration = cancellation.Register(() => ((IWinMLCancellationSource*)sourceAddress)->Cancel());
                var stopwatch = Stopwatch.StartNew();
                fixed (WINML_DECISION_QUESTION* questionPointer = nativeQuestions)
                {
                    var request = new WINML_DECISION_REQUEST
                    {
                        state = Text(state),
                        questionCount = (uint)nativeQuestions.Length,
                        questions = questionPointer
                    };
                    try { decisionSession->Decide(&request, source, &result); }
                    catch (Exception error) when (error.HResult == CanceledHResult)
                    {
                        return new DecisionOutcome([], 0, stopwatch.Elapsed, true);
                    }
                }
                TimeSpan elapsed = stopwatch.Elapsed;
                uint count = result->GetAnswerCount();
                var answers = new DecisionAnswer[count];
                for (uint i = 0; i < count; i++)
                {
                    WINML_DECISION_ANSWER answer;
                    result->GetAnswer(i, &answer);
                    var probabilities = new float[answer.optionCount];
                    fixed (float* probabilityPointer = probabilities)
                        result->GetProbabilities(i, answer.optionCount, probabilityPointer);
                    answers[i] = new DecisionAnswer(snapshot[i].Kind, answer.selectedOption, probabilities, answer.confidence,
                        answer.distributionConfidence, answer.expectedScore, answer.actProbability,
                        answer.recommendation == WINML_DECISION_RECOMMENDATION.WINML_DECISION_RECOMMENDATION_ACT);
                }
                return new DecisionOutcome(answers, result->GetInputTokenCount(), elapsed, false);
            }
            finally
            {
                Release(result);
                Release(source);
                foreach (var handle in handles) handle.Free();
                foreach (nint address in allocations) Marshal.FreeCoTaskMem(address);
            }
        });
    }

    public unsafe Task UnloadDecisionAsync() => Run(() => { UnloadDecision(); return true; });

    private unsafe void UnloadDecision()
    {
        try { if (decisionSession != null) decisionSession->Close(); }
        finally
        {
            Release(decisionSession); decisionSession = null;
            Release(decisionTasks); decisionTasks = null;
            Release(decisionRuntime); decisionRuntime = null;
        }
    }
}
