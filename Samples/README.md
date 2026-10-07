# Windows ML Samples

## Windows ML Runtime, Task API, and Server samples

The Windows ML Runtime API gives Windows applications one programming model for
local AI on CPUs, GPUs, and NPUs. The same Runtime objects run ONNX models
through ONNX Runtime and GGUF models through llama.cpp.

- [`Runtime`](Runtime/): C++ and Python samples organized by scenario, covering
  getting started, vision, speech, language, model compilation, and ONNX Runtime
  interoperability.
- [`Tasks`](Tasks/): higher-level Task API samples for text generation, chat
  completion, automatic speech recognition, and task composition over
  caller-owned Runtime objects.
- [`Server`](Server/): host the Windows ML Server, which serves a language model
  to coding agents and other OpenAI-compatible clients on the same computer,
  with C++, C#, and Python clients.

> [!NOTE]
> The Runtime, Task, and Server APIs are new and still evolving, and they may
> change. Report issues and share feedback through
> [GitHub Issues](https://github.com/microsoft/WindowsML/issues).

## Windows App SDK samples

Additional Windows ML samples for the Windows App SDK can be found in the **WindowsAppSDK-Samples** repository, alongside the rest of the Windows App SDK samples:

➡️ **[microsoft/WindowsAppSDK-Samples — Samples/WindowsML](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML)**

## Quick links

### C++ (MSBuild)

- [CppConsoleDesktop](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp/CppConsoleDesktop)
- [CppConsoleDesktop.FrameworkDependent](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp/CppConsoleDesktop.FrameworkDependent)
- [CppConsoleDesktop.SelfContained](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp/CppConsoleDesktop.SelfContained)
- [CppConsoleDesktop.GenAI](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp/CppConsoleDesktop.GenAI)
- [CppConsoleDll](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp/CppConsoleDll)
- [CppResnetBuildDemo](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp/CppResnetBuildDemo)

### C++ (CMake)

- [ResNetConsoleDesktop](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp-cmake/ResNetConsoleDesktop)
- [ResNetConsoleDesktop.SelfContained](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp-cmake/ResNetConsoleDesktop.SelfContained)
- [WinMLEpCatalog](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cmake/WinMLEpCatalog)

### C++ ABI

- [CppAbiEPEnumerationSample](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cpp-abi)

### C# (.NET)

- [CSharpConsoleDesktop](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cs/CSharpConsoleDesktop)
- [ResnetBuildDemoCS](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cs/ResnetBuildDemoCS)
- [HelloPhi](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cs/HelloPhi)
- [cs-wpf](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cs-wpf)
- [cs-winforms](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cs-winforms)
- [cs-winui](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/cs-winui)

### Python

- [SqueezeNetPython](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/python)

### Diagnostics

- [capture-logs](https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsML/capture-logs)
