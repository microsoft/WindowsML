<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLServer

`IWinMLServer` serves Text Generation lanes through an OpenAI-compatible Chat
Completions endpoint on the loopback interface. `WinMLServer.dll` implements it,
and `winml/server/WinMLServer.h` declares it together with
`IWinMLServerModelContext`, `IWinMLServerModelFormatting`,
`IWinMLServerAccessKey`, and `IWinMLServerFactory`.

The application prepares every lane from Runtime and Task objects it owns. The
server validates requests, formats each conversation with the lane's tokenizer,
runs generation, streams the result, and limits how much work clients can queue.
It does not load models, select execution targets, download files, run tools,
or keep conversation history between requests.

The [Server samples](../../Samples/Server/README.md) host the server in-process and
call it from C++, C#, and Python clients.

## Activation

Use the C++ helpers in `winml/server/WinMLServer.hpp`:

```cpp
WINML_SERVER_OPTIONS options = winml::server::MakeDefaultOptions();
options.maximumActiveRequests = 1;

Microsoft::WRL::ComPtr<IWinMLServer> server;
THROW_IF_FAILED(winml::server::Create(options, server));
```

`winml::server::Create` loads `WinMLServer.dll` with
`LOAD_LIBRARY_SEARCH_DEFAULT_DIRS` and keeps it loaded for the rest of the
process. C callers use `WinMLMakeDefaultServerOptions` and `WinMLLoadServer`
from `winml/server/WinMLServerActivation.h`. On success, `WinMLLoadServer`
returns the module it loaded. The server's code lives in that module, so release
the server before calling `FreeLibrary` on it.

`WinMLServer.dll` has no exported factory function. Both helpers call its
`DllGetClassObject` with `WINML_SERVER_ACTIVATION_CLASS_ID` to get
`IWinMLServerFactory`, then call `CreateServer`. `CreateServer` returns
`E_INVALIDARG` for options it can't accept.

| Build system | Setting | Effect |
|---|---|---|
| MSBuild | `WinMLCopyServerRuntime=true` | Deploys `WinMLServer.dll` and the Task runtime, and compiles the Server and Task interface identifiers into the project. |
| MSBuild | `WinMLCopyServerHost=true` | Also deploys `WinMLServer.exe`. |
| CMake | `WindowsML::Server` | Compiles the interface identifiers and adds `WinMLServer.dll` to `WINML_RUNTIME_FILES`. `WINML_DEPLOY_SERVER_HOST` adds `WinMLServer.exe`. |

The server is available for x64 and ARM64. ARM64EC isn't supported.

## Options

`WINML_SERVER_OPTIONS` sets the server's limits. Start from
`MakeDefaultOptions` or `WinMLMakeDefaultServerOptions` and change only the
fields you need.

| Field | Default | Meaning |
|---|---|---|
| `port` | `0` | Loopback port. `0` selects a free port; read it with `GetPort` after `Start`. |
| `maximumModels` | `8` | Models that can be registered. |
| `maximumActiveRequests` | `4` | Lanes across all models, which is also the number of requests that can run at the same time. |
| `maximumQueuedRequestsPerModel` | `8` | Requests that can wait for one model's lanes. `0` disables queueing. |
| `queueWaitTimeoutMilliseconds` | `30000` | How long a queued request waits for a lane. |
| `readTimeoutMilliseconds`, `writeTimeoutMilliseconds` | `30000` | Socket read and write timeouts, up to 300 and 30 seconds. |
| `keepAliveMaximumRequests` | `16` | Requests one connection can send. |
| `maximumRequestBodyBytes` | 1 MiB | Request body size. |
| `maximumMessages` | `128` | Messages in one request. |
| `maximumTools` | `128` | Tools in one request. |
| `maximumToolCallsPerMessage` | `64` | Tool calls in one message. |
| `maximumSchemaBytes` | 512 KiB | Combined size of a request's tool parameter schemas and response format schema. |
| `maximumCompletionTokens` | `4096` | Output tokens for one request, for every model. |
| `maximumResponseBytes` | 4 MiB | Size of one response, streamed or not. |
| `maximumSseEventBytes` | 256 KiB | Size of one server-sent event. |
| `enableDiagnostics` | `FALSE` | Allows requests to ask for diagnostics. |
| `maximumImagesPerRequest` | `4` | Images in one request. |
| `maximumImageBytes` | 512 KiB | Size of one image after base64 decoding. |
| `maximumImagePixels` | 16 Mi | Pixels in one decoded image. |

