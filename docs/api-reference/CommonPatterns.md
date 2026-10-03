<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Common Patterns

> Part of the [WinML Runtime API Reference](README.md).
> See [IWinMLRuntime](IWinMLRuntime.md) and
> [IWinMLPipelineBuilder](IWinMLPipelineBuilder.md) for model loading and graph construction.

These fragments use `wil::com_ptr` and `THROW_IF_FAILED` for brevity. The application supplies referenced objects, matching model artifacts, and model-specific bindings not shown.

## Pattern 1: Load -> Build -> Run

The minimal path from model file to inference result: load a model, create a target, add a stage, build a pipeline, bind inputs, and run.

```cpp
wil::com_ptr<IWinMLRuntime> runtime;
THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.put())));

wil::com_ptr<IWinMLModel> model;
THROW_IF_FAILED(runtime->LoadModelFromFile(L"model.onnx", nullptr, model.put()));

wil::com_ptr<IWinMLExecutionTarget> target;
THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(target.put()));

wil::com_ptr<IWinMLPipelineBuilder> builder;
THROW_IF_FAILED(runtime->CreatePipelineBuilder(builder.put()));

wil::com_ptr<IWinMLStage> stage;
THROW_IF_FAILED(builder->AddModelStage(model.get(), target.get(), L"classifier", stage.put()));
THROW_IF_FAILED(stage->RequestOutput(0));

// Build returns the pipeline object used for execution.
wil::com_ptr<IWinMLPipeline> pipeline;
THROW_IF_FAILED(builder->Build(pipeline.put()));

// Bind by ordinal index.
THROW_IF_FAILED(stage->BindInput(0, inputTensor.get()));
THROW_IF_FAILED(pipeline->Run());

wil::com_ptr<IWinMLTensor> output;
THROW_IF_FAILED(stage->GetOutput(0, output.put()));
```

To receive a single-output stage's result in caller-owned CPU storage, bind a
writable, descriptor-compatible CPU tensor before execution:

```cpp
THROW_IF_FAILED(stage->BindOutput(0, cpuOutput.get()));
THROW_IF_FAILED(pipeline->Run());
```

On success, `cpuOutput` contains that stage's result. `RequestOutput` asks the runtime to retain an output for `GetOutput`; `ResetOutput(index)` clears a previous output selection. See [output binding](IWinMLStage.md#bindoutput).

---

## Pattern 2: Declared Schema and Materialized Stage Schema

For models that expose declared tensor metadata, query `IWinMLModelSchema` from `IWinMLModel`. After `Build`, query `IWinMLStageSchema` from the stage for concrete tensor descriptors when that interface is available.

```cpp
wil::com_ptr<IWinMLModelSchema> modelSchema;
THROW_IF_FAILED(model->QueryInterface(IID_PPV_ARGS(modelSchema.put())));

UINT32 inputCount = 0;
THROW_IF_FAILED(modelSchema->GetInputCount(&inputCount));

WINML_TENSOR_SCHEMA_DESC declared = {};
THROW_IF_FAILED(modelSchema->GetInputTensorDesc(0, &declared));
// declared.dimensions[i] may be UINT64_MAX for a free/dynamic declared dimension.

// After Build, query concrete descriptors from the stage when available.
wil::com_ptr<IWinMLStageSchema> stageSchema;
if (SUCCEEDED(stage->QueryInterface(IID_PPV_ARGS(stageSchema.put()))))
{
    WINML_TENSOR_DESC materialized = {};
    THROW_IF_FAILED(stageSchema->GetInputTensorDesc(0, &materialized));
    // Use the returned descriptor for tensor creation and binding.
}
```

---

## Pattern 3: Stateful Stage Lifecycle

Declare tensor-state pairs before `Build`, then use `IWinMLStatefulStage` for Runtime-managed sequence state. `stateInputIndex` and `stateOutputIndex` are application-selected ordinals from the model schema; repeat the declaration for every state pair.

