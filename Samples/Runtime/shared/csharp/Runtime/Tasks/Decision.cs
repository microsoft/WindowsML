// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The typed decision Task API: a task
// evaluates a state, such as a support ticket, against typed questions and
// returns a probability for each option, the selected option, a confidence,
// and a recommendation to act or escalate. A session is bound to the thread
// that created it; Decider owns one on a SessionThread for use from any
// thread. Results are immutable and free-threaded.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;

namespace WinMLSamples.Runtime.Tasks;

public enum DecisionQuestionKind
{
    // Pick one of the options by label.
    Choice = 0,

    // Option i is level i; its description states the criterion of that level.
    Score = 1,

    // No options, or exactly two: false, then true.
    YesNo = 2,
}

public enum DecisionRecommendation
{
    Act = 0,
    Escalate = 1,
}

// A Choice option needs a unique label; a Score option needs a description.
public sealed record DecisionOption(string? Label, string? Description = null);

public sealed class DecisionQuestion
{
    public DecisionQuestionKind Kind { get; init; }

    public required string Instructions { get; init; }

    public IReadOnlyList<DecisionOption>? Options { get; init; }

    // From 0 to 1: recommend Act when the answer's confidence reaches it.
    // Null recommends Act when the model's act probability is at least 0.5.
    public float? ActThreshold { get; init; }
}

public sealed class DecisionRequest
{
    // The text the questions are about. A structured state is passed serialized.
    public required string State { get; init; }

    public required IReadOnlyList<DecisionQuestion> Questions { get; init; }
}

// One question's answer. Read the option probabilities from
// DecisionResult.GetProbabilities.
public readonly record struct DecisionAnswer(
    DecisionQuestionKind Kind,
    uint OptionCount,
    uint SelectedOption,
    float Confidence,
    float DistributionConfidence,
    float ExpectedScore,
    float ActProbability,
    DecisionRecommendation Recommendation);

public readonly record struct DecisionCapabilities(uint MaximumOptionsPerQuestion, bool Calibrated);

// How CreateDecisionTaskFromModel prepares a model. Unset properties keep the
// Runtime defaults: no program cache.
public sealed class DecisionModelOptions
{
    public string? CompiledProgramDirectory { get; init; }

    public CompiledProgramStorage? CompiledProgramStorage { get; init; }
}

public sealed unsafe class DecisionTaskFactory : WinMLObject, IWinMLInterface<DecisionTaskFactory>
{
    private DecisionTaskFactory(IWinMLDecisionTaskFactory* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLDecisionTaskFactory.IID_Guid;

    static DecisionTaskFactory IWinMLInterface<DecisionTaskFactory>.CreateFromOwnedPointer(nint pointer) => new((IWinMLDecisionTaskFactory*)pointer);

    // Reads a published decision model folder for target, a GPU target. A
    // null tokenizer creates the model's own from its folder. Null options
    // select the defaults. Throws NotSupportedException for a model or target
    // that Windows ML cannot run for decisions, including a CPU target.
    public DecisionTask CreateDecisionTaskFromModel(ExecutionTarget target, Model model, Tokenizer? tokenizer = null, DecisionModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(model);
        WINML_DECISION_MODEL_OPTIONS native = default;
        IWinMLDecisionTask* task = null;
        fixed (char* directory = options?.CompiledProgramDirectory)
        {
            if (options?.CompiledProgramDirectory is not null)
            {
                native.presentFields |= WINML_DECISION_MODEL_OPTION_FIELDS.WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY;
                native.compiledProgramDirectory = new PCWSTR(directory);
            }

            if (options?.CompiledProgramStorage is CompiledProgramStorage storage)
            {
                native.presentFields |= WINML_DECISION_MODEL_OPTION_FIELDS.WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE;
                native.compiledProgramStorage = (WINML_COMPILED_PROGRAM_STORAGE)storage;
            }

            Check(((IWinMLDecisionTaskFactory*)ThisPtr)->CreateDecisionTaskFromModel(
                target.Raw,
                tokenizer is null ? null : tokenizer.Raw,
                model.Raw,
                options is null ? null : &native,
                &task));
        }

        GC.KeepAlive(target);
        GC.KeepAlive(model);
        GC.KeepAlive(tokenizer);
        return new DecisionTask(Required(task));
    }
}

// Free-threaded; needs no Close.
public sealed unsafe class DecisionTask : WinMLObject, IWinMLInterface<DecisionTask>
{
    internal DecisionTask(IWinMLDecisionTask* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLDecisionTask.IID_Guid;

    static DecisionTask IWinMLInterface<DecisionTask>.CreateFromOwnedPointer(nint pointer) => new((IWinMLDecisionTask*)pointer);

    public DecisionCapabilities Capabilities
    {
        get
        {
            WINML_DECISION_CAPABILITIES capabilities;
            Check(((IWinMLDecisionTask*)ThisPtr)->GetCapabilities(&capabilities));
            return new DecisionCapabilities(capabilities.maximumOptionsPerQuestion, capabilities.calibrated);
        }
    }

    // Creates a session bound to the calling thread, and prepares the model
    // for the target.
    public DecisionSession CreateSession()
    {
        IWinMLDecisionSession* session = null;
        Check(((IWinMLDecisionTask*)ThisPtr)->CreateSession(&session));
        return new DecisionSession(Required(session));
    }
}

// Decides. Call it only on the thread that created it; Dispose closes it.
public sealed unsafe class DecisionSession : WinMLObject, IWinMLInterface<DecisionSession>
{
    private bool _closed;

