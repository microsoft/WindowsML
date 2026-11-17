// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Typed decisions with the Windows ML Task API, from C#.
//
// Ask a choice question about a state and read the answer's probabilities,
// confidence and recommendation. The task creates the model's own tokenizer
// from the model directory, so the app passes none. This is the C#
// counterpart of ..\main.cpp; it takes the same arguments and prints the same
// output.
//
// Run it
//   .\run_decision.ps1 -Language csharp [-ModelPath <model-directory>] [-State <text>]
//       [-ProgramCache <directory> [-CacheWeights]]
//
// The sample uses the sample wrapper in ..\..\..\..\Runtime\shared\csharp\Runtime,
// an idiomatic C# layer over the Runtime and Task APIs in the
// Microsoft.Windows.AI.MachineLearning package. Objects are IDisposable, and
// failures throw .NET exceptions that keep the HRESULT. A decision session
// belongs to the thread that created it; Decider keeps it on a thread of its
// own, so the sample can decide from any thread. The wrapper is sample code;
// see ..\..\..\..\Runtime\shared\csharp\README.md.

using System.Globalization;
using System.Text;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

namespace Decision;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Options? options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(
                "Usage: task-decision-csharp.exe <model-directory> [state] " +
                "[--program-cache <directory> [--cache-weights]]");
            return 2;
        }

        // The state is not limited to the console's code page.
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return await RunAsync(options);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Decision canceled.");
            return 1;
        }
        catch (Exception ex)
        {
            ReportFailure("Decision", ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync(Options options)
    {
        using WinMLRuntime runtime = WinMLRuntime.Create();
        using ExecutionTarget target = runtime.CreateExecutionTarget(ExecutionTargetKind.Gpu, ExecutionTargetPreference.Performance);
        using Model model = runtime.LoadModelFromPath(options.ModelDirectory);
        using WinMLTasks tasks = WinMLTasks.Create(runtime);

        // Sessions compile programs for the GPU. A program cache directory keeps
        // them for later runs, and with --cache-weights also the weights prepared
        // for each program. Null options select the defaults: no program cache.
        DecisionModelOptions? modelOptions = options.ProgramCache is null
            ? null
            : new DecisionModelOptions
            {
                CompiledProgramDirectory = options.ProgramCache,
                CompiledProgramStorage = options.CacheWeights ? CompiledProgramStorage.ProgramsAndWeights : null,
            };

        // A null tokenizer asks the task to create the model's own.
        DecisionTask task;
        try
        {
            task = tasks.Decision.CreateDecisionTaskFromModel(target, model, tokenizer: null, modelOptions);
        }
        catch (NotSupportedException)
        {
            Console.Error.WriteLine(
                "Windows ML cannot run this model for typed decisions on this GPU " +
                "(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). Use a supported decision model directory " +
                "and a GPU execution target.");
            return 1;
        }

        using (task)
        {
            await DecideAsync(tasks, task, options.State);
        }

        return 0;
    }

    private static async Task DecideAsync(WinMLTasks tasks, DecisionTask task, string state)
    {
        DecisionCapabilities capabilities = task.Capabilities;
        Console.WriteLine(
            $"Maximum options: {capabilities.MaximumOptionsPerQuestion.ToString(CultureInfo.InvariantCulture)}; " +
            $"calibrated probabilities: {(capabilities.Calibrated ? "yes" : "no")}");

        DecisionOption[] options =
        [
            new("billing", "A question about an invoice or payment"),
            new("technical", "A problem using the product"),
        ];
        if (capabilities.MaximumOptionsPerQuestion < options.Length)
        {
            throw WinMLException.NotSupported("The model takes fewer options per question than the sample asks.");
        }

        DecisionQuestion question = new()
        {
            Kind = DecisionQuestionKind.Choice,
            Instructions = "Which team should handle this support ticket?",
            Options = options,
        };
        DecisionRequest request = new() { State = state, Questions = [question] };
        Console.WriteLine($"State: {request.State}");
        Console.WriteLine($"Question: {question.Instructions}");

        await using Decider decider = await Decider.CreateAsync(tasks, task);
        using DecisionResult result = await DecideWithCtrlCAsync(decider, request);

        // Closing the session reports a failure that disposing it would not.
        await decider.CloseAsync();

        // Results are immutable and remain readable after the session closes.
        DecisionAnswer answer = result.GetAnswer(0);
        if (answer.OptionCount != options.Length || answer.SelectedOption >= options.Length)
        {
            throw WinMLException.Unexpected("The answer does not match the question's options.");
        }

        float[] probabilities = new float[options.Length];
        result.GetProbabilities(0, probabilities);
        for (int index = 0; index < options.Length; ++index)
        {
            Console.WriteLine($"{options[index].Label}: {Format(probabilities[index])}");
        }

        ulong inputTokenCount = result.InputTokenCount;
        Console.WriteLine(
            $"Selected: {options[answer.SelectedOption].Label}; confidence: {Format(answer.Confidence)}; " +
            $"recommendation: {(answer.Recommendation == DecisionRecommendation.Act ? "act" : "escalate")}");
        Console.WriteLine($"Input tokens: {inputTokenCount.ToString(CultureInfo.InvariantCulture)}");
    }

    // Ctrl+C cancels the decision through the Task's cancellation source, which
    // the Runtime checks before each question. The source is not disposed: the
    // console handler can still be running on its own thread when the decision
    // ends, and a source without timers holds nothing that needs releasing.
    private static async Task<DecisionResult> DecideWithCtrlCAsync(Decider decider, DecisionRequest request)
    {
        CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancel;
        try
        {
            return await decider.DecideAsync(request, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    // Four decimal places, as printf's %.4f prints the value widened to double.
    private static string Format(float value)
    {
        return ((double)value).ToString("F4", CultureInfo.InvariantCulture);
    }

    // Prints the HRESULT of a failed sample and what usually causes it.
    private static void ReportFailure(string operation, Exception ex)
    {
        Console.Error.WriteLine($"{operation} failed: 0x{ex.HResult:X8}");
        switch (ex)
        {
            case InvalidCastException:
                Console.Error.WriteLine(
                    "The Windows ML package deployed with this app does not provide this API. Build with a package " +
                    "that includes model-backed Tasks; see the model-backed README.");
                break;
            case NotSupportedException:
                Console.Error.WriteLine(
                    "Windows ML does not support this model, option or execution target for this Task.");
                break;
            case FileNotFoundException:
            case DirectoryNotFoundException:
                Console.Error.WriteLine("A model or input file was not found. Check the paths.");
                break;
        }
    }
}

internal sealed record Options(string ModelDirectory, string State, string? ProgramCache, bool CacheWeights)
{
    // Parses the arguments as the C++ sample does. Returns null for arguments
    // that need the usage text.
    public static Options? Parse(string[] args)
    {
        string? modelDirectory = null;
        string state = "I was charged twice for my subscription.";
        string? programCache = null;
        bool cacheWeights = false;
        int positionalCount = 0;
        for (int index = 0; index < args.Length; ++index)
        {
            string argument = args[index];
            if (argument == "--program-cache" && index + 1 < args.Length)
            {
                programCache = args[++index];
            }
            else if (argument == "--cache-weights")
            {
                cacheWeights = true;
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal) || positionalCount == 2)
            {
                return null;
            }
            else if (positionalCount++ == 0)
            {
                modelDirectory = argument;
            }
            else
            {
                state = argument;
            }
        }

        if (modelDirectory is null || (cacheWeights && programCache is null))
        {
            return null;
        }

        return new Options(modelDirectory, state, programCache, cacheWeights);
    }
}
