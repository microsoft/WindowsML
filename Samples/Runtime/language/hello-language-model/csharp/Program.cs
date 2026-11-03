// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Hello LanguageModel from C#: stream one greedy response from a unified decoder.
//
// Load an ONNX/ORT or GGUF language model, send it one prompt, and print the
// reply as it is generated. This is the C# counterpart of ..\main.cpp and
// prints the same reply.
//
//   LanguageModel.Load (LanguageModel.cs) loads the artifact, which selects
//   ONNX Runtime or llama.cpp, as one "decoder" stage. For ONNX it declares
//   the past/present pairs with AddStateTensorPair, so the Runtime carries
//   the KV cache between runs; a GGUF stage keeps its own. Generation resets
//   execution state, applies the chat template (or plain encoding with
//   --raw), and runs the whole prompt once. Each step then reads the last
//   logits row on a CPU target, takes the argmax, prints the decoded
//   fragment, and runs that token, until end of sequence, 256 new tokens, or
//   the sequence capacity.
//
// Run it
//   .\run_hello_language_model.ps1 -Language csharp [-Backend ort|llama]
//       [-ModelPath <path>] [-TokenizerSource <path>] [-Device cpu|gpu|npu]
//       [-Ep <name>] [-Prompt <text>] [-Raw]
//
// The Runtime API is a COM-style C API. CsWin32 generates the C# bindings when
// the project builds, from the Win32 metadata in the
// Microsoft.Windows.AI.MachineLearning package; NativeMethods.txt here and in
// ..\..\..\shared\csharp lists the APIs to generate. Generated interface
// methods throw an exception for a failure HRESULT and return their
// [out, retval] value.
//
// Learn more (paths relative to this file)
//   ../../../../../docs/Runtime/tutorials/03-language-models.md
//   ../../../../../docs/Runtime/tutorials/07-gguf-language-models.md
//   ../../../../../docs/api-reference/CommonPatterns.md (patterns 3 and 9)
//   ../../../../../docs/api-reference/IWinMLStatefulStage.md
//   ../../../../../docs/api-reference/IWinMLTokenizer.md

using System.Text;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using WinMLSamples;

namespace HelloLanguageModel;

internal static unsafe class Program
{
    private const int S_FALSE = 1;

    private static int Main(string[] args)
    {
        // The model streams UTF-8 text fragments, as the C++ sample's _O_U8TEXT stdout does.
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        Options? options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Options.PrintUsage();
            return 2;
        }

        if (options is null)
        {
            Options.PrintUsage();
            return 0;
        }

