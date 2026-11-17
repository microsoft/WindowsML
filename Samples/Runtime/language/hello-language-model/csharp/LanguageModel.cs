// Copyright (C) Microsoft Corporation. All rights reserved.
//
// One unified decoder stage with a tokenizer and a greedy decode loop: the C#
// counterpart of ..\..\..\shared\language_model_loader.h and the logits
// helpers in ..\..\..\shared\llm_inference.h that hello-language-model uses.
//
//   Load: LoadModelFromFile selects ONNX Runtime or llama.cpp from the
//   artifact. AddModelStage places it on the stage target, RequestOutput(0)
//   keeps the logits, and StatefulStageOptions declares the ONNX past/present
//   pairs (AddStateTensorPair) and any sequence capacity before Build, so the
//   Runtime carries the KV cache between runs. A GGUF stage keeps its own
//   state and declares no pairs. Tokenizer.CreateFromFile then creates the
//   tokenizer from the ONNX model's directory or from the GGUF file.
//
//   Generate: ResetExecutionState, encode the prompt (or format it with the
//   model's chat template), run it once, then repeat: read the last logits
//   row on a CPU target, take the argmax, decode and print the fragment, and
//   run that token. The loop stops at an end-of-sequence token, the token
//   limit, or the sequence capacity.

using System.Text;
using WinMLSamples.Runtime;
using WinMLSamples;

namespace HelloLanguageModel;

internal enum LanguageArtifactKind
{
    Unknown,
    Onnx,
    Gguf,
}

internal enum StopReason
{
    EndOfSequence,
    MaxTokens,
    Capacity,
}

internal sealed class GenerationResult
{
    public StopReason StopReason { get; set; } = StopReason.MaxTokens;

    // The generated token ids, excluding the end-of-sequence token that stops
    // the loop.
    public List<uint> Tokens { get; } = new();

    public StringBuilder Text { get; } = new();

    public int PromptTokenCount { get; set; }

    public int GeneratedTokenCount => Tokens.Count;
}

internal static class LanguageArtifacts
{
    public static LanguageArtifactKind GetKind(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".onnx" or ".ort" => LanguageArtifactKind.Onnx,
            ".gguf" => LanguageArtifactKind.Gguf,
            _ => LanguageArtifactKind.Unknown,
        };
    }

    public static string Name(LanguageArtifactKind kind)
    {
        return kind switch
        {
            LanguageArtifactKind.Onnx => "ONNX / ORT",
            LanguageArtifactKind.Gguf => "GGUF / llama.cpp",
            _ => "unknown",
        };
    }
}

internal sealed class LanguageModel : IDisposable
{
    public const uint DefaultMaxNewTokens = 256;

    private readonly WinMLRuntime _runtime;
    private readonly ExecutionTarget _stageTarget;
    private readonly Model _model;
    private readonly PipelineBuilder _builder;
    private readonly Stage _stage;
    private readonly Pipeline _pipeline;
    private readonly StatefulStage _statefulStage;
    private readonly Tokenizer _tokenizer;
    private readonly TokenizerDecoder _decoder;
    private readonly StructuredConversationFormatter? _formatter;

    // Token tensors are created, and logits rows read back, on a CPU target.
    private readonly ExecutionTarget _tokenTarget;
    private readonly TensorDataType _tokenDataType;
    private readonly int _tokenRank;

    // Zero when the model leaves the logits vocabulary dimension free.
    private readonly uint _vocabularySize;
    private readonly uint[] _eosTokens;

    private readonly List<IDisposable> _owned = new();

