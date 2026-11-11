<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Composed text generation

Bring your own model: the app loads the models it built and describes how they
form a text generation model. The configuration composes them in each session.

1. Load the exported decoder with `IWinMLRuntime::LoadModelFromFile` and its
   tokenizer with `WinMLCreateTokenizerFromFile`.
2. Call `SetTokenizer`. A composition has no tokenizer files, so the app always
   sets it.
3. Describe the model with `WINML_TEXT_GENERATION_COMPOSED_MODEL` (Python
   `TextGenerationComposedModel`) and call `SetComposition`. This sample uses
   the `UNIFIED` kind: one stage takes the token IDs at input 0, produces logits
   at output 0, and feeds its key/value state from output 1 back to input 1.
4. Set the logical capacity that the exported model declares, create the task
   with the same tokenizer and stream a reply.

The model is the exported Qwen decoder (`task_model.onnx`) that the
[hand-wired text generation sample](../../language/text-generation/) binds with
`SetPipeline`, `SetTokenInput`, `SetOutput` and `SetStateOwnerStage`. Comparing
the two shows what the composition description replaces. The other kinds,
`EMBEDDING_DECODER` and `PREFILL_DECODE`, describe an embedding, decoder and
head, or separate prefill and decode models, in the same way.

The model runs on the backend its load selected, on the execution target the
app passes: the CPU by default, or the GPU with `--device gpu`.

```powershell
..\..\run_composed_text_generation.ps1
..\..\run_composed_text_generation.ps1 -Device gpu
python.exe .\main.py ..\..\models\llm\task_model.onnx ..\..\models\llm\tokenizer.json
```

See [Models-backed Tasks](../README.md) for package and model requirements.
