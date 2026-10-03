<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# LLM chat

This sample adds conversation state, generation limits, streaming options, and a
split ONNX pipeline to the minimal [Hello LanguageModel](../hello-language-model/)
sample.

## Prepare and build

From `Samples\Runtime`:

```powershell
.\check_artifacts.ps1 -Sample llm-chat
.\check_artifacts.ps1 -Sample llm-chat-gguf
```

`llm-chat` covers the exported ONNX models, and `llm-chat-gguf` covers the GGUF
model. The default export uses the ungated `Qwen/Qwen2.5-0.5B-Instruct` model.
The first export creates an isolated Python environment under
`%LOCALAPPDATA%\WinMLSampleTools` and downloads the model. Keeping the
environment outside a deep repository worktree avoids Windows package-install
path limits. Install a trusted version of `uv` first if it is not already
available:

```powershell
winget install --id astral-sh.uv
```

`uv` downloads packages from PyPI and doesn't read pip's configuration. On a
network that mirrors PyPI, set `UV_DEFAULT_INDEX` to the mirror before you run
the export. When Smart App Control or App Control for Business is on, it can
block the export environment's launchers and unsigned package files; export the
model on another machine and copy it into `models\llm`.

The exporter writes:

- `model.onnx` for unified mode;
- `emb.onnx`, `decoder.onnx`, and `head.onnx` for split mode;
- tokenizer files used by both modes.

Use `language\llm-chat\export_model.ps1` directly to select another compatible
dense decoder or a different context capacity. See
[Models and artifacts](../../../../docs/Runtime/artifacts.md).

Build the sample:

```powershell
.\build.ps1 -Configuration Release -PackageOnly -Sample llm-chat
```

## Unified model

`unified` mode, the default, loads one ONNX/ORT or GGUF model and builds a
one-stage Runtime pipeline. `LoadModelFromFile` chooses the backend from the
artifact type: `.onnx` and `.ort` use ONNX Runtime, and `.gguf` uses llama.cpp.

```powershell
.\run_llm_chat.ps1 -Backend ort -Mode unified -Device cpu `
  -Prompt "Reply with one short greeting."

.\run_llm_chat.ps1 -Backend llama -Mode unified -Device cpu `
  -Prompt "Reply with one short greeting."
```

The unified flow is:

```text
LoadModelFromFile
  -> CreatePipelineBuilder
  -> AddModelStage
  -> Build
  -> create tokenizer
  -> bind token tensors and run
  -> sample logits and decode fragments
```

The sample manages conversation history, reset, streaming, and timing
statistics. Unified mode reports time to first token and throughput.

For a sharded GGUF model, keep all shards in the same directory and pass the
first shard as `-ModelPath`. Set `-ContextCapacity` to request a smaller sequence
capacity; the prompt plus `-MaxTokens` must fit within that capacity.

## Split ONNX model

`split` mode builds this three-stage ONNX topology:

```text
token IDs -> embedding -> decoder -> language-model head -> logits
                         ^
                         | persistent fixed-capacity state
```

```powershell
.\run_llm_chat.ps1 -Backend ort -Mode split -Device cpu `
  -Prompt "Reply with one short greeting."
```

The pipeline demonstrates stage connections, a stateful decoder, target
selection, tokenizer metadata, chat-template formatting, and an autoregressive
decode loop. The C++ and Python samples declare decoder state pairs by ordinal
before `Build`, bind token, position, and mask inputs for each decode step, then
read logits from the head stage. Split mode requires `emb.onnx`, `decoder.onnx`,
and `head.onnx` with a fixed KV-cache capacity.

## Chat options

Omit `-Prompt` from the PowerShell launcher (`--prompt` from the native
executable) for interactive chat. Type `new` to reset the pipeline execution
state and start a new conversation.

Use `-Raw` to skip the model's chat template and `-MaxTokens N` to bound a
response. To place the decoder on another available target, add the device and
provider options from [Devices and execution providers](../../../../docs/Runtime/providers.md).

See [Tutorial 3: language models](../../../../docs/Runtime/tutorials/03-language-models.md)
for the step-by-step progression.

## Python

[`main.py`](main.py) supports the same unified and split model layouts:

```powershell
python.exe .\language\llm-chat\main.py `
  --prompt "Reply with one short greeting."
```

`--mode unified` is the default. `--mode split` runs the three ONNX stages
(embedding, decoder, head) and binds tokens, sequence position, causal mask, and
state by ordinal; Runtime schemas provide the tensor shapes and data types. See
[Python samples](../../../../docs/Runtime/python-samples.md).

For GGUF, install the llama.cpp backend with
`python.exe -m pip install --pre windowsml-llama-core`. The wheel installs the
CPU llama.cpp backend; to run on a GPU, add a backend you build yourself. The
setup and cleanup commands are documented in
[Python payload install](../../../../docs/Runtime/gguf-models.md#python-payload-install),
and the GPU options in
[GPU backends](../../../../docs/Runtime/gguf-models.md#gpu-backends).
