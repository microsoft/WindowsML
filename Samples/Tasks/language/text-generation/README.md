<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Text generation

This sample builds a Text Generation Task from Runtime objects created by the
application, then reads the Task stream to completion.

The application creates the Runtime and model pipeline first, then passes those
objects into the typed Task configuration. Generation returns a pull stream:
each `ReadNext` call yields text that can be displayed immediately. After
completion, the result supplies the final text, token counts, finish reason,
timing, and error status. See
[Task and Runtime lifecycle](../../../../docs/Tasks/task-lifecycle.md) and
[`IWinMLTextGenerationTask`](../../../../docs/api-reference/IWinMLTextGenerationTask.md).

The ONNX path uses the exported Qwen Task graph in `models\llm\task_model.onnx`
and its tokenizer. `hybrid-ort` uses `task_model.onnx` for prefill and
`task_decode.onnx` for one-token decode. The GGUF path loads a `.gguf` model and
uses the tokenizer embedded in that file. All paths use the same Task session,
cancellation, streaming result, token-count, finish-reason, and context-capacity
interfaces.

```powershell
..\..\build.ps1 -Sample text-generation -PackageOnly
..\..\run_text_generation.ps1 -Backend llama
```

Use `-Backend ort` to run the exported Qwen ONNX Task model instead. The ORT
path also accepts a compatible pre-optimized `.ort` model. The Python entry
point uses `--backend auto` by default, which selects llama for a GGUF model and
ORT otherwise.

## Decoding

By default, generation uses sampling with `temperature=0.7`, `top-p=0.9`, and
`repetition-penalty=1.1`, because greedy selection makes a small model repeat
itself once a continuation runs past a sentence or two. The sample prints the
decoding mode it used. The Python entry point decodes greedily.

```powershell
# Deterministic and repeatable.
..\..\run_text_generation.ps1 -Backend ort -Greedy

# Override any individual control.
..\..\run_text_generation.ps1 -Backend ort `
  -Temperature 0.8 -TopP 0.95 -TopK 40 -RepetitionPenalty 1.15 -Seed 42
```

`-Chat` sends the prompt as a user turn in the model's own chat template, which
the tokenizer applies, and starts generation from those tokens. An instruct
model ends its own turn with an end-of-sequence token only when prompted that
way; a raw prompt continues until it reaches the token limit or the model's
sequence capacity. The Python entry point takes `--chat`. For a multi-turn
conversation, see the [Chat completion](../chat-completion/) sample.

```powershell
..\..\run_text_generation.ps1 -Backend ort -Chat -Prompt "Name three primary colors."
```

Example output (timing varies by machine):

```text
Decoding: sampled (temperature=0.70 top-p=0.90 repetition-penalty=1.10) by default; pass -Greedy for deterministic output
Generated text: Three primary colors are red, yellow, and blue. These colors are fundamental in creating all other hues and combinations of colors.
Token counts: prompt=34 generated=25
Finished because: end-of-sequence token
Timing: first=124.80ms total=981.81ms
Context capacity: effective=128 declared=unknown
```

## Speculative decoding

With a GGUF model, the Task can propose several draft tokens, verify them in one
multi-position model step, and keep each draft token only when it equals the
token the configured sampling selects at that position. Sampling is unchanged,
so speculation changes throughput rather than output quality. A multi-position
step can round differently from single-position steps, so a token whose
probability is nearly tied with another can occasionally be selected
differently than in sequential decoding.

| `-Speculative` | Task method | Draft source |
| --- | --- | --- |
| `model` | `WINML_TEXT_GENERATION_SPECULATIVE_METHOD_MODEL` | The model's own draft predictor: multi-token prediction (MTP, also called NextN) layers in the GGUF file, or a companion block draft model passed with `-DraftModel`, such as a DFlash, DFlash2, or DSpark model trained for the target. |
| `draft-model` | `WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL` | A smaller GGUF model, passed with `-DraftModel`, that shares the target's tokenizer. The draft must use an attention cache; a recurrent or hybrid draft proposes nothing. |
| `prompt-lookup` | `WINML_TEXT_GENERATION_SPECULATIVE_METHOD_PROMPT_LOOKUP` | Earlier occurrences of the latest tokens in the prompt and output. It needs no extra model and helps most on repetitive text. |

`-DraftTokens` (1 to 16, default 3) sets the most draft tokens per step; the
Task can lower it to what the model supports. The sample prints the decode rate
and the verification statistics, so runs with and without speculation can be
compared. Speculation pays off when verifying several positions costs about as
much as verifying one, which is typical on a GPU. On a CPU the verification
cost grows with the number of positions, so speculation can be slower than
sequential decoding; compare both on the target device. `-Device gpu` requires
a matching
[optional llama.cpp GPU module](../../../../docs/Runtime/gguf-models.md#optional-gpu-modules).

These Hugging Face repositories publish GGUF models that work with each method.
Review each publisher's terms before downloading.

| `-Speculative` | Target model | Draft model |
| --- | --- | --- |
| `model` (MTP layers) | `unsloth/Qwen3.5-9B-GGUF` | None |
| `model` (DFlash2) | `ggml-org/Qwen3.8-27B-GGUF` | `z-lab/Qwen3.8-27B-DFlash2-GGUF` |
| `model` (DSpark) | `LiquidAI/LFM2.5-1.2B-Instruct-GGUF` | `LiquidAI/LFM2.5-1.2B-Instruct-DSpark-GGUF` |
| `draft-model` | `bartowski/Meta-Llama-3.1-8B-Instruct-GGUF` | `bartowski/Llama-3.2-1B-Instruct-GGUF` |
| `prompt-lookup` | Any GGUF model | None |

```powershell
# Multi-token prediction layers built into the model.
..\..\run_text_generation.ps1 -Backend llama -Device gpu -Greedy `
  -ModelPath D:\models\Qwen3.5-9B-Q4_K_M.gguf `
  -Prompt "Explain speculative decoding." -MaxNewTokens 256 `
  -Speculative model

# A companion block draft model.
..\..\run_text_generation.ps1 -Backend llama -Device gpu -Greedy `
  -ModelPath D:\models\LFM2.5-1.2B-Instruct-Q8_0.gguf `
  -Prompt "Explain speculative decoding." -MaxNewTokens 256 `
  -Speculative model -DraftModel D:\models\LFM2.5-1.2B-Instruct-DSpark-Q8_0.gguf `
  -DraftTokens 8

# A separate, smaller draft model.
..\..\run_text_generation.ps1 -Backend llama -Device gpu -Greedy `
  -ModelPath D:\models\Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf `
  -Prompt "Explain speculative decoding." -MaxNewTokens 256 `
  -Speculative draft-model -DraftModel D:\models\Llama-3.2-1B-Instruct-Q4_K_M.gguf `
  -DraftTokens 5
```