    private LanguageModel(
        string modelPath,
        string? tokenizerSource,
        LanguageArtifactKind artifactKind,
        DeviceOptions device,
        ulong contextCapacityHint,
        bool enableDiagnostics)
    {
        // Objects created before a failure are released before the exception propagates.
        try
        {
            _runtime = Own(WinMLRuntime.Create());

            // The provider that --ep requests was prepared before loading, as in
            // the C++ sample, so only the target is created here.
            _stageTarget = Own(ExecutionTargets.Create(_runtime, device));

            // Unified load: one call selects the backend from the artifact, then
            // the rest of the setup uses the same pipeline objects.
            _model = Own(_runtime.LoadModelFromFile(modelPath));

            _builder = Own(_runtime.CreatePipelineBuilder());
            _stage = Own(_builder.AddModelStage(_model, _stageTarget, "decoder"));
            _stage.RequestOutput(0);

            // Declare any ONNX KV-cache pairs before Build; GGUF-backed stages own
            // their state and report no declared pairs here.
            using (StatefulStageOptions options = _stage.As<StatefulStageOptions>())
            {
                ulong declaredCapacity = DeclareDecoderStateTensorPairs(_model, options);
                ulong capacityHint = contextCapacityHint != 0 ? contextCapacityHint : declaredCapacity;
                if (capacityHint != 0)
                {
                    options.SequenceCapacityHint = capacityHint;
                }
            }

            if (enableDiagnostics)
            {
                StageDiagnostics.Enable(_stage);
            }

            _pipeline = Own(_builder.Build());
            _statefulStage = Own(_stage.As<StatefulStage>());

            using StageSchema schema = _stage.As<StageSchema>();
            TensorDescription tokenDescription = schema.GetInputTensorDescription(0);
            if (tokenDescription.DataType is not (TensorDataType.Int32 or TensorDataType.Int64))
            {
                throw WinMLException.NotSupported($"The decoder's token input is {tokenDescription.DataType}, not Int32 or Int64.");
            }

            if (tokenDescription.Dimensions.Length is not (1 or 2))
            {
                throw WinMLException.NotSupported($"The decoder's token input has rank {tokenDescription.Dimensions.Length}, not 1 or 2.");
            }

            _tokenDataType = tokenDescription.DataType;
            _tokenRank = tokenDescription.Dimensions.Length;

            // Tokenizer source is the ONNX model directory or the GGUF artifact
            // unless the caller supplies an override.
            _tokenizer = Own(Tokenizer.CreateFromFile(ResolveTokenizerSource(modelPath, tokenizerSource, artifactKind)));
            _decoder = Own(_tokenizer.CreateDecoder());
            if (_tokenizer.TryAs(out StructuredConversationFormatter? formatter))
            {
                _formatter = Own(formatter);
            }

            // The fixed logits vocabulary is a fact of the materialized stage, so it
            // is cached once here and used to range-check every tokenizer special
            // token and prompt token before the backend runs.
            _vocabularySize = GetStageVocabularySize(schema);
            _eosTokens = ReadTokenizerMetadata();

            _tokenTarget = Own(_runtime.CreateCpuExecutionTarget());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Stage Stage => _stage;

    public static LanguageModel Load(
        string modelPath,
        string? tokenizerSource,
        DeviceOptions device,
        ulong contextCapacityHint,
        bool enableDiagnostics)
    {
        LanguageArtifactKind artifactKind = LanguageArtifacts.GetKind(modelPath);
        if (artifactKind == LanguageArtifactKind.Unknown)
        {
            throw WinMLException.NotSupported($"Unsupported language artifact: {modelPath}");
        }

        if (artifactKind == LanguageArtifactKind.Gguf && !string.IsNullOrEmpty(device.Ep))
        {
            throw WinMLException.NotSupported("--ep selects an ORT provider and cannot be used with GGUF.");
        }

        if (artifactKind == LanguageArtifactKind.Gguf && device.Kind == ExecutionTargetKind.Npu)
        {
            throw WinMLException.NotSupported("llama.cpp runs GGUF models on CPU or GPU, not NPU.");
        }

        return new LanguageModel(modelPath, tokenizerSource, artifactKind, device, contextCapacityHint, enableDiagnostics);
    }

    public ExecutionTargetKind GetResolvedTargetKind()
    {
        using ExecutionTarget target = _stage.GetExecutionTarget();
        return target.Kind;
    }

    public GenerationResult GenerateTokens(string prompt, uint maxNewTokens, Action<string> onFragment)
    {
        return Generate(() => _tokenizer.Encode(prompt, TokenizerEncodeFlags.AddSpecialTokens), maxNewTokens, onFragment);
    }

    public GenerationResult GenerateConversation(string role, string content, uint maxNewTokens, Action<string> onFragment)
    {
        StructuredConversationFormatter formatter = _formatter
            ?? throw WinMLException.NoInterface("The tokenizer does not provide a structured conversation formatter.");
        return Generate(() => FormatConversation(formatter, role, content), maxNewTokens, onFragment);
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; --i)
        {
            _owned[i].Dispose();
        }

        _owned.Clear();
    }

    private T Own<T>(T value)
        where T : IDisposable
    {
        _owned.Add(value);
        return value;
    }

    // A stateful decoder must declare its key/value slots before Build; the
    // Runtime never infers persistent state from tensor names or order. A
    // unified export lays the cache out as trailing tensors, each past_* input
    // lining up with the present_* output at the same offset from the end, so
    // walk back from the end while the declared descriptors agree. The model
    // schema, not the stage schema, is read because the stage schema exists
    // only after Build. Returns the sequence axis of a rank-4 cache
    // [batch, heads, sequence, head_dim] when it is fixed, or zero.
    private static ulong DeclareDecoderStateTensorPairs(Model model, StatefulStageOptions options)
    {
        if (!model.TryAs(out ModelSchema? schema))
        {
            return 0;
        }

        using (schema)
        {
            uint inputCount;
            uint outputCount;
            try
            {
                inputCount = schema.InputCount;
                outputCount = schema.OutputCount;
            }
            // Like the C++ loader, any failure to read the schema means the
            // model declares no state pairs.
            catch (Exception)
            {
                return 0;
            }

            if (inputCount < 2 || outputCount < 2)
            {
                return 0;
            }

            uint candidateCount = Math.Min(inputCount - 1, outputCount - 1);
            uint matched = 0;
            while (matched < candidateCount &&
                TryGetDescription(schema, isInput: true, inputCount - 1 - matched, out TensorSchemaDescription stateInput) &&
                TryGetDescription(schema, isInput: false, outputCount - 1 - matched, out TensorSchemaDescription stateOutput) &&
                DescriptorsMatch(stateInput, stateOutput))
            {
                ++matched;
            }

            for (uint pair = 0; pair < matched; ++pair)
            {
                options.AddStateTensorPair(inputCount - matched + pair, outputCount - matched + pair);
            }

            if (matched == 0 || !TryGetDescription(schema, isInput: true, inputCount - matched, out TensorSchemaDescription firstState))
            {
                return 0;
            }

            ulong[] dimensions = firstState.Dimensions;
            return dimensions.Length == 4 && dimensions[2] > 0 && dimensions[2] != ulong.MaxValue ? dimensions[2] : 0;
        }
    }

    private static bool TryGetDescription(ModelSchema schema, bool isInput, uint index, out TensorSchemaDescription description)
    {
        try
        {
            description = isInput ? schema.GetInputTensorDescription(index) : schema.GetOutputTensorDescription(index);
            return true;
        }
        catch (Exception)
        {
            description = default;
            return false;
        }
    }

    // Two logical state tensors pair up only when their declared descriptors match.
    private static bool DescriptorsMatch(TensorSchemaDescription first, TensorSchemaDescription second)
    {
        return first.DataType == second.DataType && first.Dimensions.AsSpan().SequenceEqual(second.Dimensions);
    }

    private static string ResolveTokenizerSource(string modelPath, string? tokenizerSource, LanguageArtifactKind artifactKind)
    {
        if (!string.IsNullOrEmpty(tokenizerSource))
        {
            return tokenizerSource;
        }

        if (artifactKind == LanguageArtifactKind.Onnx)
        {
            string? directory = Path.GetDirectoryName(modelPath);
            return string.IsNullOrEmpty(directory) ? Environment.CurrentDirectory : directory;
        }

        return modelPath;
    }

    // The last logits axis is the vocabulary. Zero means the backend left it
    // free, or it covers every UINT32 token id, so range checks are skipped.
    private static uint GetStageVocabularySize(StageSchema schema)
    {
        ulong[] logits = schema.GetOutputTensorDescription(0).Dimensions;
        if (logits.Length == 0)
        {
            throw WinMLException.InvalidData("The decoder's logits output has no dimensions.");
        }

        ulong extent = logits[^1];
        if (extent == 0)
        {
            throw WinMLException.InvalidData("The decoder's logits vocabulary is empty.");
        }

        return extent == ulong.MaxValue || extent > uint.MaxValue ? 0 : (uint)extent;
    }

    // EOS is required: a greedy loop with no end-of-sequence token runs to the
    // token limit on every prompt. BOS stays optional - the chat template
    // inserts it - but it is range-checked when the tokenizer declares one.
    private uint[] ReadTokenizerMetadata()
    {
        if (!_tokenizer.TryAs(out TokenizerMetadata? metadata))
        {
            throw WinMLException.NoInterface("tokenizer does not expose required EOS metadata.");
        }

        using (metadata)
        {
            uint[] eos = metadata.GetEosTokenIds();
            if (eos.Length == 0)
            {
                throw WinMLException.InvalidData("tokenizer EOS metadata is empty.");
            }

            ValidateTokenIds(eos, "tokenizer EOS");
            if (metadata.TryGetBosTokenId(out uint beginningToken))
            {
                ValidateTokenIds([beginningToken], "tokenizer BOS");
            }

            return eos;
        }
    }

    // Rejects token ids the model's vocabulary cannot represent.
    private void ValidateTokenIds(ReadOnlySpan<uint> tokens, string label)
    {
        if (_vocabularySize == 0)
        {
            return;
        }

        foreach (uint token in tokens)
        {
            if (token >= _vocabularySize)
            {
                throw WinMLException.InvalidData(
                    $"{label} token id {token} is outside the model vocabulary ({_vocabularySize}). Tokenizer and model do not match.");
            }
        }
    }

    // The formatter renders the model's own chat template and ends the prompt
    // with the assistant generation prompt.
    private static uint[] FormatConversation(StructuredConversationFormatter formatter, string role, string content)
    {
        ConversationRequest request = new()
        {
            FormatterMode = ConversationFormatterMode.ModelDefault,
            Messages = [new ConversationMessage { Role = role, Content = content }],
            AddGenerationPrompt = true,
        };

        using ConversationFormatResult formatted = formatter.FormatConversation(request);
        return formatted.GetTokenIds();
    }

    private GenerationResult Generate(Func<uint[]> encodePrompt, uint maxNewTokens, Action<string> onFragment)
    {
        _pipeline.ResetExecutionState();
        _decoder.Reset();

        uint[] promptTokens = encodePrompt();
        if (promptTokens.Length == 0)
        {
            throw new ArgumentException("The prompt encoded to no tokens.");
        }

        ValidateTokenIds(promptTokens, "prompt");
        GenerationResult result = new() { PromptTokenCount = promptTokens.Length };

        // Pattern 3: query the built stateful stage for the effective sequence
        // capacity before running the prompt.
        ulong capacity = _statefulStage.SequenceCapacity;
        if (capacity != 0 && (ulong)promptTokens.Length >= capacity)
        {
            result.StopReason = StopReason.Capacity;
            return result;
        }

        RunTokens(promptTokens);

        uint tokenLimit = maxNewTokens == 0 ? DefaultMaxNewTokens : maxNewTokens;
        for (uint index = 0; index < tokenLimit; ++index)
        {
            ulong position = _statefulStage.SequencePosition;
            if (capacity != 0 && position >= capacity)
            {
                result.StopReason = StopReason.Capacity;
                break;
            }

            // Pattern 9: read logits from the retained output, choose the next
            // token in the app, and decode a streaming fragment.
            uint token = ChooseNextToken();
            if (Array.IndexOf(_eosTokens, token) >= 0)
            {
                result.StopReason = StopReason.EndOfSequence;
                break;
            }

            result.Tokens.Add(token);
            string fragment = _decoder.DecodeToken(token);
            result.Text.Append(fragment);
            onFragment(fragment);

            if (capacity != 0 && position + 1 >= capacity)
            {
                result.StopReason = StopReason.Capacity;
                break;
            }

            if (index + 1 < tokenLimit)
            {
                RunTokens([token]);
            }
        }

        return result;
    }

    // Binds the tokens as the decoder's input 0, shaped [n] or [1, n] in the
    // stage's token type, and runs the pipeline.
    private void RunTokens(uint[] tokens)
    {
        long[] shape = _tokenRank == 2 ? [1, tokens.Length] : [tokens.Length];
        using Tensor tensor = _tokenDataType == TensorDataType.Int32
            ? _tokenTarget.CreateTensor<int>(Array.ConvertAll(tokens, token => (int)token), shape)
            : _tokenTarget.CreateTensor<long>(Array.ConvertAll(tokens, token => (long)token), shape);
        _stage.BindInput(0, tensor);
        _pipeline.Run();
    }

    // Chooses the next token from the final-position row of the logits
    // output. For a rank-2 or higher output, RawTensorFactory.CreateTensorFromRegion
    // on the CPU target views that one row, so the sample reads one
    // vocabulary-sized row instead of the whole sequence.
    private uint ChooseNextToken()
    {
        using Tensor logits = _stage.GetOutput(0);
        long[] shape = logits.Shape;
        if (shape.Length == 0)
        {
            throw WinMLException.InvalidData("The logits output has no dimensions.");
        }

        long vocabularyExtent = shape[^1];
        if (vocabularyExtent == 0 || vocabularyExtent > uint.MaxValue)
        {
            throw WinMLException.InvalidData($"The logits vocabulary extent {vocabularyExtent} is not usable.");
        }

        int vocabularySize = (int)vocabularyExtent;
        if (shape.Length < 2)
        {
            return ArgmaxOfLastRow(logits, vocabularySize);
        }

        uint[] starts = new uint[shape.Length];
        uint[] extents = new uint[shape.Length];
        for (int axis = 0; axis < shape.Length; ++axis)
        {
            if (shape[axis] == 0 || shape[axis] > uint.MaxValue)
            {
                throw WinMLException.InvalidData("The logits output has a dynamic or empty dimension.");
            }

            extents[axis] = 1;
        }

        starts[^2] = (uint)(shape[^2] - 1);
        extents[^1] = (uint)vocabularySize;
        using Tensor row = _tokenTarget.RawTensorFactory.CreateTensorFromRegion(logits, starts, extents);
        return ArgmaxOfLastRow(row, vocabularySize);
    }

    // Greedy selection over the last vocabularySize values of a logits tensor,
    // read in place through the lock's Span.
    private static uint ArgmaxOfLastRow(Tensor tensor, int vocabularySize)
    {
        long elementCount = tensor.ElementCount;
        if (elementCount < vocabularySize)
        {
            throw WinMLException.InvalidData("The logits row is smaller than the vocabulary.");
        }

        int offset = checked((int)(elementCount - vocabularySize));
        switch (tensor.DataType)
        {
            case TensorDataType.Float32:
            {
                using TensorLock<float> data = LockForRead<float>(tensor);
                return Argmax(ElementsOf(data.AsReadOnlySpan(), elementCount).Slice(offset, vocabularySize));
            }

            case TensorDataType.Float16:
            {
                using TensorLock<Half> data = LockForRead<Half>(tensor);
                return Argmax(ElementsOf(data.AsReadOnlySpan(), elementCount).Slice(offset, vocabularySize));
            }

            default:
                throw WinMLException.NotSupported($"Unsupported logits tensor data type {(int)tensor.DataType}.");
        }
    }

    private static ReadOnlySpan<T> ElementsOf<T>(ReadOnlySpan<T> data, long elementCount)
    {
        return data.Length >= elementCount ? data : throw WinMLException.InvalidData("The logits buffer is smaller than its shape.");
    }

    // A plain CPU read lock succeeds for CPU-resident tensors. A tensor on an
    // accelerator refuses it and needs a synchronized lock, which copies the
    // data back to CPU-visible memory.
    private static TensorLock<T> LockForRead<T>(Tensor tensor)
        where T : unmanaged
    {
        try
        {
            return tensor.Lock<T>(TensorLockMode.Read, TensorLockFlags.None);
        }
        catch (NotSupportedException)
        {
            return tensor.Lock<T>(TensorLockMode.Read, TensorLockFlags.AllowSynchronizedCpuAccess);
        }
    }

    // The first highest logit, as std::max_element picks in the C++ sample.
    // Argmax keeps replies repeatable.
    private static uint Argmax(ReadOnlySpan<float> logits)
    {
        int best = 0;
        for (int i = 1; i < logits.Length; ++i)
        {
            if (logits[i] > logits[best])
            {
                best = i;
            }
        }

        return (uint)best;
    }

    private static uint Argmax(ReadOnlySpan<Half> logits)
    {
        int best = 0;
        float bestValue = (float)logits[0];
        for (int i = 1; i < logits.Length; ++i)
        {
            float value = (float)logits[i];
            if (value > bestValue)
            {
                best = i;
                bestValue = value;
            }
        }

        return (uint)best;
    }
}