    internal DecisionSession(IWinMLDecisionSession* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLDecisionSession.IID_Guid;

    static DecisionSession IWinMLInterface<DecisionSession>.CreateFromOwnedPointer(nint pointer) => new((IWinMLDecisionSession*)pointer);

    private IWinMLDecisionSession* Self => (IWinMLDecisionSession*)ThisPtr;

    public DecisionCapabilities Capabilities
    {
        get
        {
            WINML_DECISION_CAPABILITIES capabilities;
            Check(Self->GetCapabilities(&capabilities));
            return new DecisionCapabilities(capabilities.maximumOptionsPerQuestion, capabilities.calibrated);
        }
    }

    // Evaluates every question of request synchronously. Cancellation is
    // checked before each question and throws OperationCanceledException.
    public DecisionResult Decide(DecisionRequest request, CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        using NativeDecisionRequest native = new(request);
        IWinMLDecisionResult* result = null;
        Check(Self->Decide(native.Pointer, cancellation is null ? null : cancellation.Raw, &result));
        GC.KeepAlive(cancellation);
        return new DecisionResult(Required(result));
    }

    // Releases the session's execution resources. Results stay readable.
    public void Close()
    {
        if (!_closed)
        {
            Check(Self->Close());
            _closed = true;
        }
    }

    private protected override void OnDisposing()
    {
        if (!_closed)
        {
            _closed = true;
            _ = Self->Close();
        }
    }
}

// The answers of one Decide call. Immutable and free-threaded.
public sealed unsafe class DecisionResult : WinMLObject, IWinMLInterface<DecisionResult>
{
    internal DecisionResult(IWinMLDecisionResult* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLDecisionResult.IID_Guid;

    static DecisionResult IWinMLInterface<DecisionResult>.CreateFromOwnedPointer(nint pointer) => new((IWinMLDecisionResult*)pointer);

    private IWinMLDecisionResult* Self => (IWinMLDecisionResult*)ThisPtr;

    // One answer per question, in question order.
    public uint AnswerCount
    {
        get
        {
            uint count;
            Check(Self->GetAnswerCount(&count));
            return count;
        }
    }

    // The tokens the model evaluated.
    public ulong InputTokenCount
    {
        get
        {
            ulong count;
            Check(Self->GetInputTokenCount(&count));
            return count;
        }
    }

    public DecisionAnswer GetAnswer(uint index)
    {
        WINML_DECISION_ANSWER answer;
        Check(Self->GetAnswer(index, &answer));
        return new DecisionAnswer(
            (DecisionQuestionKind)answer.kind,
            answer.optionCount,
            answer.selectedOption,
            answer.confidence,
            answer.distributionConfidence,
            answer.expectedScore,
            answer.actProbability,
            (DecisionRecommendation)answer.recommendation);
    }

    // One probability per option of answer index, in option order.
    public float[] GetProbabilities(uint index)
    {
        float[] probabilities = new float[GetAnswer(index).OptionCount];
        GetProbabilities(index, probabilities);
        return probabilities;
    }

    // Copies the probabilities into destination, which must hold OptionCount values.
    public void GetProbabilities(uint index, Span<float> destination)
    {
        fixed (float* values = destination)
        {
            Check(Self->GetProbabilities(index, (uint)destination.Length, values));
        }
    }
}

// Owns unmanaged copies of a request's strings and arrays until disposed.
internal sealed unsafe class NativeDecisionRequest : IDisposable
{
    private readonly List<nint> _allocations = [];
    private WINML_DECISION_REQUEST* _request;

