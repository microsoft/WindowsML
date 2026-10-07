<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# llama.cpp backends

Windows ML ships only the llama.cpp CPU backend, in
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` and the
`windowsml-llama-core` wheel. It doesn't ship or redistribute GPU backends or
vendor runtimes, including NVIDIA CUDA components. The language samples run on
the CPU with that package alone. To run a GGUF model on a GPU, build a backend
with this folder's script.

`Build-LlamaBackend.ps1` builds a standard upstream `ggml-<family>.dll`, such as
`cuda` for NVIDIA GPUs, `vulkan`, or `opencl`, that matches the Core package.
The output is loaded by `WinMLRuntimeLlama.dll`; it is not a WinML-specific
backend fork. Run the commands below from the repository root, and use the
script from the same branch as the samples you build.

## NVIDIA CUDA responsibilities

The `cuda` backend depends on NVIDIA software that Windows ML doesn't provide:

- You install the NVIDIA CUDA Toolkit yourself, and the script builds against
  it.
- The built `ggml-cuda.dll` imports NVIDIA runtime libraries, such as cuBLAS.
  The script warns about each one. You copy the libraries your build needs into
  `LlamaBackends\cuda` yourself.
- You are responsible for accepting and complying with NVIDIA's license terms
  for the CUDA Toolkit and for any NVIDIA library you copy or distribute with
  your application, including NVIDIA's redistribution terms. Windows ML and
  these samples don't accept them for you. Review those terms, and consult your
  legal counsel where needed, before you ship an application that includes
  NVIDIA components.

The CUDA backend also needs an NVIDIA driver that supports the toolkit version
you build with. This flow was exercised on Windows ARM64 with Toolkit 13.4
(MSVC 14.44) and a CUDA 13 driver: the backend built, and a GGUF model ran with
`--device gpu` once `cublas64_13.dll` and `cublasLt64_13.dll` from the toolkit
were present in `LlamaBackends\cuda`. Without them, `--device gpu` fails with
`0x80070032`.

The script:

1. verifies the expected Core package SHA-256, NuGet identity, and signature
   policy;
2. reads the llama.cpp commit, build number, backend API version, and runtime
   contract from the package's core and CPU backend manifests;
3. verifies that the output directory contains exactly the package's core and
   CPU payload before adding a backend;
4. checks out that upstream llama.cpp commit in a working directory;
5. builds only `ggml-<family>.dll` against the WinML-namespaced core with the
   hybrid CRT (static VC++ runtime, system UCRT);
6. verifies the plugin architecture and that it imports `WinMLggml-base.dll`,
   no upstream core module, and no VC++ runtime DLL, and warns about any other
   import that isn't a system module;
7. writes the matching `WinMLLlamaBackendManifest.json`.

## Prerequisites

- Visual Studio 2022 or 2026 Developer PowerShell with the v143 C++ toolset
  (MSVC 14.30 to 14.49). Visual Studio 2026 doesn't install it by default; add
  an MSVC v143 build tools component in the Visual Studio Installer.
- Windows long paths enabled (`LongPathsEnabled`). The Vulkan build creates
  nested paths longer than 260 characters.
- Git and CMake.
- The SDK that the backend family's upstream build requires. For `cuda`, the
  NVIDIA CUDA Toolkit (set `CUDA_PATH` or pass
  `-CMakeOption '-DCUDAToolkit_ROOT=<toolkit directory>'`). For example, Vulkan
  needs a Vulkan SDK that supplies headers, the loader import library, and
  `glslc`.
- The `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` NuGet package that the
  application references, and an application output built with it.

## Build

Build the sample first so its output contains the Core payload, then build the
backend into that output:

```powershell
$corePackage = '<path to Microsoft.Windows.AI.MachineLearning.LibLlama.Core.<version>.nupkg>'
$expectedSha256 = '<64-character digest you verified>'
$platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }

.\Tools\llama\Build-LlamaBackend.ps1 `
  -Backend cuda `
  -CMakeOption '-DCUDAToolkit_ROOT=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4' `
  -CorePackage $corePackage `
  -ExpectedPackageSha256 $expectedSha256 `
  -OutputRoot ".\Samples\Runtime\out\$platform\Release\llm-chat"
```

