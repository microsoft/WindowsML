<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Tutorial 3: language models

The language samples export one source model, Qwen2.5-0.5B-Instruct, into two
ONNX representations:

```text
model.onnx                         unified decoder
emb.onnx -> decoder.onnx -> head.onnx   split pipeline
```

This teaches two topologies in order:

1. a unified one-stage Runtime loop for ONNX Runtime or llama.cpp;
2. the split ONNX Runtime pipeline.

## 1. Export the model

```powershell
.\check_artifacts.ps1 -Sample hello-language-model
```

If files are missing, the script prints the commands that acquire them. Review
the publisher's terms, run the commands, then run the check again. See
[Models and artifacts](../artifacts.md) for details.

The first export creates a local Python environment and downloads the source
model. Allow several GB of disk space for the source cache and generated
artifacts.

## 2. Build the language projects

```powershell
.\build.ps1 -Configuration Release -PackageOnly `
  -Sample hello-language-model

.\build.ps1 -Configuration Release -PackageOnly `
  -Sample llm-chat
```

These builds stage the Runtime payload needed for the sample's supported
artifact paths.

## 3. Run the minimal sample

```powershell
.\run_hello_language_model.ps1 `
  -Prompt "Name three primary colors."
```

The reply names three colors.

Read:

- `language\hello-language-model\main.cpp`;
- `shared\language_model_loader.h`.

The loader creates the target, loads the model, builds one stage, resolves the
tokenizer, and owns the bind/run/sample/decode loop.

## 4. Run chat with a unified model

```powershell
.\run_llm_chat.ps1 `
  -Mode unified `
  -Device cpu `
  -MaxTokens 64 `
  -Prompt "Name three primary colors."
```

Unified mode demonstrates:

- model-owned chat formatting;
- streamed fragments;
- caller-owned conversation history;
- Runtime sequence state;
- stop reason, prompt/generated token counts, first-token latency, and
  throughput.

Omit `-Prompt` for interactive chat. Use `new` to clear history and `quit` to
exit.

Trace the construction in `shared\language_model_loader.h`:

```text
LoadModelFromFile
 -> CreatePipelineBuilder
 -> AddModelStage
 -> Build
 -> tokenizer
 -> state position/capacity
 -> token tensor binding and Pipeline::Run
 -> greedy sampling and incremental decode
```

This works with unified ONNX and GGUF and uses the same Runtime objects in both
cases.

## 5. Run the split ONNX model

```powershell
.\run_llm_chat.ps1 `
  -Backend ort `
  -Mode split `
  -Device cpu `
  -MaxTokens 64 `
  -Prompt "Name three primary colors."
```

Trace:

```text
token ID
 -> embedding stage
 -> decoder stage + Runtime-managed KV state
 -> language-model head
 -> logits
 -> greedy sampling
 -> tokenizer decoder
```

Read:

- `language\llm-chat\winml_llm_pipeline.h`;
- `language\llm-chat\winml_llm_session.h`.

The Python sample uses the same split topology:

```powershell
python.exe .\language\llm-chat\main.py `
  --backend ort --mode split --device cpu `
  --prompt "Name three primary colors."
```

## 6. Raw prompting

```powershell
.\run_hello_language_model.ps1 `
  -Prompt "Spiders have eight legs, and insects have" `
  -Raw
```

Raw mode skips the chat template. Use it for base models or to distinguish a
model-execution problem from a conversation-template problem.

## 7. Why both ONNX artifacts exist

- The unified model provides the smallest generation example.
- Unified chat uses the same Runtime construction.
- The split model exposes stage placement, bindings, state, and composition.
- The exporter verifies the unified and split outputs against the source model.

## Next

Continue to [Speech to LanguageModel](04-speech-to-language.md).
