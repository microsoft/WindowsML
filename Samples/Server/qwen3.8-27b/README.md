<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Qwen3.8-27B with DFlash2 on the Windows ML Server

This sample serves [nvidia/Qwen3.8-27B-NVFP4](https://huggingface.co/nvidia/Qwen3.8-27B-NVFP4)
through `WinMLServer.exe` on one 32 GB GPU, with
[incoai/Qwen3.8-27B-DFlash2](https://huggingface.co/incoai/Qwen3.8-27B-DFlash2)
as its speculative-decoding drafter. Coding agents and other OpenAI-compatible
clients connect to it like any other Windows ML Server.

The Text Generation Task runs the model on the GPU (`--composition model`), not
through llama.cpp. The sample runs the same `WinMLServer.exe` as the
[server executable sample](../host/executable/README.md), and holds the key
and value (KV) cache in one of two types:

| KV cache | Context window | Use it for |
| --- | --- | --- |
| `float16` (default) | 135,168 tokens | The default. Prompts up to 131,072 tokens. |
| `int8` | 262,144 tokens | The model's full context. It needs the calibrated scales in [`kv_cache_scales.safetensors`](kv_cache_scales.safetensors). |

Both windows are the largest that fit in 32 GB of dedicated GPU memory with
this build. They were measured on an NVIDIA GeForce RTX 5090.

> [!IMPORTANT]
> This sample needs `Microsoft.Windows.AI.MachineLearning` `2.9.42-experimental`
> or later, the version [`Directory.Packages.props`](../../Directory.Packages.props)
> pins.

## Checkpoints

These are the exact revisions the settings below were tuned and tested with.
Both are published under the Apache 2.0 license.

| Folder | Hugging Face repository | Revision | Size |
| --- | --- | --- | --- |
| `models\Qwen3.8-27B-NVFP4` | [nvidia/Qwen3.8-27B-NVFP4](https://huggingface.co/nvidia/Qwen3.8-27B-NVFP4) | `482ca0f3832238542f8f5295dde86b5f22711d80` | 21.9 GB |
| `models\Qwen3.8-27B-DFlash2` | [incoai/Qwen3.8-27B-DFlash2](https://huggingface.co/incoai/Qwen3.8-27B-DFlash2) | `015e795645c74b1a0eeef3b570031fb62e769bc5` | 3.8 GB |

The target model is NVIDIA's ModelOpt mixed-precision build of Qwen3.8-27B:
NVFP4 MLP and `lm_head` weights, FP8 attention and linear-attention
projections, and BF16 for the rest. DFlash2 is a five-layer block-diffusion
drafter with no embedding or LM head of its own; it reads the target's.

Download them with the [Hugging Face CLI](https://huggingface.co/docs/huggingface_hub/guides/cli)
from `Samples\Server`. The `models` folder is gitignored.

```powershell
hf download nvidia/Qwen3.8-27B-NVFP4 --revision 482ca0f3832238542f8f5295dde86b5f22711d80 --local-dir models\Qwen3.8-27B-NVFP4
hf download incoai/Qwen3.8-27B-DFlash2 --revision 015e795645c74b1a0eeef3b570031fb62e769bc5 --local-dir models\Qwen3.8-27B-DFlash2
```

Then check that every file matches the size and SHA-256 the samples expect.
`check_artifacts.ps1` lists each file, and prints a download command for any
that is missing or different:

```powershell
.\check_artifacts.ps1 -Sample server-qwen3.8-27b
```

The run script runs the same check before it starts the server. The model id
clients send is `Qwen3.8-27B-NVFP4`; pass `-ModelId` to change it.

## INT8 KV cache calibration

An `int8` cache stores each key and value channel as an 8-bit integer times a
per-channel scale. The scales come from `kv_cache_scales.safetensors` in the
model folder, and the server fails the launch with `ERROR_NOT_SUPPORTED`
without it. NVIDIA's checkpoint doesn't include one, so this sample does:

| File | Bytes | SHA-256 |
| --- | --- | --- |
| [`kv_cache_scales.safetensors`](kv_cache_scales.safetensors) | 134,608 | `A062DD0D450412956AC8D156C409D89221C976D789DC436E9E815AA2E159911C` |

Each scale is the channel's largest absolute key or value over a calibration
corpus, divided by 127, with values given 1.5 times more range than keys. The
corpus is 9 prompts, 259,543 tokens in all: repository source code in several
programming languages, coding problems, reasoning traces, model cards, and
multilingual text. With these scales, `int8` matched `float16` on HumanEval+
and MBPP+ (541 problems, greedy decoding): the two differ on 3 problems.

Copy the file next to the model. The run script never writes into the model
folder itself:

```powershell
Copy-Item qwen3.8-27b\kv_cache_scales.safetensors models\Qwen3.8-27B-NVFP4\
```

The `float16` cache ignores the file, so both types can serve the same folder.

## Build

From `Samples\Server`:

```powershell
.\build.ps1 -Sample executable
.\build.ps1 -Sample client-cpp
```

This builds the [server executable sample](../host/executable/README.md), which
deploys `WinMLServer.exe` with the Runtime, Task, and Server DLLs and the
Direct3D 12 Agility SDK to `out\<platform>\Release\server-executable`, from the
`Microsoft.Windows.AI.MachineLearning` version that
[`Directory.Packages.props`](../../Directory.Packages.props) pins. To use a
later package, pass `-RuntimePackageVersion <version>`; to restore from a local
copy, see [`localpackages`](../localpackages/README.md).

## Run

```powershell
.\run_qwen3.8_27b_server.ps1                  # float16 KV cache, 135,168-token window
.\run_qwen3.8_27b_server.ps1 -KvCache int8    # int8 KV cache, 262,144-token window
.\run_qwen3.8_27b_server.ps1 -KvCache int8 -Client cpp
```

The first start of each KV type compiles the model's GPU programs into
`models\compiled-programs\qwen3.8-27b-<type>`, which takes several minutes.
Later starts reuse them and skip compilation. A new GPU driver compiles them
again.

Without `-Client`, the server runs until you press Ctrl+C:

```text
WINMLSERVER_ACCESS_KEY <access key>
WINMLSERVER_READY http://127.0.0.1:57761/v1
WINMLSERVER_MODEL model=Qwen3.8-27B-NVFP4 composition=model context=262144 declared_context=262144 max_output=4096 lanes=1 target=gpu reasoning_effort_mask=45
WINMLSERVER_KV_CACHE_TYPE model=Qwen3.8-27B-NVFP4 requested=int8 held=int8
WINMLSERVER_SPECULATIVE model=Qwen3.8-27B-NVFP4 method=model draft_tokens=7 draft_model=...\models\Qwen3.8-27B-DFlash2
```

With `-Client`, the script runs one of the [clients](../client/README.md) and
stops the server when it exits:

```text
Models
  Qwen3.8-27B-NVFP4: context window 262144 tokens, output limit 4096 tokens, tool calls supported

Chat completion
  User: Name three primary colors.
  Assistant: Red, blue, and yellow.
  Usage: 67 prompt tokens, 59 completion tokens

Streaming
  User: Count from one to five in words.
  Assistant: One, two, three, four, five.
  Usage: 60 prompt tokens, 50 completion tokens

Tool calling
  User: What is 12 times 34? Use the multiply tool.
  Tool call: multiply({"a":12,"b":34}) returned 408
  Assistant: 12 times 34 is **408**.
  Usage: 413 prompt tokens, 23 completion tokens
```

Pass `-ModelPath` and `-DraftModelPath` to serve checkpoints from another
folder, and `-ContextTokens` to request a smaller window on a GPU with less
memory. `Get-Help .\run_qwen3.8_27b_server.ps1 -Full` lists every option.

## Settings

The script resolves these options into a recipe, adds `naturalToolCalls`, and
starts the server with it:

| Option | Value | Why |
| --- | --- | --- |
| `--composition model` | | The Text Generation Task loads the safetensors folder itself and runs its own speculative decoding. |
| `--target gpu` | | The Task runs the model on the GPU. |
| `--kv-cache-type` | `float16` or `int8` | The KV cache type. The server prints the type it holds in `WINMLSERVER_KV_CACHE_TYPE`. |
| `--context-tokens` | `135168` or `262144` | The whole cache is reserved at launch. A window that doesn't fit in dedicated GPU memory fails the launch with `ERROR_NOT_ENOUGH_MEMORY` instead of spilling to shared memory. |
| `--max-output-tokens` | `4096` | Output limit per request. The server reserves it within the window, so prompts can use the window minus 4,096 tokens. |
| `--speculative model` | | Use the model's companion drafter, named by `--draft-model`. |
| `--draft-model` | `models\Qwen3.8-27B-DFlash2` | The DFlash2 drafter. Its BF16 weights are quantized to NVFP4 at load. |
| `--draft-tokens` | `7` | Tokens drafted per verification step; DFlash2 drafts blocks of 8. |
| `--concurrency` | `1` | One request at a time. A `model` composition serves one lane. |
| `--compiled-program-directory` | `models\compiled-programs\qwen3.8-27b-<type>` | Stores the compiled GPU programs so later starts skip compilation. Use one directory per KV type. |
| `--max-request-body-bytes` | `16777216` | 16 MiB, the maximum. With the 1 MiB default, the longest prompts fail with HTTP 413. |
| `--reasoning-efforts` | `none,low,medium,xhigh` | The `reasoning_effort` values Qwen3.8's chat template implements. Without the option, the server accepts only `none`. Recipes don't carry it, so it stays on the command line. We suggest sending `none`; see [Turn thinking off](#turn-thinking-off). |
| `--diagnostics` | | Requests with `X-WinML-Include-Diagnostics: true` get a `winml` object in the response with timings and speculative-decoding counts. |
| `naturalToolCalls` (recipe only) | `true` | Serves tool calls; see below. |

The recipe the script writes to `models\qwen3.8-27b-<type>.recipe.json` looks
like this, and you can run `WinMLServer.exe` with it directly:

```json
{
  "models": [
    {
      "compiledProgramDirectory": "...\\models\\compiled-programs\\qwen3.8-27b-int8",
      "composition": "model",
      "contextTokens": 262144,
      "id": "Qwen3.8-27B-NVFP4",
      "keyValueCacheType": "int8",
      "lanes": 1,
      "maximumOutputTokens": 4096,
      "model": { "debugName": "decoder", "path": "...\\models\\Qwen3.8-27B-NVFP4" },
      "ownedBy": "winml",
      "speculative": { "draftModel": "...\\models\\Qwen3.8-27B-DFlash2", "draftTokens": 7, "method": "model" },
      "target": { "kind": "gpu", "preference": "performance" },
      "tokenizer": { "source": "model" },
      "naturalToolCalls": true
    }
  ],
  "schemaVersion": 1,
  "server": {
    "diagnostics": true,
    "maximumImageBytes": 524288,
    "maximumRequestBodyBytes": 16777216,
    "port": 0,
    "queueCapacity": 8,
    "queueWaitTimeoutMilliseconds": 300000
  }
}
```

```powershell
.\out\x64\Release\server-executable\WinMLServer.exe --recipe models\qwen3.8-27b-int8.recipe.json --reasoning-efforts none,low,medium,xhigh
```

### Tool calls

The server constrains tool calls with the tokenizer's exact vocabulary, which a
GGUF file provides and a `tokenizer.json` folder doesn't. Without
`naturalToolCalls`, this model registers without tool calls and the server
rejects requests with `tools`. With it, the chat template describes the tools,
the model writes the calls, and the server parses them. Requests can send
`tool_choice` as `auto` or `none`, which is what coding agents send;
`required`, a named tool, strict tools, and JSON response formats return
HTTP 422. `capability_flags` in the model list is `0x0b`: tool calls, parallel
tool calls, and reasoning, without constrained generation (`0x10`).

## Serve a coding agent

Start the server without `-Client`, then point the agent at the
`WINMLSERVER_READY` address with the access key. Set the agent's prompt limit
to the context window minus the output limit, so every request fits. For
[GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models)
and the `int8` cache, in a second PowerShell window:

```powershell
$env:COPILOT_OFFLINE = "true"
$env:COPILOT_PROVIDER_BASE_URL = "http://127.0.0.1:57761/v1"
$env:COPILOT_PROVIDER_API_KEY = [System.Net.NetworkCredential]::new("", (Read-Host "Access key" -AsSecureString)).Password
$env:COPILOT_PROVIDER_WIRE_API = "completions"
$env:COPILOT_PROVIDER_MAX_PROMPT_TOKENS = "258048"   # 131072 with the float16 cache
$env:COPILOT_PROVIDER_MAX_OUTPUT_TOKENS = "4096"
$env:COPILOT_MODEL = "Qwen3.8-27B-NVFP4"
$env:NO_PROXY = "127.0.0.1"
copilot --reasoning-effort none
```

The [server executable README](../host/executable/README.md#serve-a-coding-agent)
explains each setting and how the access key is kept private.

### Turn thinking off

We suggest running Qwen3.8-27B with thinking off. With thinking on, the model
writes its reasoning before every reply, and in an agent loop that adds a long
stretch of output to each step. A request that omits `reasoning_effort` gets
the model's default thinking, so turn it off explicitly with
`"reasoning_effort": "none"`:

```json
{
  "model": "Qwen3.8-27B-NVFP4",
  "reasoning_effort": "none",
  "messages": [{ "role": "user", "content": "..." }]
}
```

If your agent lets you choose the reasoning effort, set it to `none`; Copilot
CLI takes `--reasoning-effort none`, as above. To think through a hard problem,
send `low`, `medium`, or `xhigh` on that request instead.

## Diagnostics

Send `X-WinML-Include-Diagnostics: true` with a request to get a `winml` object
in the response with the server's view of it:

| Field | Meaning |
| --- | --- |
| `formatted_prompt_tokens` | Tokens in the prompt after the chat template is applied. |
| `queue_and_prepare_ms` | Time from receiving the request to starting the Task. |
| `task_ttft_ms` | Time from starting the Task to its first token. |
| `task_total_ms` | Time the Task took for the whole reply. |
| `speculative_decoding` | `verification_steps`, `drafted_tokens`, and `accepted_draft_tokens` for the request. |

The server also writes the speculative-decoding counts for every request to
standard error as `WINMLSERVER_SPECULATIVE_REQUEST`. Generated tokens divided by
`verification_steps` is the number of tokens each step produced; it varies with
the prompt and the output.

## Troubleshooting

| Message | Cause |
| --- | --- |
| `WinMLServer.exe ... has no --kv-cache-type ...` | The sample was built with an older package. Rebuild with `2.9.42-experimental` or later. |
| `ERROR_NOT_ENOUGH_MEMORY` at launch | The cache for `--context-tokens` doesn't fit in free dedicated GPU memory. Close other GPU applications or pass a smaller `-ContextTokens`. |
| `ERROR_NOT_SUPPORTED` with `-KvCache int8` | `kv_cache_scales.safetensors` isn't in the model folder. |
| `WINMLSERVER_MEMORY_SPILL` | Part of the model ended up in shared GPU memory. Lower `-ContextTokens`. |
| HTTP 413 | The request body exceeds `--max-request-body-bytes`. |
| HTTP 422 on a tool request | The request sent a `tool_choice` other than `auto` or `none`; see [Tool calls](#tool-calls). |
