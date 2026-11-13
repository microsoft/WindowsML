<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLLlamaModelLoader

> Part of the [WinML Runtime API Reference](README.md). Declared in
> `winml/runtime/WinMLRuntimeLlama.h`, with `IWinMLLlamaStageOptions`.

`IWinMLLlamaModelLoader` is the explicit, engine-specific way to run a GGUF
model on llama.cpp from a Models-backed Task. Windows ML uses llama.cpp for a
model given to `SetModel` only when the application loaded it through this
interface; it never selects llama.cpp on its own or moves a model to it.

```
IWinMLLlamaModelLoader   IID: b9725a87-e502-4be6-a9ae-1185fd9b77cc
IWinMLLlamaStageOptions  IID: 516a4e40-9d1a-4ef3-9d4e-fd6de35c5354
```

## When to use it

| The application has | Load it with | Then |
|---|---|---|
| A published model that Windows ML supports, as a checkpoint folder or a GGUF file | `IWinMLRuntime::LoadModelFromPath` or `LoadModelFromFile` | `SetModel`; Windows ML runs it on the GPU target the application picked. |
| A GGUF file that the application wants to run on llama.cpp, for example an architecture that Windows ML does not support, CPU execution, or speculative decoding with a draft model | `IWinMLLlamaModelLoader::LoadModelFromFile` | `SetModel`; each session runs a llama.cpp stage. |
| A GGUF file in a pipeline the application builds | `IWinMLRuntime::LoadModelFromFile` | `IWinMLPipelineBuilder::AddModelStage` builds a llama.cpp stage. See [GGUF language models](../Runtime/gguf-models.md). |

## Availability and payload

Query `IWinMLRuntime` for `IWinMLLlamaModelLoader`. A Windows ML package that
includes the llama.cpp backend exposes the interface; otherwise
`QueryInterface` returns `E_NOINTERFACE`, and Python's `Runtime.load_llama_model`
raises `NotSupportedError`.

The llama.cpp engine modules ship separately from the Runtime: the
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` NuGet package for C++, or
the `windowsml-llama-core` wheel for Python. The interface is exposed whether or not
they are deployed, and loading a model reads no engine module. Without them, the
first call that needs the engine returns `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)`:
building a pipeline that runs the model, creating a text generation session that
serves it, or creating a tokenizer from the file when only the engine reads its
vocabulary.

## `LoadModelFromFile`

```cpp
HRESULT LoadModelFromFile(
    [in, string] LPCWSTR path,
    [in, unique] IWinMLResourceMapReader* resources,
    [out, retval] IWinMLModel** model);
```

Loads the GGUF file at `path` exactly as `IWinMLRuntime::LoadModelFromFile` loads
it and binds the model to llama.cpp.

| Parameter | Description |
|---|---|
| `path` | Path to a `.gguf` file. For a sharded model, the first shard. |
| `resources` | Optional resource map, as for `IWinMLRuntime::LoadModelFromFile`. |
| `model` | Receives the loaded model. |

**Returns:** `S_OK` on success, `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a
file that is not a GGUF model, and otherwise what
`IWinMLRuntime::LoadModelFromFile` returns.

A model loaded this way serves text generation only:

- `IWinMLModelTextGenerationConfiguration::SetModel` reads none of its weights.
  Each `CreateSession` composes a llama.cpp stage from it and loads the weights.
  A draft model in `WINML_TEXT_GENERATION_MODEL_OPTIONS` must be loaded through
  this method too; it enables the `DRAFT_MODEL` and `MODEL` speculative methods.
  See [llama.cpp sessions](IWinMLTextGenerationTask.md#llamacpp-sessions).
- Without a tokenizer set, `SetModel` creates the tokenizer from the GGUF file.
- Speech recognition `SetModel` and `CreateDecisionTaskFromModel` return
  `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for it.
- `IWinMLPipelineBuilder::AddModelStage` runs it as a llama.cpp stage.

```cpp
wil::com_ptr<IWinMLLlamaModelLoader> loader;
HRESULT hr = runtime->QueryInterface(IID_PPV_ARGS(loader.put()));
if (hr == E_NOINTERFACE)
{
    // This Windows ML package does not include the llama.cpp backend.
    return hr;
}

RETURN_IF_FAILED(hr);
wil::com_ptr<IWinMLModel> model;
RETURN_IF_FAILED(loader->LoadModelFromFile(modelPath, nullptr, model.put()));
wil::com_ptr<IWinMLModel> draft;
RETURN_IF_FAILED(loader->LoadModelFromFile(draftPath, nullptr, draft.put()));

WINML_TEXT_GENERATION_MODEL_OPTIONS options{};
options.presentFields = WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_DRAFT_MODEL;
options.draftModel = draft.get();
RETURN_IF_FAILED(modelConfiguration->SetModel(cpuTarget.get(), model.get(), &options));

wil::com_ptr<IWinMLTextGenerationSpeculativeConfiguration> speculative;
RETURN_IF_FAILED(configuration->QueryInterface(IID_PPV_ARGS(speculative.put())));
WINML_TEXT_GENERATION_SPECULATIVE_OPTIONS speculativeOptions{};
speculativeOptions.method = WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL;
RETURN_IF_FAILED(speculative->SetSpeculativeOptions(&speculativeOptions));
```

In Python, call `runtime.load_llama_model(path)`. See the
[llama.cpp text generation sample](../../Samples/Tasks/model-backed/llama-text-generation/).

## Targets and backends

The LibLlama.Core package and the `windowsml-llama-core` wheel deploy the CPU
backend, so an application runs llama.cpp on a CPU target unless it deploys a
GPU backend that it builds; see
[GPU backends](../Runtime/gguf-models.md#gpu-backends). A GPU target
without a matching backend fails `CreateSession` with
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.

## IWinMLLlamaStageOptions

Query a llama.cpp model stage (`IWinMLStage::QueryInterface`) for
`IWinMLLlamaStageOptions` to set llama.cpp context settings before
`IWinMLPipelineBuilder::Build`. Other stages return `E_NOINTERFACE`. After
`Build`, setters return `E_NOT_VALID_STATE`; while a `Build` that includes the
stage runs, they return `HRESULT_FROM_WIN32(ERROR_BUSY)`. Getters return the
configured values, not the engine's effective values.

| Method | Behavior |
|---|---|
| `SetThreadCount`, `GetThreadCount` | Generation thread count. Zero selects the engine default; values above 512 return `E_INVALIDARG`. |
| `SetBatchThreadCount`, `GetBatchThreadCount` | Prompt and batch thread count, with the same rules. |
| `SetMicroBatchSize`, `GetMicroBatchSize` | Physical micro-batch size. Zero selects the smaller of the context length and 512. `Build` returns `E_INVALIDARG` for a nonzero size larger than the context length, or, when the stage drafts with the model's built-in multi-token prediction layers, smaller than the draft token limit plus one. |

Python exposes these as `Stage.llama_options()` with the `thread_count`,
`batch_thread_count`, and `micro_batch_size` properties.
