// Copyright (C) Microsoft Corporation. All rights reserved.
//
// One unified decoder stage with a tokenizer and a greedy decode loop: the C#
// counterpart of ..\..\..\shared\language_model_loader.h and the logits
// helpers in ..\..\..\shared\llm_inference.h that hello-language-model uses.
//
//   Load: LoadModelFromFile selects ONNX Runtime or llama.cpp from the
//   artifact. AddModelStage places it on the stage target, RequestOutput(0)
//   keeps the logits, and IWinMLStatefulStageOptions declares the ONNX
//   past/present pairs (AddStateTensorPair) and any sequence capacity before
//   Build, so the Runtime carries the KV cache between runs. A GGUF stage
//   keeps its own state and declares no pairs. WinMLCreateTokenizerFromFile
//   then creates the tokenizer from the ONNX model's directory or from the
//   GGUF file.
//
//   Generate: ResetExecutionState, encode the prompt (or format it with the
//   model's chat template), run it once, then repeat: read the last logits
//   row on a CPU target, take the argmax, decode and print the fragment, and
//   run that one token. The loop stops at an end-of-sequence token, the token
//   limit, or the sequence capacity.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;
using WinMLSamples;
using Win32 = Windows.Win32.PInvoke;

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
    Error,
}

internal sealed class GenerationResult
{
    // S_OK, S_FALSE when generation stopped at the sequence capacity, or the
    // failure HRESULT.
    public int HResult { get; set; }

    public StopReason StopReason { get; set; } = StopReason.MaxTokens;

    // The generated token ids, excluding the end-of-sequence token that stops
    // the loop.
    public List<uint> Tokens { get; } = new();

    public System.Text.StringBuilder Text { get; } = new();

    public uint PromptTokenCount { get; set; }

    public uint GeneratedTokenCount => (uint)Tokens.Count;
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

internal sealed unsafe class LanguageModel : IDisposable
{
    public const uint DefaultMaxNewTokens = 256;

    private const int S_FALSE = 1;
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int ErrorNotSupported = unchecked((int)0x80070032);
    private const int ErrorInvalidData = unchecked((int)0x8007000D);

    private readonly ComObjects _objects = new();
    private IWinMLStage* _stage;
    private IWinMLStatefulStage* _statefulStage;
    private IWinMLPipeline* _pipeline;
    private IWinMLTokenizer* _tokenizer;
    private IWinMLStructuredConversationFormatter* _formatter;
    private IWinMLTokenizerDecoder* _decoder;
    private IWinMLRawTensorFactory* _tokenTensorFactory;
    private WINML_TENSOR_DATA_TYPE _tokenDataType;
    private uint _tokenDimensionCount;

    // Zero when the model leaves the logits vocabulary dimension free.
    private uint _vocabularySize;
    private uint[] _eosTokens = Array.Empty<uint>();

    private LanguageModel()
    {
    }

    public IWinMLStage* Stage => _stage;

    public ComObjects Objects => _objects;

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
            throw new COMException($"Unsupported language artifact: {modelPath}", ErrorNotSupported);
        }

        if (artifactKind == LanguageArtifactKind.Gguf && !string.IsNullOrEmpty(device.Ep))
        {
            throw new COMException("--ep selects an ORT provider and cannot be used with GGUF.", ErrorNotSupported);
        }

        if (artifactKind == LanguageArtifactKind.Gguf && device.Kind == WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_NPU)
        {
            throw new COMException("llama.cpp runs GGUF models on CPU or GPU, not NPU.", ErrorNotSupported);
        }

