<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# GGUF language models

Windows ML Runtime pipelines run GGUF decoder models through the llama.cpp
backend. The same language helper and Runtime pipeline flow are used for ONNX
Runtime and llama.cpp. llama.cpp is reached by an explicit choice: a GGUF model
in a pipeline that the application builds, or a GGUF model that the application
loads with the llama.cpp loader for a Task.

## Supported samples

These Runtime samples accept GGUF:

| Sample | PowerShell backend option | Python backend option |
|---|---|---|
| [Hello LanguageModel](../../Samples/Runtime/language/hello-language-model/) | `-Backend llama` | `--backend llama` |
| [LLM chat](../../Samples/Runtime/language/llm-chat/) | `-Backend llama -Mode unified` | `--backend llama --mode unified` |
| [Speech to LanguageModel](../../Samples/Runtime/language/speech-to-language-model/) | `-Backend llama` | `--backend llama` |

GGUF uses the `unified` language-model layout only. The `split` layout uses
`emb.onnx`, `decoder.onnx`, and `head.onnx` through ONNX Runtime.

## GGUF files in Models-backed Tasks

A text generation Task that is given a GGUF model with `SetModel` serves it
according to how the application loaded it, and never switches engines:

- Loaded with `IWinMLRuntime::LoadModelFromFile` or `LoadModelFromPath`
  (Python `Runtime.load_model`), Windows ML plans the model into CGC programs
  for the GPU target (see [The CGC backend](backends.md#the-cgc-backend)). A
  file whose architecture or weight types Windows ML does not plan fails
  `SetModel` with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.
- Loaded with `IWinMLLlamaModelLoader::LoadModelFromFile` (Python
  `Runtime.load_llama_model`), each session runs the model on llama.cpp, with
  the payload described below. A draft model loaded the same way enables
  speculative decoding.

See [Models-backed Tasks](../../Samples/Tasks/model-backed/README.md) and
[`IWinMLLlamaModelLoader`](../api-reference/IWinMLLlamaModelLoader.md).

## llama.cpp version

The `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` package and the
`windowsml-llama-core` wheel of Windows ML 2.9 carry llama.cpp build 11847. A
GGUF file loads when that build supports its architecture and tensor types.
Files that use the `W3_G32` tensor type of earlier Windows ML releases no longer
load; requantize them with a standard llama.cpp type. A GPU backend must match
the llama.cpp build and backend API version of the Core package, so rebuild
backends after you update it; see
[Package matching](../../Tools/llama/README.md#package-matching).

## Acquire the GGUF model

```powershell
.\check_artifacts.ps1 -Sample llm-chat-gguf
```

The catalog owns the source URL, size, and SHA-256. Review the publisher's
terms, run the command printed by the script, then run the check again.

## C++ payload deployment

The language C++ projects reference the
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` package. It contains the
Runtime llama.cpp adapter, the llama.cpp core, and the CPU backend. The build
copies the payload for the target platform beside the sample executable:

```powershell
.\build.ps1 -Sample hello-language-model -PackageOnly
```

```text
out\<platform>\Release\hello-language-model\
  WinMLRuntimeLlama.dll, WinMLllama.dll, WinMLggml*.dll, WinMLmtmd.dll
  WinMLLlamaCoreManifest.json
  LlamaBackends\
    cpu\ggml-cpu.dll, WinMLLlamaBackendManifest.json
```

The package includes no GPU backend, so these samples run GGUF models on the
CPU. They reject an explicit GPU request while no GPU backend is present, with
`LoadModel failed with HRESULT 0x80070032`. Use `-Device cpu`, or add a GPU
backend as described in [GPU backends](#gpu-backends).

Projects that don't reference the LibLlama.Core package get no llama.cpp
payload. To keep a referencing project from deploying it, set
`WinMLCopyLlamaRuntime` to `false`.

## Python payload install

The llama.cpp backend ships as the `windowsml-llama-core` wheel, which installs
beside the `windowsml` Runtime and pins the same `windowsml` version. Install it
with the same `python.exe` that runs the samples:

```powershell
python.exe -m pip install --pre windowsml-llama-core numpy
```

The wheel contains the Runtime llama.cpp adapter, the llama.cpp core, and the
CPU backend. It includes no GPU backend, so `--device gpu` is rejected until you
add one as described in [GPU backends](#gpu-backends). Use `--device cpu` for
CPU inference.

Run a GGUF sample:

```powershell
python.exe .\language\hello-language-model\main.py --backend llama --device cpu
```

To install local wheels, copy them into [`localpackages`](../../Samples/Runtime/localpackages/README.md#python-wheels)
and pin their version to the Runtime package version that the samples use
(see [Matching wheel version](../../Samples/Runtime/localpackages/README.md#matching-wheel-version)):

```powershell
python.exe -m pip install --find-links .\localpackages "windowsml-llama-core==$windowsmlVersion" numpy
```

Remove the llama.cpp backend when that environment no longer needs it:

```powershell
python.exe -m pip uninstall -y windowsml-llama-core
```

A backend that you built into the environment isn't tracked by pip. Delete its
`LlamaBackends\<family>` folder yourself.

## GPU backends

Windows ML doesn't ship or redistribute GPU vendor runtimes.
To run GGUF models on a GPU, build the matching ggml backend
with [`Build-LlamaBackend.ps1`](../../Tools/llama/README.md) and place it in
`LlamaBackends\<family>` beside the application, or under the `windowsml\lib`
directory of a Python environment. The tool's README walks through the CUDA
backend for NVIDIA GPUs and also covers other families, such as Vulkan.

For CUDA, you build `ggml-cuda.dll` against your own installation of the NVIDIA
CUDA Toolkit. The CUDA Toolkit, cuBLAS, and the other NVIDIA runtime libraries
the backend loads are NVIDIA's software under NVIDIA's license. You are
responsible for accepting that license and for meeting its terms, including its
redistribution terms, before you copy any NVIDIA file into an application or
ship one to others.

## Shards and context capacity

For a sharded GGUF model, keep every shard in one directory and pass the first
shard to the sample. Set the context capacity to the prompt and generation
window you need.

```powershell
.\run_llm_chat.ps1 `
  -Backend llama `
  -Mode unified `
  -Device cpu `
  -ModelPath D:\models\model-00001-of-00003.gguf `
  -ContextCapacity 64 `
  -MaxTokens 16 `
  -Prompt "Name three primary colors."
```

The prompt and generated tokens must fit within the requested capacity. In
Python, use `--context` for the same capacity hint.
