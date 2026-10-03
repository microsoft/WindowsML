<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Hello LanguageModel

This sample loads one language model, applies the model's chat template, and
streams one greedy response. `LoadModelFromFile` chooses the backend from the
artifact type: ONNX/ORT models use ONNX Runtime, and GGUF models use llama.cpp.

The ONNX artifact prepared by [LLM chat](../llm-chat/) is `model.onnx`, with
token IDs as input, logits as output, and sequence state managed by Runtime.
Tokenizer files are stored beside the model. GGUF models carry or reference their
own tokenizer metadata.

`shared\language_model_loader.h` shows the shared C++ flow: create a target, load
the model, build a one-stage pipeline, resolve tokenizer metadata, submit token
tensors, read logits, select the next token, and decode text fragments.

## Prepare, build, and run

```powershell
cd Samples\Runtime
.\check_artifacts.ps1 -Sample hello-language-model
.\build.ps1 -Configuration Release -PackageOnly `
  -Sample hello-language-model

.\run_hello_language_model.ps1
```

Run the GGUF model:

```powershell
.\check_artifacts.ps1 -Sample llm-chat-gguf
.\build.ps1 -Sample hello-language-model -PackageOnly
.\run_hello_language_model.ps1 -Backend llama -Device cpu
```

Pass a model and prompt when needed:

```powershell
.\run_hello_language_model.ps1 `
  -ModelPath .\models\llm\model.onnx `
  -Prompt "Explain why the sky is blue."
```

Add `-Raw` (`--raw` for the executable) to skip the model's chat template and
submit the prompt text directly. This is useful for base models and for separating tokenizer-template
errors from model execution errors.

For the progression from this one-stage sample to the split ONNX pipeline, see
[Tutorial 3: language models](../../../../docs/Runtime/tutorials/03-language-models.md).

## Python

[`main.py`](main.py) runs the same ONNX/ORT or GGUF model through
`windowsml.runtime`:

```powershell
python.exe .\language\hello-language-model\main.py `
  --backend ort --prompt "Reply with one short greeting."
```

For GGUF, install the llama.cpp backend with
`python.exe -m pip install --pre windowsml-llama-core`, then run
`main.py --backend llama`. The wheel installs the CPU llama.cpp backend. To run
on a GPU, add a backend you build yourself; see
[Python payload install](../../../../docs/Runtime/gguf-models.md#python-payload-install)
and [GPU backends](../../../../docs/Runtime/gguf-models.md#gpu-backends).
