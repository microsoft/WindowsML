<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# In-process server

This sample hosts the Windows ML Server inside an application. It loads a GGUF
model, builds a Text Generation lane for it, registers the lane with the
server, and starts the server on a free loopback port. It then starts one of
the [clients](../../client/README.md) with the server's address and access key,
waits for the client to exit, and stops the server.

The application prepares everything the server runs: the Runtime, the pipeline,
the tokenizer, the Task runtime, and the Text Generation configuration. The
server adds the HTTP endpoint, request validation, streaming, and admission. It
does not load models or choose hardware. See
[`IWinMLServer`](../../../../docs/api-reference/IWinMLServer.md) and
[`IWinMLTextGenerationTask`](../../../../docs/api-reference/IWinMLTextGenerationTask.md).

```powershell
..\..\check_artifacts.ps1 -Sample server-in-process
..\..\build.ps1 -Sample in-process
..\..\build.ps1 -Sample client-cpp
..\..\run_in_process_server.ps1 -Client cpp
```

Use `-Client csharp` or `-Client python` to run another client, `-ModelPath` to
serve another GGUF model, `-ModelId` to change the id clients send,
`-ContextTokens` to request a context window, and `-Device gpu` to run the
model on a GPU.

```text
Model: qwen2.5-0.5b-instruct-q4_k_m
  Device: CPU
  Context window: 8192 tokens (the model declares 32768)
  Maximum output: 2048 tokens
  Tool calls: supported
Serving at http://127.0.0.1:51209/v1

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

Client exited with code 0. Server stopped.
```

The lines through `Serving at` come from the host; the rest come from the
client. When GPU backends are deployed, llama.cpp first prints the devices it
finds. The [client README](../../client/README.md) explains the client's
output.

## Lanes

A lane is one Task runtime, one tokenizer, and one Text Generation
configuration that the server runs requests on, one at a time. The sample
builds a one-stage pipeline on the CPU or, with `--device gpu`, on a GPU
execution target, creates the tokenizer from the GGUF
file, which carries the model's tokenizer and chat template, and creates a
unified configuration whose stage takes token ids, returns logits, and holds the
sequence state.

`RegisterModel` takes the lane under a model id, which is the name clients send
in the `model` field. Registration takes over the configuration; from then on
only the server runs that pipeline. A model can have several lanes to serve
several requests at once, but each lane needs its own pipeline. Requests beyond
the active lanes wait in the model's queue, within the limits in
`WINML_SERVER_OPTIONS`.

## Capabilities

The registration tells clients what the model supports. The server guarantees
well-formed tool calls and JSON schema output only when the backend can
constrain which tokens the model generates, so the sample asks a short-lived
Text Generation session whether it can, before the server takes over the
pipeline. The session uses up the configuration it is created from, so the
probe gets its own configuration.

The server checks each request against the context window the pipeline was
built with. Without `-ContextTokens`, the Runtime picks a default window that
can be smaller than the window the model declares; `-ContextTokens` requests a
specific size. The sample also publishes the declared window through
`IWinMLServerModelContext`, and the model list reports both.

## Access key

`Start` opens the loopback listeners on a free port and creates a new access
key. The sample reads the port with `GetPort` and the key with
`winml::server::GetAccessKey`, then starts the client with both in the client's
environment. The key never appears on a command line, where other users on the
computer could read it, and the sample clears it from its own environment as
soon as the client has started.

`Stop` cancels any request still running, waits for the lanes to finish, and
discards the key. A server starts only once: to serve again, an application
creates a new server with newly prepared lanes, and that server creates a new
key.

## Ctrl+C

Ctrl+C reaches every process attached to the console. While the client runs,
the host ignores Ctrl+C, the client exits, and the host stops the server and
returns the client's exit code.