```cpp
wil::com_ptr<IWinMLStatefulStageOptions> stateOptions;
THROW_IF_FAILED(decoderStage->QueryInterface(IID_PPV_ARGS(stateOptions.put())));
THROW_IF_FAILED(stateOptions->AddStateTensorPair(stateInputIndex, stateOutputIndex));
THROW_IF_FAILED(stateOptions->SetCallerOwnsStateTensors(FALSE));

wil::com_ptr<IWinMLPipeline> pipeline;
THROW_IF_FAILED(builder->Build(pipeline.put()));

wil::com_ptr<IWinMLStatefulStage> stateful;
THROW_IF_FAILED(decoderStage->QueryInterface(IID_PPV_ARGS(stateful.put())));

// Resolve capacity for a model whose declared state axis is free, when supported.
HRESULT capacityHr = stateful->SetSequenceCapacity(256);
THROW_HR_IF(
    capacityHr,
    FAILED(capacityHr) &&
        capacityHr != HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED));

THROW_IF_FAILED(pipeline->Run());

UINT64 position = 0;
THROW_IF_FAILED(stateful->GetSequencePosition(&position));
THROW_IF_FAILED(stateful->RewindTo(0));

// Start a new sequence on the same pipeline object.
THROW_IF_FAILED(pipeline->ResetExecutionState());
```

Stateful operations require a built stage configured with Runtime-managed state. Unsupported state operations can return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.

---

## Pattern 4: Multi-Stage Pipeline with Connect

Add stages, connect them by ordinal output/input indexes, and build the pipeline.

```cpp
wil::com_ptr<IWinMLModel> preprocess, encoder, projection, decoder;
THROW_IF_FAILED(runtime->LoadModelFromFile(L"preprocess.onnx", nullptr, preprocess.put()));
THROW_IF_FAILED(runtime->LoadModelFromFile(L"vision_encoder.onnx", nullptr, encoder.put()));
THROW_IF_FAILED(runtime->LoadModelFromFile(L"projection.onnx", nullptr, projection.put()));
THROW_IF_FAILED(runtime->LoadModelFromFile(L"decoder.onnx", nullptr, decoder.put()));

wil::com_ptr<IWinMLExecutionTarget> cpuTarget, gpuTarget;
THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(cpuTarget.put()));
THROW_IF_FAILED(runtime->CreateExecutionTargetFromD3D12(d3d12Device.get(), nullptr, gpuTarget.put()));

wil::com_ptr<IWinMLPipelineBuilder> builder;
THROW_IF_FAILED(runtime->CreatePipelineBuilder(builder.put()));

wil::com_ptr<IWinMLStage> prepStage, encStage, projStage, decStage;
THROW_IF_FAILED(builder->AddModelStage(preprocess.get(), cpuTarget.get(), L"preprocess", prepStage.put()));
THROW_IF_FAILED(builder->AddModelStage(encoder.get(), gpuTarget.get(), L"encoder", encStage.put()));
THROW_IF_FAILED(builder->AddModelStage(projection.get(), gpuTarget.get(), L"projection", projStage.put()));
THROW_IF_FAILED(builder->AddModelStage(decoder.get(), gpuTarget.get(), L"decoder", decStage.put()));

THROW_IF_FAILED(builder->Connect(prepStage.get(), 0, encStage.get(), 0));
THROW_IF_FAILED(builder->Connect(encStage.get(), 0, projStage.get(), 0));
THROW_IF_FAILED(builder->Connect(projStage.get(), 0, decStage.get(), 0));
THROW_IF_FAILED(decStage->RequestOutput(0));

wil::com_ptr<IWinMLPipeline> pipeline;
THROW_IF_FAILED(builder->Build(pipeline.put()));

// Bind the graph's external input on the first stage, run, read from the last.
THROW_IF_FAILED(prepStage->BindInput(0, imageTensor.get()));
THROW_IF_FAILED(pipeline->Run());

wil::com_ptr<IWinMLTensor> output;
THROW_IF_FAILED(decStage->GetOutput(0, output.put()));

THROW_IF_FAILED(pipeline->ResetExecutionState());
```

