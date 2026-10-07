<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Tutorial 7: run a GGUF language model

This tutorial reuses the same Runtime application flow as the
unified ONNX sample while changing the artifact and deployed backend payload.

## 1. Acquire the pinned model

```powershell
.\check_artifacts.ps1 -Sample llm-chat-gguf
```

If files are missing, the script prints the commands that acquire them. Review
the publisher's terms, run the commands, then run the check again. See
[Models and artifacts](../artifacts.md) for details.

## 2. Build with the llama payload

```powershell
.\build.ps1 -Sample hello-language-model `
  -Configuration Release -PackageOnly
```

Language projects reference the
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` package, which copies the
llama.cpp runtime with its CPU backend beside the sample executable. See
[C++ payload deployment](../gguf-models.md#c-payload-deployment). No separate
build flag is required for these language samples.

## 3. Run the shared starter

```powershell
.\run_hello_language_model.ps1 -Backend llama -Device cpu
```

Look for:

```text
Artifact: GGUF / llama.cpp
Resolved target: CPU
```

## 4. Run unified chat

```powershell
.\build.ps1 -Sample llm-chat -PackageOnly
.\run_llm_chat.ps1 -Backend llama -Mode unified -Device cpu `
  -Prompt "Name three primary colors."
```

The sample performs model load, stage creation, pipeline build, tokenizer
resolution, state inspection, token binding, pipeline execution, greedy
sampling, and incremental decode. The `.gguf` extension selects llama.cpp; no
backend-specific application API is used.

The `split` mode is not a GGUF mode; it teaches the exported three-stage ONNX
topology through ONNX Runtime.

## 5. Run a large or sharded GGUF

Large artifacts may advertise a context larger than you need. Set
`-ContextCapacity` to request the K/V window used by the sample. For a sharded
GGUF, keep every shard in one directory and pass the first shard:

```powershell
.\run_llm_chat.ps1 `
  -Backend llama `
  -Mode unified `
  -Device cpu `
  -ModelPath D:\models\model-00001-of-00003.gguf `
  -ContextCapacity 64 `
  -MaxTokens 16 `
  -Prompt "Name three primary colors."
```

The prompt and generated tokens must fit within `-ContextCapacity`. `-Device gpu`
needs a GPU backend that you build yourself, such as the CUDA backend for NVIDIA
GPUs or Vulkan. See [GPU backends](../gguf-models.md#gpu-backends) and the
[llama.cpp backend tool](../../../Tools/llama/README.md).

## 6. Compose ONNX Runtime speech and GGUF language

```powershell
.\build.ps1 -Sample speech-to-language-model -PackageOnly
.\run_speech_to_language_model.ps1 -Backend llama
```

The output identifies both sides of the composition before producing
the transcript-grounded response `brown`.

See [GGUF language models](../gguf-models.md) for Python setup, optional
modules, shards, and context capacity.
