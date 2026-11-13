<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# GGUF language models

Windows ML Runtime loads GGUF decoder models through the llama.cpp backend. The
same language helper and Runtime pipeline flow are used for ONNX Runtime and
llama.cpp.

## Supported samples

These Runtime samples accept GGUF:

| Sample | PowerShell backend option | Python backend option |
|---|---|---|
| [Hello LanguageModel](../../Samples/Runtime/language/hello-language-model/) | `-Backend llama` | `--backend llama` |
| [LLM chat](../../Samples/Runtime/language/llm-chat/) | `-Backend llama -Mode unified` | `--backend llama --mode unified` |
| [Speech to LanguageModel](../../Samples/Runtime/language/speech-to-language-model/) | `-Backend llama` | `--backend llama` |

GGUF uses the `unified` language-model layout only. The `split` layout uses
`emb.onnx`, `decoder.onnx`, and `head.onnx` through ONNX Runtime.

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
CPU by default. They reject an explicit GPU request while no GPU backend is
present, with `LoadModel failed with HRESULT 0x80070032`. Use `-Device cpu`, or
add a GPU backend as described in [GPU backends](#gpu-backends).

Projects that don't reference the LibLlama.Core package get no llama.cpp
payload. To keep a referencing project from deploying it, set
`WinMLCopyLlamaRuntime` to `false`.

## Python payload install

The llama.cpp backend ships as wheels that install beside the `windowsml`
Runtime and pin the same `windowsml` version. Install them with the same
`python.exe` that runs the samples. The `windowsml-llama-core` wheel contains
the Runtime llama.cpp adapter, the llama.cpp core, and the CPU backend:

```powershell
python.exe -m pip install --pre windowsml-llama-core numpy
```

It includes no GPU backend, so `--device gpu` is rejected until you add one as
described in [GPU backends](#gpu-backends). Use `--device cpu` for CPU
inference.

Run a GGUF sample:

```powershell
python.exe .\language\hello-language-model\main.py --backend llama --device cpu
```

To install local wheels, copy them into [`localpackages`](../../Samples/Runtime/localpackages/README.md#python-wheels)
and pin their version:

```powershell
python.exe -m pip install --pre --find-links .\localpackages "windowsml-llama-core==<windowsml-version>" numpy

# For example, for Runtime package 2.7.2021-experimental:
python.exe -m pip install --pre --find-links .\localpackages "windowsml-llama-core==2.7.2021a0" numpy
```

Remove the llama.cpp backend when that environment no longer needs it:

```powershell
python.exe -m pip uninstall -y windowsml-llama-cuda windowsml-llama-core
```

pip leaves `nvidia-cublas` installed because other packages, such as PyTorch,
can use it. Uninstall it separately if nothing else in the environment needs
it. A backend that you built into the environment isn't tracked by pip. Delete
its `LlamaBackends\<family>` folder yourself.

## GPU backends

### NVIDIA CUDA

Windows ML publishes the llama.cpp CUDA backend for x64 and ARM64. It is
opt-in, so the CPU backend stays the default. The CUDA backend runs on an
NVIDIA GPU with a driver that supports CUDA 13.

For the C++ samples, pass `-LlamaCuda` to `build.ps1`. The GGUF projects then
also reference the `Microsoft.Windows.AI.MachineLearning.LibLlama` package,
which brings the `LibLlama.Cuda.win-x64` and `LibLlama.Cuda.win-arm64`
packages, and the build deploys the backend for the target platform:

```powershell
.\build.ps1 -Sample hello-language-model -LlamaCuda -PackageOnly
.\run_hello_language_model.ps1 -Backend llama -Device gpu
```

```text
out\<platform>\Release\hello-language-model\
  LlamaBackends\
    cpu\ggml-cpu.dll, WinMLLlamaBackendManifest.json
    cuda\ggml-cuda.dll, WinMLLlamaBackendManifest.json
    cuda\cublas64_13.dll, cublasLt64_13.dll
```

The backend needs NVIDIA cuBLAS 13.7 or later (`cublas64_13.dll` and
`cublasLt64_13.dll`, from CUDA Toolkit 13.4 or later), which the packages don't
include. The build copies cuBLAS into `LlamaBackends\cuda` from the first of
these sources that has it:

1. The folder that the `WinMLLlamaCudaRedistDirectory` property names.
2. The CUDA Toolkit that `CUDA_PATH`, then `CUDA_HOME`, names. The toolkit
   installer sets `CUDA_PATH`. Set `WinMLLlamaCudaUseToolkitRedist` to `false`
   to skip this source.
3. NVIDIA's cuBLAS redistributable, downloaded only after you accept the
   [NVIDIA CUDA Toolkit EULA](https://docs.nvidia.com/cuda/eula/) by setting
   `WinMLLlamaCudaAcceptNvidiaLicense` to `true`. The first build downloads the
   pinned archive (about 155 MB for ARM64 and 425 MB for x64), verifies its
   SHA-256, and caches cuBLAS in
   `%LOCALAPPDATA%\Microsoft\WindowsML\LibLlama\CudaRedist`. Later builds reuse
   the cache.

MSBuild reads environment variables as properties, so you can set these before
you run `build.ps1`:

```powershell
$env:WinMLLlamaCudaRedistDirectory = "D:\redist\cublas\arm64"
.\build.ps1 -Sample hello-language-model -LlamaCuda -PackageOnly
```

If no source provides cuBLAS, the build warns with `WINMLLLAMA0001`, the
Runtime can't load the CUDA backend, and the samples still run GGUF models with
`-Device cpu`. cuBLAS is NVIDIA software under NVIDIA's license. You are
responsible for accepting that license and for meeting its terms, including its
redistribution terms, before you ship an application that includes it.

A later build without `-LlamaCuda` leaves `LlamaBackends\cuda` in the output
folder. Delete that folder to return a sample to the CPU backend.

For Python, install the `windowsml-llama-cuda` wheel. It adds the CUDA backend
to `windowsml-llama-core` and installs NVIDIA's `nvidia-cublas` wheel, which
NVIDIA licenses under its own terms. `windowsml[llama]` installs the CPU and
CUDA backends together.

```powershell
python.exe -m pip install --pre windowsml-llama-cuda numpy
python.exe .\language\hello-language-model\main.py --backend llama --device gpu

# Or, for local wheels from the same build as windowsml:
python.exe -m pip install --pre --find-links .\localpackages "windowsml-llama-cuda==<windowsml-version>" numpy
```

If cuBLAS is missing or can't be loaded, `import windowsml.runtime` warns once
with the command that fixes it, and the CPU backend still works. Import
`windowsml.runtime` before other packages that load cuBLAS, such as PyTorch. A
different cuBLAS that is already loaded makes the import fail.

### Other backend families

For another ggml backend family, such as Vulkan, build a matching backend with
[`Build-LlamaBackend.ps1`](../../Tools/llama/README.md) and place it in
`LlamaBackends\<family>` beside the application. Windows ML doesn't
redistribute the vendor runtimes those backends load.

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
