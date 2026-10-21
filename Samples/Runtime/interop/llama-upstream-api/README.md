<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# llama.cpp upstream API

This sample calls llama.cpp's own C API, declared in `llama.h`, against the
llama.cpp build that Windows ML ships in the
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` package. It uses no Windows
ML API. It loads the ggml backends deployed beside the executable, loads a GGUF
model, formats a prompt with the model's chat template, generates up to 64
tokens with greedy sampling, and prints the response.

Calling llama.cpp directly is an explicit opt-in. The package adds llama.cpp's
headers and import libraries to a project only when the project sets the
property described in [How linking works](#how-linking-works).

[Hello LanguageModel](../../language/hello-language-model/) runs a GGUF model
through the Windows ML Runtime API instead.

## Prerequisites

- Visual Studio with **Desktop development with C++**.
- The `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` package version
  pinned in [`Samples\Directory.Packages.props`](../../../Directory.Packages.props).
  It provides the llama.cpp headers (`include\llama`) and import libraries
  (`lib\native\<arch>`) for x64 and ARM64, and NuGet restores it when the
  sample builds. See [Package restore](../../README.md#package-restore).
- A GGUF decoder model. The commands below use the model that the GGUF samples
  use.

## Build and run

Run these commands from `Samples\Runtime`. `check_artifacts.ps1` reports the
GGUF model and prints the command that downloads it when it is missing.

```powershell
.\check_artifacts.ps1 -Sample llm-chat-gguf
.\build.ps1 -Configuration Release -PackageOnly -Sample llama-upstream-api

$platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }
& ".\out\$platform\Release\llama-upstream-api\llama-upstream-api.exe" `
  --model .\models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf
```

`--prompt` replaces the default prompt, and `--max-tokens` changes the token
limit:

```powershell
& ".\out\$platform\Release\llama-upstream-api\llama-upstream-api.exe" `
  --model .\models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf `
  --prompt "Name three primary colors." --max-tokens 32
```

The sample prints the devices that the loaded backends registered, the prompt,
and the response. llama.cpp errors go to standard error. The exit code is 0 on
success, 1 when a step fails, and 2 for invalid arguments.

## How linking works

The project file sets one property:

```xml
<WinMLLinkLlamaUpstreamApi>true</WinMLLinkLlamaUpstreamApi>
```

With it, the LibLlama.Core package targets add the package's `include\llama`
folder to the include path and link `WinMLllama.lib`, `WinMLggml.lib`,
`WinMLggml-base.lib`, and `WinMLmtmd.lib` from `lib\native\<arch>`. These are
the import libraries of the llama.cpp core that the package copies beside the
executable (`WinMLllama.dll`, `WinMLggml.dll`, `WinMLggml-base.dll`, and
`WinMLmtmd.dll`), so code written against `llama.h`, or `mtmd.h` for multimodal
input, builds and runs without changes.

- The property is off by default, so the package's headers and libraries don't
  take precedence over a llama.cpp that a project builds or references itself.
- The package targets read the property after the project body, so it can be
  set anywhere in the project.
- An import library that no code references adds no DLL dependency. This sample
  doesn't use `mtmd.h`, so its executable doesn't import `WinMLmtmd.dll`.

CMake projects use the package's config in `build\cmake`
(`find_package(microsoft.windows.ai.machinelearning.libllama.core CONFIG)`) and
link the `WinML::llama` imported target, or `WinML::mtmd` for multimodal input.

The headers are llama.cpp's public headers at the llama.cpp release that the
package is built from. `WinMLLlamaCoreManifest.json` beside the executable
records that release (`llamaBuild` and `llamaCommit`). llama.cpp's API can
change between releases, so rebuild the application when you update the
package.

## Backends

ggml backends are separate DLLs that llama.cpp loads at run time, so the
application doesn't link them. The build copies the CPU backend to
`LlamaBackends\cpu` beside the executable. Before it loads the model, the sample
calls `ggml_backend_load_all_from_path` on each folder under `LlamaBackends`.

ggml loads a backend DLL by its full path, but Windows resolves the backend's
own imports through the standard DLL search order, which doesn't include the
backend's folder. The sample passes the folder to `SetDllDirectoryW` while ggml
loads it, so libraries placed beside a backend, such as the NVIDIA libraries
that a CUDA backend imports, resolve.

A GPU backend placed in `LlamaBackends\<family>`, for example one built with
[`Build-LlamaBackend.ps1`](../../../../Tools/llama/README.md), loads the same
way. llama.cpp's default model parameters then offload all of the model's
layers to the GPU devices that the loaded backends register.

ggml checks a backend's API version but doesn't read
`WinMLLlamaBackendManifest.json`, so this sample doesn't verify, as the Windows
ML runtime does, that a backend was built for the package's llama.cpp commit.
Build each backend for the LibLlama.Core package version that the sample uses,
as [Package matching](../../../../Tools/llama/README.md#package-matching)
describes.
