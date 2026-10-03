<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Windows ML Server samples

The Windows ML Server serves language models to other processes on the same
computer through an OpenAI-compatible Chat Completions endpoint. Coding agents,
the OpenAI libraries, and other OpenAI-compatible clients connect to it the same
way they connect to a hosted service. The application that hosts the server
prepares each model with the Runtime and Task APIs, so the server runs on the
same backends and hardware as the [Task API samples](../Tasks/README.md).

> [!NOTE]
> The Server, Runtime, and Task APIs are new and still evolving, and they may
> change. Report issues and share feedback through
> [GitHub Issues](https://github.com/microsoft/windowsml/issues).

An application hosts the server by loading `WinMLServer.dll`, registering the
models it has prepared, and starting the server in its own process. For
development and testing, the package also includes `WinMLServer.exe`, which
loads a model file and serves it from the command line. Both hosts serve the
same endpoint, so each client works with either one.

| Sample | Languages | What it demonstrates |
| --- | --- | --- |
| [In-process server](host/in-process/) | C++ | Build a Text Generation lane for a GGUF model, register it with the server, start the server in the application's process, and start a client with the endpoint and access key. |
| [Server executable](host/executable/) | PowerShell | Run `WinMLServer.exe`, read the endpoint and access key it prints, and connect a client or a coding agent. |
| [Clients](client/) | C++, C#, Python | List the served models, complete a chat, stream a reply, and complete a tool call. The C++ client uses WinHTTP; the C# and Python clients use the OpenAI libraries. |

The server's interfaces are documented in
[`IWinMLServer`](../../docs/api-reference/IWinMLServer.md).

## Security model

The server is for one user on one computer.

- It listens only on the loopback addresses `127.0.0.1` and `[::1]`, so other
  computers cannot connect to it.
- Each time it starts, it creates a new random access key. Every request must
  send the key as `Authorization: Bearer <key>`. The server rejects a request
  without it before reading the request body.
- Every request must name a loopback address in its `Host` header and must not
  carry an `Origin` header, so web pages cannot reach the server through a
  browser.
- The server never runs tools. When the model calls a tool, the client runs it
  and sends the result in its next request.

The host gives the key only to clients that run as the same user. These samples
pass it to the client in its environment and never on its command line, where
other users on the computer could read it. The clients connect to the server
directly, never through a proxy, which would receive the key with every request.
Treat the key like a password; it stops working when the server stops.

## Build

```powershell
.\build.ps1 -Sample all -Configuration Release
```

`build.ps1` builds the C++ projects with MSBuild, which requires Visual Studio
with the Desktop development with C++ workload, and the C# client with the
.NET 8 SDK. Builds default to the host architecture, restore the
`Microsoft.Windows.AI.MachineLearning` and
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` packages from nuget.org or the
[`localpackages`](localpackages/README.md) override directory, and do not
download models. The Runtime package version must include `WinMLServer.dll` and
`WinMLServer.exe`. The LibLlama.Core package provides the llama.cpp runtime and
its CPU backend for GGUF models; the
[GGUF models guide](../../docs/Runtime/gguf-models.md#c-payload-deployment)
describes how the build deploys them, and
[GPU backends](../../docs/Runtime/gguf-models.md#gpu-backends) how to add a GPU
backend that you build yourself. Pass
`-Platform ARM64` or `-Platform x64` consistently to
both build and run scripts when targeting a different architecture.
The C# client is built into `client\csharp\bin\<Platform>\<Configuration>\net8.0`
so the run scripts select the matching architecture.

## Prepare and run

```powershell
.\check_artifacts.ps1 -Sample server-in-process
.\run_in_process_server.ps1 -Client cpp
.\run_in_process_server.ps1 -Client csharp
.\run_in_process_server.ps1 -Client python

.\check_artifacts.ps1 -Sample server-executable
.\run_server_executable.ps1 -Client cpp
.\run_server_executable.ps1
```

`check_artifacts.ps1` reads `Samples\shared\SampleArtifacts.psd1`, checks
whether the model is present under `Samples\Server\models`, and prints the
command that downloads it if it is missing. Both hosts serve the same Qwen2.5
0.5B Instruct GGUF model as the Task API samples, on the CPU by default. Pass
`-Device gpu` to either run script to run the model on a GPU, where llama.cpp
uses a GPU backend for the device the Runtime selects.

With `-Client`, each run script starts the server, runs the client against it,
and stops the server when the client exits. Without `-Client`,
`run_server_executable.ps1` keeps serving until you press Ctrl+C, so you can
connect a coding agent or another client yourself. Every PowerShell script has
built-in help:

```powershell
Get-Help .\run_server_executable.ps1 -Full
```

## Python

The Python client needs Python 3.10 or later and the OpenAI Python library:

```powershell
python.exe -m pip install openai
.\run_in_process_server.ps1 -Client python
```

The run scripts use the `python.exe` found first on `PATH`. The client uses
only the OpenAI library, so it runs from any Python environment that has it.
