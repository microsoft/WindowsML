<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Speculative decoding with Qwen3.8-27B in Task Studio

Speculative decoding makes Qwen3.8-27B decode faster. A small drafter proposes
several tokens at once, and Qwen3.8-27B checks them all in one step and keeps
only the tokens it would have chosen itself. With greedy decoding (temperature
0) the text stays the same, apart from a rare near tie between two tokens (see
[Troubleshooting](#troubleshooting)).

Task Studio drives this entirely through the Windows ML Task API. The app only
chooses the method and shows the statistics that the Runtime reports. See
[Speculative decoding](README.md#speculative-decoding) in the README for the
API calls.

## What you need

- A GPU driver that runs NVFP4 and FP8 weights. With a driver that doesn't,
  creating the sessions fails with `ERROR_NOT_SUPPORTED` (see
  [Known issues](MODELS.md#known-issues)).
- About 24 GB of free GPU memory. The model and the drafter took 20.5 GB on the
  PC in [Results](#results), and long prompts take more. Close other GPU-heavy
  apps first.
- About 24 GB of disk for the two checkpoints, and free system memory for the
  first load, which compiles the GPU programs. Later loads reuse them.
- Task Studio from its packaged zip, or built as the
  [README](README.md#build-and-run) describes, with the Windows ML version that
  this repository pins.

## 1. Download the model and its drafter

```powershell
pip install -U "huggingface_hub[cli]"
hf download nvidia/Qwen3.8-27B-NVFP4 --local-dir C:\models\Qwen3.8-27B-NVFP4
hf download incoai/Qwen3.8-27B-DFlash2 --local-dir C:\models\Qwen3.8-27B-DFlash2
```

| Checkpoint | What it is | Size |
| --- | --- | ---: |
| [nvidia/Qwen3.8-27B-NVFP4](https://huggingface.co/nvidia/Qwen3.8-27B-NVFP4) | The model, quantized with NVIDIA Model Optimizer (NVFP4 MLP and `lm_head`, FP8 attention projections, BF16 elsewhere), with its tokenizer | About 20 GB |
| [incoai/Qwen3.8-27B-DFlash2](https://huggingface.co/incoai/Qwen3.8-27B-DFlash2) | A DFlash 2 block-diffusion drafter trained for Qwen3.8-27B. It has 5 layers in BF16 and proposes up to 7 tokens per step. | About 3.6 GB |

The checkpoints are published by third parties, not by Microsoft. Review each
publisher's license and terms before you use them.

## 2. Load the model with the drafter

1. In the sidebar, choose the model folder `C:\models\Qwen3.8-27B-NVFP4`. The
   tokenizer field fills in with that folder's `tokenizer_config.json`.
2. Leave **Context window** at **Model default**.
3. Under **Speculative decoding**, choose **Drafter model**, then browse to the
   drafter folder `C:\models\Qwen3.8-27B-DFlash2`.
4. Select **Create task sessions**. The first load takes several minutes while
   the GPU programs compile, and later loads reuse them.

The **Connected model** card then shows the method, how many tokens it
proposes per step, and which methods the Runtime reports for this model. A
method that the Runtime reports as unsupported fails the load and lists the
supported methods. If the Runtime refuses to create the sessions, the error
suggests loading with speculative decoding off, which tells whether it refused
the model or the drafter.

### The other methods

| Option | With Qwen3.8-27B |
| --- | --- |
| **Drafter model** | Recommended. Uses the DFlash 2 drafter above. |
| **Draft layers** | Uses the multi-token prediction (MTP) layer that the model carries, with no extra download. |
| **Prompt lookup** | Not supported, because the model's recurrent state can't roll back, so the load is refused. |
| **Off** | Normal decoding. |

## 3. See the speedup

- **Completion tab.** Each reply's footer shows the decode rate, the drafted
  and accepted tokens with the acceptance rate, and the tokens per verify step.
  The Chat result doesn't report these statistics, so use Completion or Perf.
- **Perf tab, Speculative decoding: Off vs on.** The card runs the same greedy
  prompts with speculative decoding off and then with the loaded method, and
  compares the text.
  - **Prompts.** Keep **Short**, which runs three small prompts. **Coding**
    asks about this app's own C# source as a plain completion rather than a
    chat turn, and Qwen3.8-27B ends two of its three prompts without writing
    anything. **Prompts used** shows the exact text.
  - **Generate.** 128 to 32,768 tokens per prompt. The context window must hold
    the prompt plus these tokens.
  - **End of text.** Keep **Stop** for realistic numbers. **Ignore (full
    count)** generates the full count past the end of the answer, where text
    tends to repeat and inflates acceptance.
  - **Copy as Markdown** copies the table and the summary.

The comparison creates the sessions again for each method and resets the chat
conversation.

## Results

Measured with `TaskStudio.SmokeTests --spec` on an NVIDIA RTX Spark with GPU
driver 621.24 and Windows ML 2.9.31-experimental, greedy, up to 128 tokens for
each of the three short prompts, with the decode rate over all three prompts.
Each method was compared with its own Off run, so the Off rates differ a
little. The text with speculative decoding matched the text without it on
every prompt.

| Method | Off (tokens/s) | On (tokens/s) | Speedup | Drafts accepted (rewrite code, explain, count) | Tokens per verify step |
| --- | ---: | ---: | ---: | --- | --- |
| Drafter model (DFlash 2) | 14.3 | 49.3 | 3.44x | 75%, 21%, 100% | 6.25, 2.40, 7.94 |
| Draft layers (MTP) | 11.7 | 24.4 | 2.09x | 92%, 49%, 99% | 3.75, 2.44, 3.97 |

The gain depends on how predictable the text is. Code that repeats its input
and structured output gain the most, and free prose the least.

To run the same comparison from the command line, build `TaskStudio.SmokeTests`
as the README's [Smoke tests](README.md#smoke-tests) section shows, and run it
with each method.

```powershell
$smoke = '.\Apps\TaskStudio\SmokeTests\bin\ARM64\Debug\net8.0-windows10.0.19041.0\win-arm64\TaskStudio.SmokeTests.exe'
$model = 'C:\models\Qwen3.8-27B-NVFP4'
& $smoke --spec $model "$model\tokenizer_config.json" companion C:\models\Qwen3.8-27B-DFlash2
& $smoke --spec $model "$model\tokenizer_config.json" layers
```

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| The load fails with an out-of-memory error | Another process is using GPU memory or system memory. Close it and load again. |
| "The Runtime cannot create sessions for this model with this drafter on this GPU" | Load the model with speculative decoding off. If that fails too, the GPU driver likely doesn't run NVFP4 or FP8 weights. If it loads, the drafter is the cause. |
| "The Runtime cannot use this companion drafter with this model" | The drafter folder isn't a DFlash 2 drafter trained for this model. Choose the `Qwen3.8-27B-DFlash2` folder. |
| "Load the drafter the same way as the model" | The model is a folder, so the drafter must be a folder too. A `.gguf` drafter pairs only with a `.gguf` model. |
| No drafted or accepted numbers on a reply | Chat replies don't report them. Use Completion or the Perf tab. |
| The Off and on texts differ | A verify step evaluates several positions at once, which can pick differently when two tokens are almost tied. The Perf card reports how much of the text matched before they diverged. |

See [MODELS.md](MODELS.md) for the other models tested with Task Studio.