`-Backend` selects the upstream `ggml-<family>` target and the
`GGML_<FAMILY>` option. The CPU backend ships in the Core package, so `cpu` is
not accepted.

The output payload is:

```text
Samples\Runtime\out\<platform>\Release\llm-chat\
  WinMLRuntimeLlama.dll
  WinMLllama.dll
  WinMLggml.dll
  WinMLggml-base.dll
  WinMLmtmd.dll
  WinMLLlamaCoreManifest.json
  LlamaBackends\
    cpu\
      ggml-cpu.dll
      WinMLLlamaBackendManifest.json
    cuda\
      ggml-cuda.dll
      WinMLLlamaBackendManifest.json
      (NVIDIA runtime libraries that you copy here)
    vulkan\
      ggml-vulkan.dll
      WinMLLlamaBackendManifest.json
```

A rebuild of the sample leaves `LlamaBackends\<family>` in place; a clean
removes it. Rebuild the backend whenever the Core package version changes,
because the manifest pins the exact llama.cpp commit.

Windows ML does not redistribute vendor runtimes such as `vulkan-1.dll`,
`OpenCL.dll`, or NVIDIA CUDA libraries. The runtime loads them from the system
or from `LlamaBackends\<family>`. If the script warns that the backend imports a
module that isn't a system module, copy that module into
`LlamaBackends\<family>` after the script finishes, subject to that module's
license terms. The runtime selects another backend when a backend is absent or
unusable.

## Python

The `windowsml-llama-core` wheel installs the same Core payload as the
`LibLlama.Core` package from the same Windows ML release, so a Python
environment with `windowsml-llama-core` installed can take a backend too. Pass the
environment's `windowsml\lib` directory as `-OutputRoot`:

```powershell
$python = 'C:\venvs\winml\Scripts\python.exe'
$library = & $python -c "import pathlib, windowsml; print(pathlib.Path(windowsml.__file__).parent / 'lib')"

.\Tools\llama\Build-LlamaBackend.ps1 `
  -Backend cuda `
  -CMakeOption '-DCUDAToolkit_ROOT=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4' `
  -CorePackage $corePackage `
  -ExpectedPackageSha256 $expectedSha256 `
  -OutputRoot $library
```

The script refuses an environment whose payload doesn't match the package.
Rebuild the backend after you upgrade `windowsml`. `pip uninstall` doesn't
remove `LlamaBackends\<family>`; delete that folder yourself.

## Family options

Pass extra configure options for the selected family with `-CMakeOption`:

```powershell
.\Tools\llama\Build-LlamaBackend.ps1 `
  -Backend opencl `
  -CMakeOption '-DGGML_OPENCL_USE_ADRENO_KERNELS=OFF' `
  -CorePackage $corePackage `
  -ExpectedPackageSha256 $expectedSha256 `
  -OutputRoot ".\Samples\Runtime\out\$platform\Release\llm-chat"
```

Only `GGML_<FAMILY>_*` options for the selected family and `<Package>_ROOT`
search hints are accepted. The script rejects toolchain, compiler, CRT, flag,
and core ggml options, because they could break the Core contract.

## Package matching

The backend must match the Core package's llama.cpp commit, build number,
backend API version, and runtime contract. The runtime rejects a backend whose
manifest doesn't match the Core it is placed beside, or whose file hash doesn't
match its manifest.

The script reads these values from the Core package's own
`WinMLLlamaCoreManifest.json` and CPU backend manifest, so the package is the
source of truth rather than anything restated here. Core's source patches are
Windows ML fixes to the core; they don't change the ggml backend interface, so
the script builds the backend from the unpatched upstream commit. Tensor types
that only those patches add, such as `W3_G32`, aren't supported by the built
backend, so the runtime runs them on a backend that ships in the packages.

Pass the package SHA-256 as `-ExpectedPackageSha256`; the script refuses to
proceed if the package does not match. It also verifies the NuGet signature with
`dotnet nuget verify --all`. Use `-AllowUnsignedPackage` only when you have
separately approved an unsigned package, such as a pipeline build you are
validating.