Use separate targets when adjacent stages must run on different hardware classes.

---

## Pattern 5: Tensor Creation and Binding

Create tensors from factory interfaces obtained with `QueryInterface` from an `IWinMLExecutionTarget`; see [IWinMLRawTensorFactory](IWinMLRawTensorFactory.md).

```cpp
wil::com_ptr<IWinMLRawTensorFactory> rawFactory;
THROW_IF_FAILED(cpuTarget->QueryInterface(IID_PPV_ARGS(rawFactory.put())));

UINT64 dims[] = {1, 3, 224, 224};
WINML_TENSOR_DESC desc = {WINML_TENSOR_DATA_TYPE_FLOAT32, 4, dims};

wil::com_ptr<IWinMLTensor> cpuTensor;
THROW_IF_FAILED(rawFactory->CreateTensor(&desc, pixelData, dataSize, cpuTensor.put()));

// Read tensor data back.
wil::com_ptr<IWinMLTensorDataLock> lock;
THROW_IF_FAILED(cpuTensor->Lock(WINML_TENSOR_LOCK_MODE_READ, WINML_TENSOR_LOCK_FLAG_NONE, lock.put()));

BYTE* pData = nullptr;
UINT64 byteSize = 0;
THROW_IF_FAILED(lock->GetData(&pData, &byteSize));
const float* pF = reinterpret_cast<const float*>(pData);

// Bind to a stage by ordinal index.
THROW_IF_FAILED(stage->BindInput(0, cpuTensor.get()));
```

---

## Pattern 6: GPU Tensor Zero-Copy

```cpp
wil::com_ptr<IWinMLExecutionTarget> gpuTarget;
THROW_IF_FAILED(runtime->CreateExecutionTargetFromD3D12(d3d12Device.get(), d3d12Queue.get(), gpuTarget.put()));

wil::com_ptr<IWinMLRawTensorFactory> rawFactory;
THROW_IF_FAILED(gpuTarget->QueryInterface(IID_PPV_ARGS(rawFactory.put())));

UINT64 dims[] = {1, 3, 224, 224};
WINML_TENSOR_DESC desc = {WINML_TENSOR_DATA_TYPE_FLOAT32, 4, dims};
WINML_BUFFER_BINDING binding = {pMyD3D12Resource, 0, bufferSize};

wil::com_ptr<IWinMLTensor> gpuTensor;
THROW_IF_FAILED(rawFactory->CreateTensorFromBuffer(&desc, &binding, gpuTensor.put()));

// Use with a stage placed on the same GPU target.
wil::com_ptr<IWinMLStage> stage;
THROW_IF_FAILED(builder->AddModelStage(model.get(), gpuTarget.get(), L"gpu-stage", stage.put()));
wil::com_ptr<IWinMLPipeline> pipeline;
THROW_IF_FAILED(builder->Build(pipeline.put()));

THROW_IF_FAILED(stage->BindInput(0, gpuTensor.get()));
THROW_IF_FAILED(pipeline->Run());
```

---

## Pattern 7: Ahead-of-Time Compile and Unified Load

Ahead-of-time compilation is exposed by `IWinMLModelCompiler`, queried from an `IWinMLExecutionTarget`. The compiled artifact is loaded with the same `IWinMLRuntime::LoadModelFromFile` method as the source model.

```cpp
wil::com_ptr<IWinMLModel> model;
THROW_IF_FAILED(runtime->LoadModelFromFile(L"model.onnx", nullptr, model.put()));

wil::com_ptr<IWinMLExecutionTarget> gpuTarget;
THROW_IF_FAILED(runtime->CreateExecutionTargetFromD3D12(d3d12Device.get(), nullptr, gpuTarget.put()));

wil::com_ptr<IWinMLModelCompiler> compiler;
THROW_IF_FAILED(gpuTarget->QueryInterface(IID_PPV_ARGS(compiler.put())));

// Write the compiled artifact and external resources to the requested files.
THROW_IF_FAILED(compiler->CompileToFile(
    model.get(), L"model.epctx.onnx", L"model.epctx.onnx.data"));

// Later run: load the compiled artifact through the same Load* entry point.
wil::com_ptr<IWinMLModel> compiledModel;
THROW_IF_FAILED(runtime->LoadModelFromFile(
    L"model.epctx.onnx", nullptr, compiledModel.put()));

wil::com_ptr<IWinMLStage> stage;
THROW_IF_FAILED(builder->AddModelStage(compiledModel.get(), gpuTarget.get(), L"compiled", stage.put()));
```