`maximumQueuedRequestsPerModel` and the image limits accept zero; every other
limit and timeout must be greater than zero. A model that accepts images
requires all three image limits. `maximumResponseBytes` and
`maximumSseEventBytes` must leave room for a final error event, so a truncated
stream always reports the truncation. Smaller values return `E_INVALIDARG`.

## `RegisterModel`

```cpp
HRESULT RegisterModel(const WINML_SERVER_MODEL_REGISTRATION* registration);
```

Registers one model id with one or more lanes. A lane runs one request at a
time, so a model with two lanes can serve two requests at once. Call
`RegisterModel` before `Start`; afterward it returns `E_NOT_VALID_STATE`.

```cpp
WINML_SERVER_LANE lane{};
lane.taskRuntime = tasks.Get();
lane.tokenizer = tokenizer.Get();
lane.configuration = configuration.Get();

WINML_SERVER_MODEL_REGISTRATION registration{};
registration.modelId = L"my-model";
registration.ownedBy = L"my-app";
registration.capabilities.contextWindowTokens = 8192;
registration.capabilities.maximumOutputTokens = 2048;
registration.capabilities.capabilityFlags = WINML_SERVER_MODEL_CAPABILITY_FLAGS_TOOL_CALLS;
registration.capabilities.reasoningEffortMask = WINML_SERVER_REASONING_EFFORT_FLAGS_DISABLED;
registration.laneCount = 1;
registration.lanes = &lane;
THROW_IF_FAILED(server->RegisterModel(&registration));
```

### Lanes

`WINML_SERVER_LANE` keeps the objects one lane needs together:

| Field | Meaning |
|---|---|
| `taskRuntime` | The `IWinMLTasks` object that created the configuration. |
| `tokenizer` | The model's tokenizer. It must support structured conversation formatting. |
| `configuration` | A validated `IWinMLTextGenerationConfiguration` for the lane's pipeline. |
| `mediaEncoder` | Optional `IWinMLMediaEncoder` for image input. Required on every lane of a model that sets `VISION`; null otherwise. |
| `generationEnforcementPolicy` | How the lane enforces tool calls and structured output. The zero value is the default. |

