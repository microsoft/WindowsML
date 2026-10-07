<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Server executable

`WinMLServer.exe` serves a model file through the Windows ML Server from the
command line. It loads the model, builds the lanes, starts the server, and
prints the server's address and access key. Use it during development to try
the server with a client or a coding agent. An application that serves models
to its own clients hosts `WinMLServer.dll` in its process, as the
[in-process sample](../in-process/README.md) does.

The `Microsoft.Windows.AI.MachineLearning` package includes `WinMLServer.exe`.
This project compiles no code: `WinMLCopyServerHost` deploys the executable
with the Server and Task runtimes it loads, and the
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` package adds the llama.cpp
runtime and the CPU backend that serves GGUF models.

```powershell
..\..\check_artifacts.ps1 -Sample server-executable
..\..\build.ps1 -Sample executable
..\..\build.ps1 -Sample client-cpp
..\..\run_server_executable.ps1 -Client cpp
```

With `-Client`, the script starts `WinMLServer.exe`, reads the access key and
address from its output, and starts the client with them in the client's
environment. When the client exits, the script stops the server.

```text
WINMLSERVER_MODEL model=qwen2.5-0.5b-instruct-q4_k_m composition=unified context=8192 declared_context=32768 max_output=2048 lanes=1 target=cpu reasoning_effort_mask=1
Serving at http://127.0.0.1:51314/v1

Server: http://127.0.0.1:51314/v1

Models
  qwen2.5-0.5b-instruct-q4_k_m: context window 8192 tokens, output limit 2048 tokens, tool calls supported
...

Client exited with code 0. Server stopped.
```

The client prints the same output with either host; the
[client README](../../client/README.md) shows all of it.

## Output

When the server is ready, `WinMLServer.exe` writes these lines to standard
output and then writes nothing more to it:

| Line | Content |
| --- | --- |
| `WINMLSERVER_ACCESS_KEY <key>` | The access key for this run. |
| `WINMLSERVER_READY <url>` | The base URL clients connect to. |
| `WINMLSERVER_MODEL ...` | One line for each model it serves, with the model id, context window, output limit, lane count, and execution target. |

A script that starts the server reads standard output up to the
`WINMLSERVER_READY` line, as `run_server_executable.ps1` does. Because the key
is on standard output, read it from a pipe; don't redirect it to a file or log.

## Serve a coding agent

Without `-Client`, the server runs in the console until you press Ctrl+C. A
coding agent sends long instructions and many tool definitions with each
request, so request a larger context window:

```powershell
..\..\run_server_executable.ps1 -ContextTokens 32768
```

```text
WINMLSERVER_ACCESS_KEY <access key>
WINMLSERVER_READY http://127.0.0.1:60713/v1
WINMLSERVER_MODEL model=qwen2.5-0.5b-instruct-q4_k_m composition=unified context=32768 declared_context=32768 max_output=2048 lanes=1 target=cpu reasoning_effort_mask=1
```

Give the agent the `WINMLSERVER_READY` address as its OpenAI-compatible base
URL and the key as its API key. For example,
[GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models)
reads its model provider from environment variables. In a second PowerShell
window:

```powershell
$env:COPILOT_OFFLINE = "true"
$env:COPILOT_PROVIDER_BASE_URL = "http://127.0.0.1:60713/v1"
$env:COPILOT_PROVIDER_API_KEY = [System.Net.NetworkCredential]::new("", (Read-Host "Access key" -AsSecureString)).Password
$env:COPILOT_PROVIDER_WIRE_API = "completions"
$env:COPILOT_PROVIDER_MAX_PROMPT_TOKENS = "30720"
$env:COPILOT_PROVIDER_MAX_OUTPUT_TOKENS = "2048"
$env:COPILOT_MODEL = "qwen2.5-0.5b-instruct-q4_k_m"
$env:NO_PROXY = "127.0.0.1"
copilot
```

Paste the key at the `Access key` prompt. `Read-Host -AsSecureString` hides it
as you paste and keeps it out of the PowerShell command history, which is saved
to a file. `NO_PROXY` makes the agent connect to the server directly, so the key
isn't sent to a proxy set in `HTTP_PROXY` or `HTTPS_PROXY`. The key works only
while this server runs, and each run prints a new one.

Set the prompt limit to the context window minus the output limit, so that
every request the agent sends fits the window the server checks. On the CPU, the
agent's first reply can take a minute or more, because the server first
processes the agent's instructions and tool definitions; add `-Device gpu` to
process them on a GPU. The sample model is
small enough to show the connection working, but agent tasks need a larger and
more capable model; use `-ModelPath` to serve one.

## Options

`run_server_executable.ps1` passes `-ModelId`, `-ContextTokens`, and `-Port` to
`WinMLServer.exe`, and passes `-Device` as the model's `--target`. Run the
executable without arguments to list all of its options:

```powershell
..\..\out\<platform>\Release\server-executable\WinMLServer.exe
```