        string modelPath = options.ModelPath ?? SampleAssets.FindModel(Path.Combine("llm", "model.onnx"));
        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine(
                "Could not find the language model. Run check_artifacts.ps1 for the selected language artifact or pass --model.");
            return 1;
        }

        LanguageArtifactKind artifactKind = LanguageArtifacts.GetKind(modelPath);
        if (artifactKind == LanguageArtifactKind.Unknown)
        {
            Console.Error.WriteLine($"Unsupported language artifact: {modelPath}");
            return 2;
        }

        DeviceOptions device = options.Device;
        if (artifactKind == LanguageArtifactKind.Gguf && !string.IsNullOrEmpty(device.Ep))
        {
            Console.Error.WriteLine("--ep selects an ORT provider and cannot be used with GGUF.");
            return 2;
        }

        try
        {
            if (device.Verbose)
            {
                // Must run before the Runtime creates the ONNX Runtime environment.
                ExecutionProviders.EnableVerboseLogging();
            }

            // Registers the provider that --ep names, and requires one for
            // --device npu. GGUF models do not run on ONNX Runtime providers.
            if (artifactKind == LanguageArtifactKind.Onnx)
            {
                ExecutionProviders.Prepare(device);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Artifact: {LanguageArtifacts.Name(artifactKind)}");
        Console.WriteLine($"Loading {modelPath}...");

        // LanguageModel.Load performs the Runtime setup: LoadModelFromFile ->
        // CreatePipelineBuilder -> AddModelStage -> RequestOutput -> state
        // options -> Build -> tokenizer/decoder creation.
        LanguageModel model;
        try
        {
            model = LanguageModel.Load(modelPath, options.TokenizerSource, device, options.ContextCapacity, device.Verbose);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(
                $"LoadModel failed with HRESULT 0x{ex.HResult:X8}. Verify the artifact, tokenizer, target, and backend files.");
            return 1;
        }

        using (model)
        {
            try
            {
                Console.WriteLine($"Resolved target: {DeviceOptions.KindName(model.GetResolvedTargetKind())}");
                if (device.Verbose)
                {
                    StageDiagnostics.Print(model.Stage, model.Objects);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to inspect the resolved target (0x{ex.HResult:X8}).");
                return 1;
            }

            // The model owns the manual loop from CommonPatterns.md: encode the
            // prompt, bind token tensors, Run, read logits, argmax, and
            // DecodeToken fragments.
            GenerationResult result = options.Raw
                ? model.GenerateTokens(options.Prompt, 0, Console.Write)
                : model.GenerateConversation("user", options.Prompt, 0, Console.Write);

            if (result.HResult == S_FALSE && result.GeneratedTokenCount != 0)
            {
                Console.WriteLine();
                Console.WriteLine("[stopped at the model sequence capacity]");
                return 0;
            }

            if (result.HResult == S_FALSE)
            {
                Console.Error.WriteLine("The prompt does not fit the model sequence capacity.");
                return 1;
            }

            if (result.HResult < 0)
            {
                Console.Error.WriteLine(
                    $"{(options.Raw ? "GenerateTokens" : "GenerateConversation")} failed with HRESULT 0x{result.HResult:X8}.");
                if (!options.Raw)
                {
                    Console.Error.WriteLine("Use --raw to distinguish model execution from chat-template formatting.");
                }

                return 1;
            }

            Console.WriteLine();
            return 0;
        }
    }
}

internal sealed record Options(
    DeviceOptions Device,
    string? ModelPath,
    string? TokenizerSource,
    string Prompt,
    ulong ContextCapacity,
    bool Raw)
{
    public const string DefaultPrompt = "Reply with one short greeting.";
    private const ulong MaximumContextCapacity = 1_048_576;

    // Returns null when help is requested. Like the C++ sample, the first
    // positional argument is the model (unless --model is given) and the
    // second is the prompt.
    public static Options? Parse(string[] args)
    {
        DeviceOptions device = new();
        string? model = null;
        string? tokenizer = null;
        string prompt = DefaultPrompt;
        ulong context = 0;
        bool raw = false;
        List<string> positional = new();
        for (int i = 0; i < args.Length; ++i)
        {
            if (device.TryParse(args, ref i))
            {
                continue;
            }

            switch (args[i].ToLowerInvariant())
            {
                case "--model":
                    model = DeviceOptions.Value(args, ref i);
                    break;
                case "--tokenizer":
                    tokenizer = DeviceOptions.Value(args, ref i);
                    break;
                case "--prompt":
                    prompt = DeviceOptions.Value(args, ref i);
                    break;
                case "--context":
                    string value = DeviceOptions.Value(args, ref i);
                    if (!ulong.TryParse(value, System.Globalization.NumberStyles.None, null, out context) ||
                        context > MaximumContextCapacity)
                    {
                        throw new ArgumentException($"--context must be a decimal integer from 0 through {MaximumContextCapacity}.");
                    }

                    break;
                case "--raw":
                    raw = true;
                    break;
                case "-h":
                case "--help":
                case "/?":
                    return null;
                default:
                    if (args[i].Length == 0 || args[i][0] == '-')
                    {
                        throw new ArgumentException($"Unrecognized argument: {args[i]}");
                    }

                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count > 2)
        {
            throw new ArgumentException($"Unexpected argument: {positional[2]}");
        }

        model ??= positional.Count > 0 ? positional[0] : null;
        if (positional.Count > 1)
        {
            prompt = positional[1];
        }

        return new Options(device, model, tokenizer, prompt, context, raw);
    }

    public static void PrintUsage()
    {
        Console.WriteLine($"Usage: hello-language-model-csharp.exe [model] [\"prompt\"] [options] {DeviceOptions.Usage}");
        Console.WriteLine("  --model PATH       Unified ONNX/ORT decoder or GGUF decoder (default: models\\llm\\model.onnx).");
        Console.WriteLine("  --tokenizer PATH   Optional tokenizer directory/file override.");
        Console.WriteLine($"  --prompt TEXT      Prompt text (default: \"{DefaultPrompt}\").");
        Console.WriteLine("  --context TOKENS   Optional sequence-capacity hint.");
        Console.WriteLine("  --raw              Skip the model chat template.");
        Console.WriteLine("  --device cpu|gpu|npu selects the decoder's target; GGUF models run on CPU or GPU.");
        Console.WriteLine("  --ep NAME          ORT execution-provider pin; invalid for GGUF. --device npu requires it.");
        Console.WriteLine("  --performance/--efficiency steer adapter selection when several adapters are present.");
        Console.WriteLine("  --verbose shows ONNX Runtime node placement and the stage's execution diagnostics.");
    }
}