        LanguageModel created = new();
        try
        {
            created.Initialize(modelPath, tokenizerSource, artifactKind, device, contextCapacityHint, enableDiagnostics);
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    public WINML_EXECUTION_TARGET_KIND GetResolvedTargetKind()
    {
        using ComObjects objects = new();
        IWinMLExecutionTarget* target = objects.Add(_stage->GetExecutionTarget());
        return target->GetKind();
    }

    public GenerationResult GenerateTokens(string prompt, uint maxNewTokens, Action<string> onFragment)
    {
        return Generate(() => Encode(prompt), maxNewTokens, onFragment);
    }

    public GenerationResult GenerateConversation(string role, string content, uint maxNewTokens, Action<string> onFragment)
    {
        if (_formatter == null)
        {
            return new GenerationResult { HResult = E_NOINTERFACE, StopReason = StopReason.Error };
        }

        return Generate(() => FormatConversation(role, content), maxNewTokens, onFragment);
    }

    public void Dispose()
    {
        _objects.Dispose();
    }

    private void Initialize(
        string modelPath,
        string? tokenizerSource,
        LanguageArtifactKind artifactKind,
        DeviceOptions device,
        ulong contextCapacityHint,
        bool enableDiagnostics)
    {
        IWinMLRuntime* runtime;
        Guid runtimeIid = typeof(IWinMLRuntime).GUID;
        PInvoke.WinMLCreateRuntime(&runtimeIid, (void**)&runtime).ThrowOnFailure();
        _objects.Add(runtime);

        // The provider that --ep requests was prepared before loading, as in
        // the C++ sample, so only the target is created here.
        IWinMLExecutionTarget* stageTarget = ExecutionTargets.Create(runtime, device, _objects);

        // Unified load: one call selects the backend from the artifact, then
        // the rest of the setup uses the same pipeline objects.
        IWinMLModel* model = _objects.Add(runtime->LoadModelFromFile(modelPath, null));

        IWinMLPipelineBuilder* builder = _objects.Add(runtime->CreatePipelineBuilder());
        _stage = _objects.Add(builder->AddModelStage(model, stageTarget, "decoder"));
        _stage->RequestOutput(0);

        // Declare any ONNX KV-cache pairs before Build; GGUF-backed stages own
        // their state and report no declared pairs here.
        IWinMLStatefulStageOptions* options = _objects.QueryInterface<IWinMLStatefulStageOptions>(_stage);
        ulong declaredCapacity = DeclareDecoderStateTensorPairs(model, options);
        ulong capacityHint = contextCapacityHint != 0 ? contextCapacityHint : declaredCapacity;
        if (capacityHint != 0)
        {
            options->SetSequenceCapacityHint(capacityHint);
        }

        if (enableDiagnostics)
        {
            StageDiagnostics.Enable(_stage, _objects);
        }

        _pipeline = _objects.Add(builder->Build());
        _statefulStage = _objects.QueryInterface<IWinMLStatefulStage>(_stage);

        IWinMLStageSchema* schema = _objects.QueryInterface<IWinMLStageSchema>(_stage);
        WINML_TENSOR_DESC tokenDescription = schema->GetInputTensorDesc(0);
        if (tokenDescription.dataType != WINML_TENSOR_DATA_TYPE.WINML_TENSOR_DATA_TYPE_INT32 &&
            tokenDescription.dataType != WINML_TENSOR_DATA_TYPE.WINML_TENSOR_DATA_TYPE_INT64)
        {
            throw new COMException($"The decoder's token input is {tokenDescription.dataType}, not INT32 or INT64.", ErrorNotSupported);
        }

        if (tokenDescription.dimensionCount != 1 && tokenDescription.dimensionCount != 2)
        {
            throw new COMException($"The decoder's token input has rank {tokenDescription.dimensionCount}, not 1 or 2.", ErrorNotSupported);
        }

        _tokenDataType = tokenDescription.dataType;
        _tokenDimensionCount = tokenDescription.dimensionCount;

        // Tokenizer source is the ONNX model directory or the GGUF artifact
        // unless the caller supplies an override.
        string resolvedTokenizerSource = ResolveTokenizerSource(modelPath, tokenizerSource, artifactKind);
        IWinMLTokenizer* tokenizer;
        PInvoke.WinMLCreateTokenizerFromFile(resolvedTokenizerSource, &tokenizer).ThrowOnFailure();
        _tokenizer = _objects.Add(tokenizer);
        _decoder = _objects.Add(_tokenizer->CreateDecoder());
        _formatter = _objects.TryQueryInterface<IWinMLStructuredConversationFormatter>(_tokenizer);

        // The fixed logits vocabulary is a fact of the materialized stage, so it
        // is cached once here and used to range-check every tokenizer special
        // token and prompt token before the backend runs.
        _vocabularySize = GetStageVocabularySize(schema);
        InitializeTokenizerMetadata();

        // Token tensors are created, and logits rows read back, on a CPU target.
        IWinMLExecutionTarget* tokenTarget = _objects.Add(runtime->CreateCpuExecutionTarget());
        _tokenTensorFactory = _objects.QueryInterface<IWinMLRawTensorFactory>(tokenTarget);
    }

    // A stateful decoder must declare its key/value slots before Build; the
    // Runtime never infers persistent state from tensor names or order. A
    // unified export lays the cache out as trailing tensors, each past_* input
    // lining up with the present_* output at the same offset from the end, so
    // walk back from the end while the declared descriptors agree. The model
    // schema, not the stage schema, is read because the stage schema exists
    // only after Build. Returns the sequence axis of a rank-4 cache
    // [batch, heads, sequence, head_dim] when it is fixed, or zero.
    private ulong DeclareDecoderStateTensorPairs(IWinMLModel* model, IWinMLStatefulStageOptions* options)
    {
        IWinMLModelSchema* schema = _objects.TryQueryInterface<IWinMLModelSchema>(model);
        if (schema == null)
        {
            return 0;
        }

        uint inputCount;
        uint outputCount;
        try
        {
            inputCount = schema->GetInputCount();
            outputCount = schema->GetOutputCount();
        }
        catch (COMException)
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
            TryGetSchemaDesc(schema, isInput: true, inputCount - 1 - matched, out WINML_TENSOR_SCHEMA_DESC stateInput) &&
            TryGetSchemaDesc(schema, isInput: false, outputCount - 1 - matched, out WINML_TENSOR_SCHEMA_DESC stateOutput) &&
            DescriptorsMatch(stateInput, stateOutput))
        {
            ++matched;
        }

        for (uint pair = 0; pair < matched; ++pair)
        {
            options->AddStateTensorPair(inputCount - matched + pair, outputCount - matched + pair);
        }

        if (matched != 0 &&
            TryGetSchemaDesc(schema, isInput: true, inputCount - matched, out WINML_TENSOR_SCHEMA_DESC firstState) &&
            firstState.dimensionCount == 4 &&
            firstState.dimensions[2] > 0 &&
            firstState.dimensions[2] != ulong.MaxValue)
        {
            return firstState.dimensions[2];
        }

        return 0;
    }

