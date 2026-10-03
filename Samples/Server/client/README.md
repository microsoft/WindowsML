<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Server clients

These clients call the Windows ML Server's OpenAI-compatible endpoint the way an
application or a coding agent does. Each one lists the served models, completes
a chat, streams a reply, and completes a tool call. All three print the same
output and work with either host.

| Client | HTTP and JSON | Build or install |
| --- | --- | --- |
| [C++](cpp/main.cpp) | WinHTTP and `Windows.Data.Json`, so every request and response is visible in the code. | `..\build.ps1 -Sample client-cpp` |
| [C#](csharp/Program.cs) | The [OpenAI library for .NET](https://www.nuget.org/packages/OpenAI). | `..\build.ps1 -Sample client-csharp`, with the .NET 8 SDK |
| [Python](python/main.py) | The [OpenAI Python library](https://pypi.org/project/openai/). | `python.exe -m pip install openai`, with Python 3.10 or later |

```powershell
..\run_in_process_server.ps1 -Client csharp
..\run_server_executable.ps1 -Client python
```

```text
Server: http://127.0.0.1:51209/v1

Models
  qwen2.5-0.5b-instruct-q4_k_m: context window 8192 tokens, output limit 2048 tokens, tool calls supported

Chat completion
  User: Name three primary colors.
  Assistant: Red, blue, and yellow.
  Usage: 27 prompt tokens (0 reused), 7 completion tokens

Streaming
  User: Count from one to five in words.
  Assistant: Sure, here are the words for counting from one to five:

One
Two
Three
Four
Five
  Usage: 37 prompt tokens (5 reused), 22 completion tokens

Tool calling
  User: What is 12 times 34? Use the multiply tool.
  Tool call: multiply({"a": 12, "b": 34}) returned 408
  Assistant: The result of multiplying 12 by 34 is 408.
  Usage: 241 prompt tokens (219 reused), 17 completion tokens
```

## Endpoint and key

The host starts the client with two environment variables:

| Variable | Value |
| --- | --- |
| `WINMLSERVER_BASE_URL` | The server's base URL, for example `http://127.0.0.1:51209/v1`. |
| `WINMLSERVER_ACCESS_KEY` | The access key the server created when it started. |

The run scripts set them only in the client's environment. Each client sends
the key as a bearer token in the `Authorization` header of every request; the
OpenAI libraries do that with the key they are given as their API key. The
server answers a request without a valid key with HTTP 401, and the clients
print the server's error message.

The clients connect to the server directly, bypassing any proxy configured on
the computer, because a proxy would receive the key with every request. The
OpenAI libraries use the computer's proxy settings by default, so the C# and
Python clients turn them off.

To run a client against a server you started yourself, for example with
`..\run_server_executable.ps1` and no `-Client`, set the variables from the
server's output in the client's console, and paste the key at the
`Access key` prompt:

```powershell
$env:WINMLSERVER_BASE_URL = "http://127.0.0.1:51209/v1"
$env:WINMLSERVER_ACCESS_KEY = [System.Net.NetworkCredential]::new("", (Read-Host "Access key" -AsSecureString)).Password
..\out\<platform>\Release\server-client-cpp\server-client-cpp.exe
```

`Read-Host -AsSecureString` keeps the key off the screen and out of the
PowerShell command history.

Each client uses the first model the server lists. Pass `--model <id>` to
choose another one.

## Model list

`GET /v1/models` returns the OpenAI model list. Each model also has a `winml`
object that describes the limits the server checks requests against:

| Field | Meaning |
| --- | --- |
| `context_window_tokens` | The context window in force. The prompt and the reply must fit in it. |
| `declared_context_window_tokens` | The window the model file declares, or `null` when it is unknown. |
| `maximum_output_tokens` | The model's output limit. A request can't ask for more with `max_completion_tokens` or `max_tokens`. |
| `capability_flags` | `0x1` tool calls, `0x2` parallel tool calls, `0x4` JSON schema output, `0x8` reasoning, `0x10` constrained generation, `0x20` image input. |
| `reasoning_effort_mask` | The `reasoning_effort` values the model accepts: `0x1` none, `0x2` minimal, `0x4` low, `0x8` medium, `0x10` high, `0x20` xhigh, `0x40` max. |
| `lanes` | How many of the model's requests can run at the same time. |

The clients ask for at most 256 output tokens, or the model's output limit if
it is lower, and run the tool call only when the model supports tool calls. The
OpenAI library for .NET doesn't expose the `winml` object, so the C# client
reads the raw response.

## Chat completion and streaming

The chat completion sends a system message and a user message, then prints the
reply and the token usage. The streaming request also sets `stream` and
`stream_options.include_usage` to `true`. The server replies with server-sent
events: chunks with text to append, then a chunk with no choices that carries
the usage, then `data: [DONE]`. The OpenAI library for .NET asks for the usage
chunk on its own.

`usage.prompt_tokens` counts the whole formatted prompt. When a prompt starts
with tokens the server has already processed, the server reuses them, and
`usage.prompt_tokens_details.cached_tokens` reports how many. The second request
of the tool call reuses most of the first.

## Tool calling

A tool call takes two requests. The first offers a `multiply` function and sets
`tool_choice` to `required`. The server constrains what the model generates, so
the call's arguments always match the function's JSON schema. The client runs
the function, then appends the model's message and a `tool` message with the
result to the conversation. The second request sets `tool_choice` to `none`,
and the model answers with the result.

An agent sends `tool_choice` as `auto` instead, and repeats until the model
answers without calling a tool. The server never runs a tool itself.
