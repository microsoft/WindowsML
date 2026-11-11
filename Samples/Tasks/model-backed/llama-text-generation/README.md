<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Text generation with llama.cpp

Load a GGUF file through the llama.cpp model loader and generate text with it.
llama.cpp is an explicit, engine-specific choice: Windows ML uses it for a
model only when the app loads the model this way, and only for text
generation.

1. Query the runtime for `IWinMLLlamaModelLoader` (Python:
   `Runtime.load_llama_model`). The loader is present when the Windows ML
   package includes the llama.cpp engine.
2. Load the model, and optionally a smaller draft model, with the loader's
   `LoadModelFromFile`.
3. Call `SetModel`, passing the draft model in
   `WINML_TEXT_GENERATION_MODEL_OPTIONS`, and select the `DRAFT_MODEL`
   speculative method. `SetModel` reads the tokenizer from the GGUF file.
4. Create the task with the configuration's tokenizer and stream a reply. Each
   session loads the weights.

With a draft model, the draft proposes tokens that the model verifies, and the
sample prints how many were accepted:

```text
Speculative decoding: steps=12 drafted=36 accepted=30
```

A draft model must share the model's tokenizer and must be loaded through the
same loader.

Use this sample for a GGUF file that Windows ML does not plan for the GPU, or
when you want llama.cpp itself. The llama.cpp packages carry the CPU backend:
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` for C++ and the
`windowsml-llama-core` wheel for Python. The sample runs on the CPU, its
default target. `--device gpu` needs a llama.cpp GPU backend that you build
and deploy yourself (see [GPU backends](../../../../docs/Runtime/gguf-models.md#gpu-backends));
without one, session creation returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`
and the sample says so.

```powershell
..\..\run_llama_text_generation.ps1
..\..\run_llama_text_generation.ps1 -ModelPath <model.gguf> -DraftModel <draft.gguf>
python.exe .\main.py ..\..\models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf
```

When the deployed Windows ML package does not include the loader,
`QueryInterface` returns `E_NOINTERFACE` and `Runtime.load_llama_model` raises
`NotSupportedError`. Sessions also need the llama.cpp payload deployed with the
app: the `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` package for C++,
or the `windowsml-llama-core` wheel of the same version as `windowsml` for
Python (see [Package requirements](../README.md#package-requirements)).
Without it, loading the model and `SetModel` still succeed, and the first
session creation fails with `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)`. The
sample explains both cases. See [C++ payload deployment](../../../../docs/Runtime/gguf-models.md#c-payload-deployment)
and [Python payload install](../../../../docs/Runtime/gguf-models.md#python-payload-install).

See [Models-backed Tasks](../README.md) for package and model requirements.