    public NativeDecisionRequest(DecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Questions);
        try
        {
            WINML_DECISION_REQUEST* native = Allocate<WINML_DECISION_REQUEST>(1);
            native->state = Copy(request.State ?? throw new ArgumentException("The request has no state.", nameof(request)));
            native->questionCount = (uint)request.Questions.Count;
            WINML_DECISION_QUESTION* questions = Allocate<WINML_DECISION_QUESTION>(request.Questions.Count);
            for (int i = 0; i < request.Questions.Count; ++i)
            {
                DecisionQuestion question = request.Questions[i] ?? throw new ArgumentException("A question is null.", nameof(request));
                IReadOnlyList<DecisionOption> options = question.Options ?? [];
                questions[i].kind = (WINML_DECISION_QUESTION_KIND)question.Kind;
                questions[i].instructions = Copy(question.Instructions ?? throw new ArgumentException("A question has no instructions.", nameof(request)));
                questions[i].optionCount = (uint)options.Count;
                WINML_DECISION_OPTION* nativeOptions = Allocate<WINML_DECISION_OPTION>(options.Count);
                for (int j = 0; j < options.Count; ++j)
                {
                    DecisionOption option = options[j] ?? throw new ArgumentException("An option is null.", nameof(request));
                    nativeOptions[j].label = Copy(option.Label);
                    nativeOptions[j].description = Copy(option.Description);
                }

                questions[i].options = nativeOptions;
                if (question.ActThreshold is float threshold)
                {
                    questions[i].presentFields = WINML_DECISION_QUESTION_FIELDS.WINML_DECISION_QUESTION_FIELDS_ACT_THRESHOLD;
                    questions[i].actThreshold = threshold;
                }
            }

            native->questions = questions;
            _request = native;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public WINML_DECISION_REQUEST* Pointer =>
        _request != null ? _request : throw new ObjectDisposedException(nameof(NativeDecisionRequest));

    public void Dispose()
    {
        _request = null;
        foreach (nint allocation in _allocations)
        {
            System.Runtime.InteropServices.NativeMemory.Free((void*)allocation);
        }

        _allocations.Clear();
    }

    // Returns null for an empty array, as the native request expects.
    private T* Allocate<T>(int count)
        where T : unmanaged
    {
        if (count == 0)
        {
            return null;
        }

        T* memory = (T*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(T));
        _allocations.Add((nint)memory);
        return memory;
    }

    private PCWSTR Copy(string? value)
    {
        if (value is null)
        {
            return default;
        }

        char* copy = (char*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(value.Length + 1), sizeof(char));
        _allocations.Add((nint)copy);
        value.AsSpan().CopyTo(new Span<char>(copy, value.Length));
        copy[value.Length] = '\0';
        return new PCWSTR(copy);
    }
}