    private static bool TryGetSchemaDesc(IWinMLModelSchema* schema, bool isInput, uint index, out WINML_TENSOR_SCHEMA_DESC desc)
    {
        try
        {
            desc = isInput ? schema->GetInputTensorDesc(index) : schema->GetOutputTensorDesc(index);
            return true;
        }
        catch (COMException)
        {
            desc = default;
            return false;
        }
    }

    // Two logical state tensors pair up only when their declared descriptors match.
    private static bool DescriptorsMatch(WINML_TENSOR_SCHEMA_DESC first, WINML_TENSOR_SCHEMA_DESC second)
    {
        if (first.dataType != second.dataType || first.dimensionCount != second.dimensionCount)
        {
            return false;
        }

        for (uint axis = 0; axis < first.dimensionCount; ++axis)
        {
            if (first.dimensions[axis] != second.dimensions[axis])
            {
                return false;
            }
        }

        return true;
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
    private static uint GetStageVocabularySize(IWinMLStageSchema* schema)
    {
        WINML_TENSOR_DESC logits = schema->GetOutputTensorDesc(0);
        if (logits.dimensionCount == 0 || logits.dimensions == null)
        {
            throw new COMException("The decoder's logits output has no dimensions.", ErrorInvalidData);
        }

        ulong extent = logits.dimensions[logits.dimensionCount - 1];
        if (extent == 0)
        {
            throw new COMException("The decoder's logits vocabulary is empty.", ErrorInvalidData);
        }

        return extent == ulong.MaxValue || extent > uint.MaxValue ? 0 : (uint)extent;
    }

    // EOS is required: a greedy loop with no end-of-sequence token runs to the
    // token limit on every prompt. BOS stays optional - the chat template
    // inserts it - but it is range-checked when the tokenizer declares one.
    private void InitializeTokenizerMetadata()
    {
        IWinMLTokenizerMetadata* metadata = _objects.TryQueryInterface<IWinMLTokenizerMetadata>(_tokenizer);
        if (metadata == null)
        {
            throw new COMException("tokenizer does not expose required EOS metadata.", E_NOINTERFACE);
        }

        // GetEosTokenIds returns S_FALSE, which the projection discards, with
        // an empty array when the tokenizer declares no EOS token.
        uint eosCount = 0;
        uint* eosTokens = null;
        metadata->GetEosTokenIds(&eosCount, &eosTokens);
        uint[] eos = TakeTokens(eosTokens, eosCount);
        if (eos.Length == 0)
        {
            throw new COMException("tokenizer EOS metadata is empty.", ErrorInvalidData);
        }

        ValidateTokenIds(eos, "tokenizer EOS");
        _eosTokens = eos;

        // GetBosTokenId returns S_FALSE without writing the id when the
        // tokenizer has no BOS token, so an unchanged sentinel means "none".
        uint beginningToken = uint.MaxValue;
        metadata->GetBosTokenId(&beginningToken);
        if (beginningToken != uint.MaxValue)
        {
            ValidateTokenIds(new[] { beginningToken }, "tokenizer BOS");
        }
    }

    // Rejects token ids the model's vocabulary cannot represent.
    private void ValidateTokenIds(uint[] tokens, string label)
    {
        if (_vocabularySize == 0)
        {
            return;
        }

        foreach (uint token in tokens)
        {
            if (token >= _vocabularySize)
            {
                throw new COMException(
                    $"{label} token id {token} is outside the model vocabulary ({_vocabularySize}). Tokenizer and model do not match.",
                    ErrorInvalidData);
            }
        }
    }

    private uint[] Encode(string prompt)
    {
        uint* tokens = null;
        _tokenizer->Encode(prompt, WINML_TOKENIZER_ENCODE_FLAGS.WINML_TOKENIZER_ENCODE_FLAG_ADD_SPECIAL_TOKENS, out uint count, &tokens);
        return TakeTokens(tokens, count);
    }

    // The formatter renders the model's own chat template and ends the prompt
    // with the assistant generation prompt.
    private uint[] FormatConversation(string role, string content)
    {
        using ComObjects objects = new();
        fixed (char* rolePointer = role)
        fixed (char* contentPointer = content)
        {
            WINML_CONVERSATION_MESSAGE message = new()
            {
                role = new PCWSTR(rolePointer),
                content = new PCWSTR(contentPointer),
            };
            WINML_CONVERSATION_REQUEST request = new()
            {
                formatterMode = WINML_CONVERSATION_FORMATTER_MODE.WINML_CONVERSATION_FORMATTER_MODE_MODEL_DEFAULT,
                messageCount = 1,
                messages = &message,
                addGenerationPrompt = true,
            };

            IWinMLConversationFormatResult* formatted = objects.Add(_formatter->FormatConversation(&request));
            uint count = 0;
            uint* tokens = null;
            formatted->GetTokenIds(&count, &tokens);
            return TakeTokens(tokens, count);
        }
    }

    // Copies a CoTaskMemAlloc token array and frees it.
    private static uint[] TakeTokens(uint* tokens, uint count)
    {
        try
        {
            return tokens == null ? Array.Empty<uint>() : new ReadOnlySpan<uint>(tokens, checked((int)count)).ToArray();
        }
        finally
        {
            Win32.CoTaskMemFree(tokens);
        }
    }

    private GenerationResult Generate(Func<uint[]> encodePrompt, uint maxNewTokens, Action<string> onFragment)
    {
        GenerationResult result = new();
        try
        {
            GenerateEncoded(encodePrompt, maxNewTokens, onFragment, result);
        }
        catch (Exception ex)
        {
            result.HResult = ex.HResult;
            result.StopReason = StopReason.Error;
        }

        return result;
    }

    private void GenerateEncoded(Func<uint[]> encodePrompt, uint maxNewTokens, Action<string> onFragment, GenerationResult result)
    {
        _pipeline->ResetExecutionState();
        _decoder->Reset();

        uint[] promptTokens = encodePrompt();
        if (promptTokens.Length == 0)
        {
            throw new COMException("The prompt encoded to no tokens.", E_INVALIDARG);
        }

        ValidateTokenIds(promptTokens, "prompt");
        result.PromptTokenCount = (uint)promptTokens.Length;

        // Pattern 3: query the built stateful stage for the effective sequence
        // capacity before running the prompt.
        ulong capacity = _statefulStage->GetSequenceCapacity();
        if (capacity != 0 && (ulong)promptTokens.Length >= capacity)
        {
            result.HResult = S_FALSE;
            result.StopReason = StopReason.Capacity;
            return;
        }

        RunTokens(promptTokens);

        uint tokenLimit = maxNewTokens == 0 ? DefaultMaxNewTokens : maxNewTokens;
        for (uint index = 0; index < tokenLimit; ++index)
        {
            ulong position = _statefulStage->GetSequencePosition();
            if (capacity != 0 && position >= capacity)
            {
                result.HResult = S_FALSE;
                result.StopReason = StopReason.Capacity;
                break;
            }

            // Pattern 9: read logits from the retained output, choose the next
            // token in the app, and decode a streaming fragment.
            float[] logits = ReadLastTokenLogits();
            uint token = ArgmaxToken(logits);
            if (Array.IndexOf(_eosTokens, token) >= 0)
            {
                result.StopReason = StopReason.EndOfSequence;
                break;
            }

            result.Tokens.Add(token);

            // The fragment is owned by the decoder and valid until its next call.
            PCWSTR fragment;
            _decoder->DecodeToken(token, &fragment);
            if (fragment.Value != null)
            {
                string text = fragment.ToString();
                result.Text.Append(text);
                onFragment(text);
            }

            if (capacity != 0 && position + 1 >= capacity)
            {
                result.HResult = S_FALSE;
                result.StopReason = StopReason.Capacity;
                break;
            }

            if (index + 1 < tokenLimit)
            {
                RunTokens(new[] { token });
            }
        }
    }

    // Binds the tokens as the decoder's input 0, shaped [n] or [1, n] in the
    // stage's token type, and runs the pipeline.
    private void RunTokens(uint[] tokens)
    {
        using ComObjects objects = new();
        ulong* dimensions = stackalloc ulong[2];
        if (_tokenDimensionCount == 2)
        {
            dimensions[0] = 1;
            dimensions[1] = (ulong)tokens.Length;
        }
        else
        {
            dimensions[0] = (ulong)tokens.Length;
        }

        WINML_TENSOR_DESC description = new()
        {
            dataType = _tokenDataType,
            dimensionCount = _tokenDimensionCount,
            dimensions = dimensions,
        };

        IWinMLTensor* tensor;
        if (_tokenDataType == WINML_TENSOR_DATA_TYPE.WINML_TENSOR_DATA_TYPE_INT32)
        {
            int[] values = Array.ConvertAll(tokens, token => (int)token);
            fixed (int* data = values)
            {
                tensor = objects.Add(_tokenTensorFactory->CreateTensor(&description, data, (ulong)(values.Length * sizeof(int))));
            }
        }
        else
        {
            long[] values = Array.ConvertAll(tokens, token => (long)token);
            fixed (long* data = values)
            {
                tensor = objects.Add(_tokenTensorFactory->CreateTensor(&description, data, (ulong)(values.Length * sizeof(long))));
            }
        }

        _stage->BindInput(0, tensor);
        _pipeline->Run();
    }

    // Reads the final-position row of the logits output. For a rank-2 or
    // higher output, IWinMLRawTensorFactory::CreateTensorFromRegion on the CPU
    // target views that one row, so the sample reads one vocabulary-sized row
    // instead of the whole sequence.
    private float[] ReadLastTokenLogits()
    {
        using ComObjects objects = new();
        IWinMLTensor* logitsTensor = objects.Add(_stage->GetOutput(0));
        if (logitsTensor == null)
        {
            throw new COMException("The decoder produced no logits.", E_INVALIDARG);
        }

        WINML_TENSOR_DESC desc = logitsTensor->GetDesc();
        if (desc.dimensionCount == 0 || desc.dimensions == null)
        {
            throw new COMException("The logits output has no dimensions.", ErrorInvalidData);
        }

        ulong vocabularyExtent = desc.dimensions[desc.dimensionCount - 1];
        if (vocabularyExtent == 0 || vocabularyExtent > uint.MaxValue)
        {
            throw new COMException($"The logits vocabulary extent {vocabularyExtent} is not usable.", ErrorInvalidData);
        }

        uint vocabularySize = (uint)vocabularyExtent;
        if (desc.dimensionCount >= 2)
        {
            uint positionAxis = desc.dimensionCount - 2;
            uint* starts = stackalloc uint[(int)desc.dimensionCount];
            uint* extents = stackalloc uint[(int)desc.dimensionCount];
            for (uint axis = 0; axis < desc.dimensionCount; ++axis)
            {
                if (desc.dimensions[axis] == 0 || desc.dimensions[axis] > uint.MaxValue)
                {
                    throw new COMException("The logits output has a dynamic or empty dimension.", ErrorInvalidData);
                }

                starts[axis] = 0;
                extents[axis] = 1;
            }

            starts[positionAxis] = (uint)(desc.dimensions[positionAxis] - 1);
            extents[desc.dimensionCount - 1] = vocabularySize;
            logitsTensor = objects.Add(_tokenTensorFactory->CreateTensorFromRegion(logitsTensor, starts, extents, desc.dimensionCount));
            desc = logitsTensor->GetDesc();
        }

        ulong elementCount = 1;
        for (uint axis = 0; axis < desc.dimensionCount; ++axis)
        {
            elementCount = checked(elementCount * desc.dimensions[axis]);
        }

        if (elementCount < vocabularySize)
        {
            throw new COMException("The logits row is smaller than the vocabulary.", ErrorInvalidData);
        }

        ulong offset = elementCount - vocabularySize;
        IWinMLTensorDataLock* dataLock = objects.Add(LockForRead(logitsTensor));
        byte* data;
        ulong byteCount;
        dataLock->GetData(&data, &byteCount);

        float[] logits = new float[vocabularySize];
        switch (desc.dataType)
        {
            case WINML_TENSOR_DATA_TYPE.WINML_TENSOR_DATA_TYPE_FLOAT32:
                if (data == null || byteCount < elementCount * sizeof(float))
                {
                    throw new COMException("The logits buffer is smaller than its shape.", ErrorInvalidData);
                }

                new ReadOnlySpan<float>((float*)data + offset, (int)vocabularySize).CopyTo(logits);
                break;
            case WINML_TENSOR_DATA_TYPE.WINML_TENSOR_DATA_TYPE_FLOAT16:
                if (data == null || byteCount < elementCount * sizeof(ushort))
                {
                    throw new COMException("The logits buffer is smaller than its shape.", ErrorInvalidData);
                }

                Half* values = (Half*)data + offset;
                for (int i = 0; i < logits.Length; ++i)
                {
                    logits[i] = (float)values[i];
                }

                break;
            default:
                throw new COMException($"Unsupported logits tensor data type {(int)desc.dataType}.", ErrorNotSupported);
        }

        return logits;
    }

    // A plain CPU read lock succeeds for CPU-resident tensors. A tensor on an
    // accelerator refuses it with ERROR_NOT_SUPPORTED and needs a synchronized
    // lock, which copies the data back to CPU-visible memory.
    private static IWinMLTensorDataLock* LockForRead(IWinMLTensor* tensor)
    {
        try
        {
            return tensor->Lock(WINML_TENSOR_LOCK_MODE.WINML_TENSOR_LOCK_MODE_READ, WINML_TENSOR_LOCK_FLAGS.WINML_TENSOR_LOCK_FLAG_NONE);
        }
        catch (Exception ex) when (ex.HResult == ErrorNotSupported)
        {
            return tensor->Lock(
                WINML_TENSOR_LOCK_MODE.WINML_TENSOR_LOCK_MODE_READ,
                WINML_TENSOR_LOCK_FLAGS.WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS);
        }
    }

    // Greedy selection: the first highest-logit token, as std::max_element
    // picks in the C++ sample. Argmax keeps replies repeatable.
    private static uint ArgmaxToken(float[] logits)
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
}
