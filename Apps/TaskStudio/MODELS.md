<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Models tested with Task Studio

Task Studio runs models through the Models-backed Task APIs on the CGC backend,
and Windows ML decides which models it can run. This page lists the models
tested in this app with the Windows ML version that
[`Samples\Directory.Packages.props`](../../Samples/Directory.Packages.props)
pins, on an NVIDIA GPU, with the [smoke tests](README.md#smoke-tests) and in the
app. Other models can work too. A model that Windows ML doesn't support fails
to load with `ERROR_NOT_SUPPORTED` (0x80070032) rather than running
incorrectly.

The models are published by third parties, not by Microsoft. Review each
publisher's license and terms before you use a model.

## Which files to pick

| Format | Tabs | Pick |
| --- | --- | --- |
| Checkpoint folder | Chat, Completion, Speech, Decide | The folder with `config.json`, the safetensors weights, and the tokenizer files as published. |
| GGUF file | Chat, Completion | The single `.gguf` file. Leave the tokenizer empty to use the tokenizer stored in the file. |

## Chat and Completion

### Checkpoint folders

| Model | Hugging Face | Notes |
| --- | --- | --- |
| Qwen2.5-0.5B-Instruct | [Qwen/Qwen2.5-0.5B-Instruct](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct) | |
| Qwen3-0.6B | [Qwen/Qwen3-0.6B](https://huggingface.co/Qwen/Qwen3-0.6B) | Prompt lookup speculative decoding |
| Qwen3.5-0.8B | [Qwen/Qwen3.5-0.8B](https://huggingface.co/Qwen/Qwen3.5-0.8B) | Text only. Draft layers (MTP) speculative decoding |
| Llama-3.2-1B-Instruct | [unsloth/Llama-3.2-1B-Instruct](https://huggingface.co/unsloth/Llama-3.2-1B-Instruct) | |
| Gemma-3-1B-it | [unsloth/gemma-3-1b-it](https://huggingface.co/unsloth/gemma-3-1b-it) | See [Known issues](#known-issues) |
| Qwen3.5-9B | [Qwen/Qwen3.5-9B](https://huggingface.co/Qwen/Qwen3.5-9B) | Text only. Draft layers (MTP) speculative decoding |
| Qwen3.8-27B NVFP4 | [nvidia/Qwen3.8-27B-NVFP4](https://huggingface.co/nvidia/Qwen3.8-27B-NVFP4) | Text only. Needs a GPU driver that runs NVFP4, see [Known issues](#known-issues). Draft layers (MTP) or a DFlash 2 drafter, see [SPECULATIVE-DECODING.md](SPECULATIVE-DECODING.md) |

### GGUF files

Every file below loads with the tokenizer stored in it.

| Model | Hugging Face repository | Files |
| --- | --- | --- |
| Qwen2.5-0.5B-Instruct | [Qwen/Qwen2.5-0.5B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF) | `qwen2.5-0.5b-instruct-fp16.gguf`, `qwen2.5-0.5b-instruct-q8_0.gguf`, `qwen2.5-0.5b-instruct-q4_k_m.gguf` |
| Qwen3-0.6B | [unsloth/Qwen3-0.6B-GGUF](https://huggingface.co/unsloth/Qwen3-0.6B-GGUF) | `Qwen3-0.6B-BF16.gguf`, `Qwen3-0.6B-Q4_0.gguf` |
| Qwen3.5-0.8B | [unsloth/Qwen3.5-0.8B-GGUF](https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF) | `Qwen3.5-0.8B-Q8_0.gguf`, `Qwen3.5-0.8B-Q4_0.gguf` |
| Llama-3.2-1B-Instruct | [bartowski/Llama-3.2-1B-Instruct-GGUF](https://huggingface.co/bartowski/Llama-3.2-1B-Instruct-GGUF) | `Llama-3.2-1B-Instruct-Q4_0.gguf` |
| Gemma-3-1B-it | [ggml-org/gemma-3-1b-it-GGUF](https://huggingface.co/ggml-org/gemma-3-1b-it-GGUF) | `gemma-3-1b-it-f16.gguf` |

## Speech

| Model | Hugging Face | Tested |
| --- | --- | --- |
| Whisper tiny | [openai/whisper-tiny](https://huggingface.co/openai/whisper-tiny) | Recorded and live |
| Nemotron 3.5 ASR streaming 0.6B | [nvidia/nemotron-3.5-asr-streaming-0.6b](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b) | Recorded and live |

## Decide

| Model | Hugging Face | Files |
| --- | --- | --- |
| Laya | [convaiinnovations/laya](https://huggingface.co/convaiinnovations/laya) | The root checkpoint: `config.json`, `rl_agent_config.json`, `encoder\config.json`, `model.safetensors`, and the `tokenizer` folder |

## Known issues

- **NVFP4 and FP8 checkpoints.** Checkpoints quantized with NVIDIA ModelOpt,
  such as [nvidia/Qwen3.8-27B-NVFP4](https://huggingface.co/nvidia/Qwen3.8-27B-NVFP4),
  need a GPU driver that runs NVFP4 or FP8 itself. With a driver that doesn't,
  creating the sessions fails with `ERROR_NOT_SUPPORTED`. On an NVIDIA RTX
  Spark, driver 621.24 runs nvidia/Qwen3.8-27B-NVFP4 and driver 621.01 doesn't.
- **Companion draft models.** Windows ML takes a DFlash 2 draft model as the
  companion drafter. A DFlash 1 draft model, such as
  [z-lab/Qwen3.5-9B-DFlash](https://huggingface.co/z-lab/Qwen3.5-9B-DFlash),
  fails session creation with `ERROR_NOT_SUPPORTED`.
- **Seeded sampling.** A fixed seed repeats the random draws, but a run can
  still pick a different token at a near tie when the GPU computes slightly
  different scores. With Gemma-3-1B in Completion, about one run in six
  differed from the previous run after the first few tokens.
- **Error details.** When Windows ML refuses a model, the app shows only the
  system text for the error code, such as "The request is not supported." The
  entries above list the causes seen while testing.

## How the tokenizer works

The model works on token IDs, not text. The tokenizer turns your prompt into
IDs and the generated IDs back into text, and its chat template wraps each
conversation turn in the special tokens the model was trained on. The tokenizer
must match the model exactly. A tokenizer from another model family produces
garbage or fails.

**Checkpoint folders.** The tokenizer is two files that the publisher ships
beside the weights, `tokenizer.json` (vocabulary, merges and pre-tokenizer
rules) and `tokenizer_config.json` (special tokens and the chat template). When
you pick a folder, Task Studio fills the Tokenizer field with that folder's
`tokenizer_config.json`, and the Runtime reads `tokenizer.json` from the same
folder.

**GGUF files.** A GGUF file stores its tokenizer in its metadata
(`tokenizer.ggml.*` and `tokenizer.chat_template`). Leave the Tokenizer field
empty, and the Runtime reads the tokenizer from the file. If the Runtime can't
read the tokenizer that a file declares, loading fails. Set the Tokenizer field
to the `tokenizer_config.json` of the same model's checkpoint, with
`tokenizer.json` beside it. Quantization doesn't change the tokenizer, so one
checkpoint's tokenizer serves every GGUF file of that model.

## Download

Use the [Hugging Face CLI](https://huggingface.co/docs/huggingface_hub/guides/cli):

```powershell
pip install -U "huggingface_hub[cli]"

# A checkpoint folder (pick the folder in the app)
hf download Qwen/Qwen3-0.6B --local-dir C:\models\Qwen3-0.6B

# One GGUF file (pick the file in the app)
hf download bartowski/Llama-3.2-1B-Instruct-GGUF Llama-3.2-1B-Instruct-Q4_0.gguf --local-dir C:\models
```

Gated repositories need `hf auth login` and accepting the license on Hugging
Face first.
