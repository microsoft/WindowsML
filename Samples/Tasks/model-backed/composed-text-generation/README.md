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

The model runs on the execution target that the app passes, the CPU by default
or the GPU with `--device gpu`.

On the GPU, the sample pins WebGPU with
`IWinMLOrtCompatibility::CreateExecutionTarget` (Python
`create_ort_execution_target`). WebGPU is a safe default because it works
across GPU vendors, and the Runtime acquires it when it builds the composition
if it isn't installed. An unpinned GPU target uses only a provider that is
already installed, so on a PC without one it fails with
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` (`0x80070032`).

`--ep` (`-Ep` in the run script) pins another provider on the selected device,
for example `--ep trtrtx` for TensorRT for RTX on an NVIDIA RTX GPU. It takes a
provider's catalog name, such as `NvTensorRTRTXExecutionProvider`, or one of the
short names `webgpu`, `trtrtx`, `nvtensorrtrtx`, `openvino`, `qnn`, `vitisai`
and `migraphx`. Not every provider runs every model, so if a provider can't run
this one, use the WebGPU default.

```powershell
..\..\run_composed_text_generation.ps1
..\..\run_composed_text_generation.ps1 -Device gpu
..\..\run_composed_text_generation.ps1 -Device gpu -Ep trtrtx
python.exe .\main.py ..\..\models\llm\task_model.onnx ..\..\models\llm\tokenizer.json
```

See [Models-backed Tasks](../README.md) for package and model requirements.