For in-memory compilation, stream output to [`IWinMLCompileOutputSink`](IWinMLCompileOutputSink.md) with `CompileToSink`, then reload captured bytes with `IWinMLRuntime::LoadModelFromBuffer` and an `IWinMLResourceMapReader` when external resources are needed.

---

## Pattern 8: Tensor Lock for CPU Access

```cpp
// Use ALLOW_SYNCHRONIZED_CPU_ACCESS when direct CPU access may not be available.
wil::com_ptr<IWinMLTensorDataLock> dataLock;
HRESULT hr = tensor->Lock(
    WINML_TENSOR_LOCK_MODE_WRITE,
    WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS,
    dataLock.put());
if (SUCCEEDED(hr))
{
    BYTE* pData = nullptr;
    UINT64 dataSize = 0;
    THROW_IF_FAILED(dataLock->GetData(&pData, &dataSize));
    memcpy(pData, pixelData, dataSize);

    wil::com_ptr<IWinMLTensorSynchronizedDataLock> synchronizedLock;
    if (SUCCEEDED(dataLock->QueryInterface(IID_PPV_ARGS(synchronizedLock.put()))))
    {
        THROW_IF_FAILED(synchronizedLock->Commit());
    }
}
```

---

## Pattern 9: Manual Text Decode Loop

This outline shows positional binding with an `IWinMLTokenizerDecoder` for incremental detokenization.

**Illustrative outline, not a complete application.** The code omits token tensor creation, prompt/decode input binding, model-specific state and auxiliary inputs, and token feedback. `SampleLogitsInApp` and `eosTokenId` are application-supplied placeholders. See [IWinMLStageSchema](IWinMLStageSchema.md) for input descriptors and [IWinMLTextGenerationTask](IWinMLTextGenerationTask.md) for Task-managed generation.

```cpp
wil::com_ptr<IWinMLRuntime> runtime;
THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.put())));

wil::com_ptr<IWinMLModel> model;
THROW_IF_FAILED(runtime->LoadModelFromFile(
    L"C:\\models\\qwen2.5-0.5b-instruct\\model.onnx", nullptr, model.put()));

wil::com_ptr<IWinMLExecutionTarget> target;
THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(target.put()));

wil::com_ptr<IWinMLPipelineBuilder> builder;
THROW_IF_FAILED(runtime->CreatePipelineBuilder(builder.put()));

wil::com_ptr<IWinMLStage> stage;
THROW_IF_FAILED(builder->AddModelStage(model.get(), target.get(), L"decoder", stage.put()));
THROW_IF_FAILED(stage->RequestOutput(0));

wil::com_ptr<IWinMLPipeline> pipeline;
THROW_IF_FAILED(builder->Build(pipeline.put()));

wil::com_ptr<IWinMLTokenizer> tokenizer;
THROW_IF_FAILED(WinMLCreateTokenizerFromFile(
    L"C:\\models\\qwen2.5-0.5b-instruct\\tokenizer.json", tokenizer.put()));

UINT32 tokenCount = 0;
UINT32* tokenIds = nullptr;
THROW_IF_FAILED(tokenizer->Encode(
    L"The sky is often",
    WINML_TOKENIZER_ENCODE_FLAG_ADD_SPECIAL_TOKENS,
    &tokenCount,
    &tokenIds));

wil::com_ptr<IWinMLTokenizerDecoder> decoder;
THROW_IF_FAILED(tokenizer->CreateDecoder(decoder.put()));

std::wstring generated;
for (UINT32 i = 0; i < 256; ++i)
{
    // Bind this step's input/state tensors positionally, then run.
    THROW_IF_FAILED(pipeline->Run());

    wil::com_ptr<IWinMLTensor> logits;
    THROW_IF_FAILED(stage->GetOutput(/* logits output index */ 0, logits.put()));

    UINT32 nextToken = SampleLogitsInApp(logits.get());
    if (nextToken == eosTokenId)
    {
        break;
    }

    // The returned fragment is decoder-owned; do not free it, and it is only
    // valid until the next call on this decoder.
    LPCWSTR fragment = nullptr;
    THROW_IF_FAILED(decoder->DecodeToken(nextToken, &fragment));
    generated += fragment;
}

wprintf(L"%s\n", generated.c_str());
CoTaskMemFree(tokenIds);
```