The tokenizer must be the configuration's tokenizer, and the task runtime and the
configuration must use the same Runtime. Successful registration takes over each
configuration, as
[`IWinMLTextGenerationTask`](IWinMLTextGenerationTask.md) describes for session
creation: the original can't be changed or used again. Registration that fails
leaves the configurations unchanged. To use speculative decoding, select it on
the configuration before `Validate`, as described in
[Speculative decoding](IWinMLTextGenerationTask.md#speculative-decoding).

Each lane needs its own pipeline, sequence state, output tensors, and media
encoder. Read-only inputs can be shared. Don't run, reset, or rebind a
registered pipeline; the server keeps the lane objects until `Stop` completes or
the server is released.

A media encoder takes images as batch-one NHWC RGB tensors with `UINT8`,
`FLOAT16`, or `FLOAT32` elements. Fixed heights and widths are honored, and
wildcard dimensions keep the uploaded size. The server doesn't infer layouts,
crop images, or apply model-specific normalization; registration rejects
encoders it can't feed.

### Capabilities

`WINML_SERVER_MODEL_CAPABILITIES` describes what the model's requests can use.
The server publishes it in the model list and checks every request against it.

| Field | Meaning |
|---|---|
| `contextWindowTokens` | The context window in force. The formatted prompt plus the requested output must fit in it. |
| `maximumOutputTokens` | The model's output limit. It can't be zero or larger than the context window. |
| `capabilityFlags` | `WINML_SERVER_MODEL_CAPABILITY_FLAGS` values. |
| `reasoningEffortMask` | The `reasoning_effort` values requests can send, as `WINML_SERVER_REASONING_EFFORT_FLAGS` values. |

| Capability flag | Requests it allows |
|---|---|
| `TOOL_CALLS` (`0x1`) | `tools` and `tool_choice`. |
| `PARALLEL_TOOL_CALLS` (`0x2`) | `parallel_tool_calls: true`. Requires `TOOL_CALLS`. |
| `JSON_SCHEMA` (`0x4`) | `response_format` of type `json_object` or `json_schema`. |
| `REASONING` (`0x8`) | `reasoning_effort` values other than `none`. `reasoningEffortMask` can't include them without this flag. |
| `CONSTRAINED_LOGITS` (`0x10`) | None. It tells clients that the lanes constrain generation. |
| `VISION` (`0x20`) | `image_url` content parts. |

| Reasoning effort flag | `reasoning_effort` value |
|---|---|
| `DISABLED` (`0x1`) | `none`, which turns reasoning off. |
| `MINIMAL` (`0x2`) | `minimal` |
| `LOW` (`0x4`) | `low` |
| `MEDIUM` (`0x8`) | `medium` |
| `HIGH` (`0x10`) | `high` |
| `XHIGH` (`0x20`) | `xhigh` |
| `MAX` (`0x40`) | `max` |

Set only the efforts the model's chat template implements. A reasoning model
can set no efforts and keep its default behavior.

### Generation enforcement

With the default `WINML_SERVER_GENERATION_ENFORCEMENT_POLICY_CONVERSATION_CONSTRAINTS`,
the lane constrains generation for tool calls and structured output when the
request needs it, so a required tool call or a JSON schema response always
parses. `WINML_SERVER_GENERATION_ENFORCEMENT_POLICY_NATURAL` lets the model
generate freely. Natural lanes accept tools with `tool_choice` omitted, `auto`,
or `none`, and return HTTP 422 for a required or named `tool_choice`, strict
function definitions, JSON response formats, and images. Every lane of one model
must use the same policy.

### Errors

| Result | Cause |
|---|---|
| `E_POINTER` | `registration`, `modelId`, `ownedBy`, `lanes`, or a lane's task runtime, tokenizer, or configuration is null. |
| `E_INVALIDARG` | An empty model id, no lanes, more lanes than `maximumActiveRequests`, a zero context window or output limit, an output limit larger than the context window, unknown or inconsistent flags, `VISION` without image limits, or mixed enforcement policies. |
| `HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)` | The model id is already registered. |
| `HRESULT_FROM_WIN32(ERROR_TOO_MANY_NAMES)` | `maximumModels` models are already registered. |
| `HRESULT_FROM_WIN32(ERROR_TOO_MANY_CMDS)` | The lanes of the models already registered plus this model's lanes would exceed `maximumActiveRequests`. |
| `HRESULT_FROM_WIN32(ERROR_INVALID_DATA)` | A lane's objects don't belong together, or a lane shares a writable resource with another lane. |
| `E_NOINTERFACE` | The tokenizer doesn't support structured conversation formatting. |
| `E_NOT_VALID_STATE` | The server has started, or a configuration was already used. |

## `IWinMLServerModelContext`

```cpp
HRESULT SetModelContext(LPCWSTR modelId, const WINML_SERVER_MODEL_CONTEXT* context);
```

Publishes the context window the model file declares, next to the window in
force. The model list reports the declared window as
`declared_context_window_tokens`, or `null` when it is unknown.
`WINML_SERVER_CONTEXT_DISCLOSURE_ARTIFACT_DECLARED` requires a nonzero
`declaredContextWindowTokens`, and `WINML_SERVER_CONTEXT_DISCLOSURE_UNKNOWN`
requires zero; anything else returns `E_INVALIDARG`.

Query the interface from the server, and call it after `RegisterModel` for the
same model and before `Start`. Afterward it returns `E_NOT_VALID_STATE`, so the
model list can't change while clients use it. An unregistered model id returns
`HRESULT_FROM_WIN32(ERROR_NOT_FOUND)`.

## `IWinMLServerModelFormatting`

```cpp
HRESULT SetModelFormatterMode(LPCWSTR modelId, WINML_CONVERSATION_FORMATTER_MODE formatterMode);
```

Selects the conversation formatter for every lane and request of one model,
including the token count the server checks against the context window.
`WINML_CONVERSATION_FORMATTER_MODE_MODEL_DEFAULT` is the default, and
`WINML_CONVERSATION_FORMATTER_MODE_PORTABLE_JINJA_V1` is the other supported
mode; other values return `E_INVALIDARG`. Requests can't change the selection.
Confirm that the selected mode works with the model's tokenizer, chat template,
and advertised capabilities.

The call rules match `SetModelContext`: after `RegisterModel`, before `Start`,
and `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` for an unregistered model id.

## `Start`, `Stop`, and `GetState`

```cpp
HRESULT Start();
HRESULT Stop();
HRESULT GetState(WINML_SERVER_STATE* state);
```

`Start` opens listeners on `127.0.0.1` and, when the computer has IPv6 loopback,
`[::1]`, on the same port. It creates a new access key and moves the server from
`WINML_SERVER_STATE_CREATED` to `WINML_SERVER_STATE_RUNNING`. A server starts
only once: `Start` returns `E_NOT_VALID_STATE` in any other state, and also when
no model is registered. If `Start` fails after the lanes have taken over their
configurations, the server moves to `WINML_SERVER_STATE_STOPPED`; create a new
server with new configurations to try again.

`Stop` cancels active requests, joins the lane threads, discards the access key,
and moves the server to `WINML_SERVER_STATE_STOPPED`. It returns when that work
is done, and calling it again has no effect. `Stop` on a server that never
started also moves it to `WINML_SERVER_STATE_STOPPED`. If a listener stops
accepting connections while the server is running, the server closes the other
listener and reports `WINML_SERVER_STATE_STOPPING`; call `Stop` to finish.

## `GetPort`

```cpp
HRESULT GetPort(UINT16* port);
```

Returns the listening port while the server is running. In any other state it
returns `E_NOT_VALID_STATE` and writes zero. The base URL is
`http://127.0.0.1:<port>/v1`.

## `IWinMLServerAccessKey`

```cpp
HRESULT GetAccessKey(UINT32 keyCapacity, CHAR* key, UINT32* keyLength);
```

Every request must send the access key as `Authorization: Bearer <key>`. `Start`
creates a new random key, 64 lowercase hexadecimal characters, and `Stop`
discards it. Applications can't choose the key.

Call `GetAccessKey` with zero capacity and a null buffer to get the size. That
call returns `HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)` and sets
`keyLength` to the length including the terminating null. A second call with a
buffer of that size copies the key. On failure the buffer is zero-filled. When
the server isn't running, the method returns `E_NOT_VALID_STATE` and writes zero
to `keyLength`.

`winml::server::GetAccessKey(IWinMLServer*, std::string&)` does both calls.
Give the key only to clients that run as the same user, for example in a child
process's environment. Keep it out of command lines, URLs, and logs. Clients
should connect to the server directly, because a proxy would receive the key
with every request.

## HTTP endpoint

| Route | Purpose |
|---|---|
| `GET /v1/models` | Lists the registered models. |
| `POST /v1/chat/completions` | Runs a chat completion, streamed or not. |

The server checks every request before it reads the body:

- The connection must come from `127.0.0.1` or `::1`.
- The `Host` header must be `127.0.0.1:<port>`, `localhost:<port>`, or
  `[::1]:<port>` for the server's port.
- Requests with an `Origin` header are rejected, so web pages can't call the
  server.
- The `Authorization` header must carry the current key. Otherwise the server
  returns HTTP 401 with `WWW-Authenticate: Bearer` and the error code
  `invalid_api_key`.

Chat completion requests must also send `Content-Type: application/json`.

### Model list

Each entry has the OpenAI fields `id`, `object`, `created`, and `owned_by`, and
a `winml` object:

| Field | Value |
|---|---|
| `context_window_tokens` | `contextWindowTokens` |
| `declared_context_window_tokens` | The declared window from `SetModelContext`, or `null`. |
| `maximum_output_tokens` | `maximumOutputTokens` |
| `capability_flags` | `capabilityFlags` |
| `reasoning_effort_mask` | `reasoningEffortMask` |
| `lanes` | The model's lane count. |

### Chat completion requests

The server reads these request fields:

| Field | Notes |
|---|---|
| `model` | A registered model id. |
| `messages` | Messages with a `role` and `content`. Assistant messages can carry `tool_calls` and `reasoning_content`; `tool` messages carry `tool_call_id`. User messages can carry `image_url` parts with base64 `data:` URLs for PNG, JPEG, GIF, BMP, or WebP images. |
| `max_completion_tokens` or `max_tokens` | Send at most one. The default and the maximum are the smaller of `maximumCompletionTokens` and the model's `maximumOutputTokens`. |
| `stream`, `stream_options.include_usage` | Server-sent events, with an optional usage chunk. |
| `tools`, `tool_choice`, `parallel_tool_calls` | Function tools. `tool_choice` is `auto`, `none`, `required`, or a named function. |
| `response_format` | `text`, `json_object`, or `json_schema`. |
| `reasoning_effort` | One of the values in the model's `reasoning_effort_mask`. |
| `temperature`, `top_p`, `min_p`, `top_k`, `presence_penalty`, `frequency_penalty`, `seed` | Sampling options. |

`n` must be 1 when present, and `stop` isn't supported. Requests that use either
one return HTTP 422 `unsupported_parameter`.

The response is an OpenAI chat completion. `finish_reason` is `stop`,
`tool_calls`, `length`, `cancelled`, or `error`. Reasoning text is returned in
`reasoning_content`. `usage.prompt_tokens` counts the formatted prompt, and
`usage.prompt_tokens_details.cached_tokens` reports how many of those tokens a
lane reused from an earlier request instead of processing them again.

A streamed response sends a chunk with the assistant role, chunks with
`content`, `reasoning_content`, or `tool_calls` deltas, a chunk with the finish
reason, a usage chunk with no choices when `include_usage` is set, and then
`data: [DONE]`. If generation fails after the stream starts, the server sends an
error event and then `data: [DONE]`.

### Errors

Errors use the OpenAI error shape:
`{"error": {"message", "type", "param", "code"}}`. `type` is `server_error` for
5xx statuses and `invalid_request_error` otherwise.

| Status | Codes |
|---|---|
| 400 | `invalid_json`, `invalid_type`, `invalid_value`, `invalid_messages`, `invalid_message`, `invalid_model`, `invalid_tool_choice`, `conflicting_parameters`, `invalid_host`, `invalid_request`, `context_length_exceeded` |
| 401 | `invalid_api_key` |
| 403 | `loopback_required`, `browser_origin_rejected` |
| 404 | `model_not_found`, `not_found` |
| 405 | `method_not_allowed` |
| 408 | `request_cancelled` |
| 413 | `request_too_large`, `limit_exceeded` |
| 415 | `unsupported_media_type` |
| 422 | `unsupported_parameter`, `unsupported_capability`, `unsupported_response_format`, `unsupported_tool_type`, `unsupported_content_part`, `unsupported_image_url` |
| 429 | `queue_full`, `queue_timeout`, with `Retry-After: 1` |
| 500 | `generation_failed`, `response_too_large`, `server_error` |
| 503 | `server_stopping`, `model_unavailable`, `resource_exhausted` |

### Diagnostics

A chat completion that reaches a lane returns an `X-WinML-Request-Id` header.
When `enableDiagnostics` is `TRUE` and a request sends
`X-WinML-Include-Diagnostics: true`, the response also has a `winml` object with
the request id, the formatted prompt token count, the generated token ids, and
timings for queueing, the first token, and the whole generation. When a request
decodes speculatively, the object also has `speculative_decoding`, with the
`WINML_TEXT_GENERATION_SPECULATIVE_METHOD` value and the verification step,
drafted token, and accepted draft token counts.

## Ownership and threading

Every method can be called from any thread. `RegisterModel`, `Start`, and `Stop`
are serialized with each other. `Stop` can run while requests are active.

Each lane creates its Text Generation and Chat Completion sessions on its own
thread and runs one request at a time. Requests for a model wait in its queue
until a lane is free, up to `maximumQueuedRequestsPerModel` requests and
`queueWaitTimeoutMilliseconds`.

For preparing lane objects, see [IWinMLTasks](IWinMLTasks.md),
[IWinMLTextGenerationTask](IWinMLTextGenerationTask.md),
[IWinMLChatCompletionTask](IWinMLChatCompletionTask.md), and
[IWinMLStructuredConversationFormatter](IWinMLStructuredConversationFormatter.md).
