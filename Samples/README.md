# Windows ML Samples

## Windows ML Runtime, Task API, and Server samples

The Windows ML Runtime API gives Windows applications one programming model for
local AI on CPUs, GPUs, and NPUs. The same Runtime objects run ONNX models
through ONNX Runtime and GGUF models through llama.cpp, and Tasks can take
published model weights directly and run them on the GPU target the
application picks.

- [`Runtime`](Runtime/): C++ and Python samples organized by scenario, covering
  getting started, vision, speech, language, model compilation, and ONNX Runtime
  interoperability.
- [`Tasks`](Tasks/): higher-level Task API samples. Models-backed Task samples give a
  Task published weights, an exported model described as a composition, or a
  GGUF file loaded explicitly with llama.cpp, for text generation, speech
  recognition, and typed decisions. Other samples cover chat completion,
  speech recognition, and task composition over caller-owned Runtime objects.
- [`Server`](Server/): host the Windows ML Server, which serves a language model
  to coding agents and other OpenAI-compatible clients on the same computer,
  with C++, C#, and Python clients.

> [!NOTE]
> The Runtime, Task, and Server APIs are new and still evolving, and they may
> change. Report issues and share feedback through
> [GitHub Issues](https://github.com/microsoft/WindowsML/issues).

## Recover disk space

The samples keep models, build outputs, and model preparation tools in these
places. Delete what you no longer need.

| Location | Contents | How to recover the space |
|---|---|---|
| `Runtime\models`, `Tasks\models`, and `Server\models` | The models you download and the models the preparation scripts produce. | Delete a model you no longer need. `check_artifacts.ps1` prints how to obtain it again. |
| Next to a model file | A compiled copy of the model, `<model>_ctx.onnx`, that some execution providers save the first time they run it, and the context or engine files (`.bin`, `.engine`) they save beside it. | Delete them. The provider saves them again the next time it runs the model. |
| `out` and `obj` in `Runtime`, `Tasks`, and `Server`, and the `bin` and `obj` folders next to the projects | Build outputs. | Delete them. The next build creates them again. |
| `%LOCALAPPDATA%\WinMLSampleTools` | The Python environments of the model preparation scripts. On an ARM64 PC the language model export uses `llm-export-py311-x64-v1`, so an `llm-export-py311-v1` folder there is left from an earlier version of the samples. | Delete the environments you no longer use, or the whole folder. The scripts create the environments they need again. |
| `%USERPROFILE%\.cache\huggingface\hub`, unless you moved it with `HF_HOME` or `HF_HUB_CACHE` | The model that the language model export downloads, in a folder such as `models--Qwen--Qwen2.5-0.5B-Instruct`. This is Hugging Face's shared cache. | Delete the model's folder if no other project uses it. |
| `%APPDATA%\uv\python` | A Python that uv downloads for the language model export when no matching Python is installed. On an ARM64 PC this is x64 Python. | `uv python list --only-installed` shows it, and `uv python uninstall` removes a version that nothing else uses. Also delete the `llm-export` environments in `%LOCALAPPDATA%\WinMLSampleTools`, which stop working without it. |
| `%LOCALAPPDATA%\WindowsMLTaskStudio\CompiledPrograms` | The programs that [Task Studio](../Apps/TaskStudio/) compiles. | In Task Studio, open **Settings**, expand **Advanced**, and select **Delete program cache**, which is available when no sessions are loaded. You can also delete the folder while Task Studio is closed. |

A program cache, whether Task Studio's or the folder you pass to a sample's
`-ProgramCache`, keeps the programs that earlier Windows ML versions compiled.
After you update Windows ML, delete it to recover that space. The next run
compiles the programs again.

The language model export and the model preparation scripts keep the packages
they install out of the uv and pip caches, so deleting their environments frees
that space. Earlier versions of the samples also left the packages in those
caches, and uv shares those files with the environment, so the space returns
only after you delete the environment and clear the cache.
`uv cache clean torch` removes PyTorch, the largest of them. `uv cache clean`
and `pip cache purge` clear everything, including what other projects have
cached.

## Optional model MSIX sample

- [`OptionalMsixMinimal`](OptionalMsixMinimal/): a standalone Windows Forms sample
  demonstrating optional model MSIX packaging, installation, ONNX inference, and
  optional-package removal.

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