---

## Pattern 10: Next-Iteration Edges and Fixed-Count Iterative Replay

`ConnectNextIteration` has the same source-stage/output and target-stage/input parameters as `Connect`, but marks the edge as feedback into the next iteration.

```text
                         current iteration
  [embed] --Connect--> [decoder] --Connect--> [sample]
     ^                                             |
     |                                             |
     +---------- ConnectNextIteration -------------+
                   sample output N
                    -> embed input N+1

Run 0: initial token -> graph -> token 1
Run 1: token 1      -> graph -> token 2
Run 2: token 2      -> graph -> token 3
```

Bind the initial value directly to the next-iteration target input before the first run. `ConnectNextIteration` records the feedback edge; the caller still chooses how many iterations to run. Query `IWinMLPipelineIterationExecution` from the built pipeline before calling `SubmitIterations`.

```cpp
THROW_IF_FAILED(builder->Connect(sampleStage.get(), 0, outputStage.get(), 0));
THROW_IF_FAILED(builder->ConnectNextIteration(sampleStage.get(), 1, embeddingStage.get(), tokenInputIndex));
THROW_IF_FAILED(builder->ConnectNextIteration(sampleStage.get(), 2, decoderStage.get(), positionInputIndex));

THROW_IF_FAILED(sampleStage->RequestOutput(
    observedTokenOutputIndex));

wil::com_ptr<IWinMLPipeline> pipeline;
THROW_IF_FAILED(builder->Build(pipeline.put()));

wil::com_ptr<IWinMLPipelineIterationExecution> iteration;
THROW_IF_FAILED(pipeline->QueryInterface(IID_PPV_ARGS(iteration.put())));

wil::com_ptr<IWinMLFence> completionFence;
THROW_IF_FAILED(iteration->SubmitIterations(denoiseStepCount, nullptr, completionFence.put()));
THROW_IF_FAILED(completionFence->Wait(INFINITE));
```

For token-by-token text generation, run or submit once per token and read the sampled-token output needed for detokenization or EOS handling.

---

## Pattern 11: ONNX Name Resolution at Setup Time

For ONNX models, `IWinMLOrtModelSchema` can resolve an input or output name to the ordinal index used by positional binding. Resolve names during setup and bind by index in the execution loop.

```cpp
// Setup time: resolve once, then cache the index.
wil::com_ptr<IWinMLOrtModelSchema> ortSchema;
THROW_IF_FAILED(model->QueryInterface(IID_PPV_ARGS(ortSchema.put())));

UINT32 inputIdsIndex = 0;
THROW_IF_FAILED(ortSchema->FindInputIndex(L"input_ids", &inputIdsIndex));

// Hot loop: bind positionally every iteration.
for (UINT32 step = 0; step < maxSteps; ++step)
{
    THROW_IF_FAILED(stage->BindInput(inputIdsIndex, currentTokenTensor.get()));
    THROW_IF_FAILED(pipeline->Run());
}
```

See [IWinMLOrtNamedBindings](IWinMLOrtNamedBindings.md) and
[IWinMLOrtModelSchema](IWinMLOrtModelSchema.md).