The statistics from the first example (rates vary by machine):

```text
Decode rate: 62.82 tokens/s
Speculative decoding: model draft predictor steps=94 drafted=281 accepted=161 acceptance=57.3%
```

The sample fails with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` (`0x80070032`)
when the selected method cannot run with the model. Building the pipeline fails
for a `-DraftModel` block draft model that was not trained for the target, and
creating the session fails for `model` with a GGUF that has no multi-token
prediction layers. A request that decodes sequentially, such as a prompt that
contains media, reports the method as `none`. The sample supports speculative
decoding for GGUF models on the llama.cpp backend and reports an error when
`-Speculative` is used with another backend. The Python entry point takes
`--device`, `--speculative`, `--draft-tokens`, and `--draft-model`. See
[Speculative decoding](../../../../docs/api-reference/IWinMLTextGenerationTask.md#speculative-decoding)
in the API reference.

## Hybrid prefill/decode backends

`hybrid-ort` takes a *model directory* rather than a single model file and
builds a `PREFILL_DECODE` Text Generation Task from two independent pipelines:

- prefill runs `task_model.onnx` on ORT and consumes the entire prompt in one
  dynamic-length step, and
- decode runs `task_decode.onnx` one token at a time on ORT.

```powershell
..\..\build.ps1 -Sample text-generation -PackageOnly
..\..\run_text_generation.ps1 -Backend hybrid-ort `
  -Prompt "The sky is often" -MaxNewTokens 16
```

The native sample prints the prefill and decode target kind; for a D3D12 target,
it also prints adapter and command-queue details.

Three rules govern the caller-managed state handoff, and
`shared/hybrid_text_generation_task.h` documents each at its use site:

1. State outputs are only observable when the stage is configured with
   `SetCallerOwnsStateTensors(TRUE)`; otherwise the state output is not returned
   to the caller.
2. Binding any output disables backend output allocation for every output on
   that stage, so the logits output must be bound once the state output is.
3. The logits output must be bound *after*
   `IWinMLPipelineBuilder::Build`; binding it before `Build` causes execution to
   fail for this caller-owned-state configuration. The state tensors remain part
   of the pre-`Build` stateful-stage setup.

State moves from prefill to decode with `IWinMLMutableTensor::CopyFrom`, which
works across execution targets (CPU tensor to D3D12 tensor). The Task
itself transfers no state between the two pipelines.
