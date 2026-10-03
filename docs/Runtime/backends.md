<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Backends and model formats

`LoadModelFromFile` selects the Runtime backend from the artifact file type.
The application still uses the same Runtime objects: model, target, pipeline
builder, stages, bindings, `Run`, and output tensors.

| Artifact | Backend | Typical sample use |
|---|---|---|
| `.onnx` | ONNX Runtime | Vision, speech, language, and compilation inputs. |
| `.ort` | ONNX Runtime | Preoptimized ONNX Runtime artifacts. |
| `.gguf` | llama.cpp | Unified decoder language samples. |

Device selection is separate. A sample can request `cpu`, `gpu`, or `npu`
while the model file type continues to choose the backend.

## Unified and split language layouts

The language samples use two layouts:

| Layout | Files | Backend path | What it shows |
|---|---|---|---|
| `unified` | One `.onnx`, `.ort`, or `.gguf` decoder | ONNX Runtime for `.onnx` and `.ort`; llama.cpp for `.gguf` | One decoder stage and a token loop. |
| `split` | `emb.onnx`, `decoder.onnx`, and `head.onnx` | ONNX Runtime | Stage placement, caller-owned bindings, and Runtime state. |

The split topology is:

```text
tokens -> emb.onnx -> decoder.onnx -> head.onnx -> logits
```

The application binds tokens to embedding input `0`. It binds the hidden state
from embedding output `0` to decoder input `0`, sequence position to decoder
input `1`, and the attention mask to decoder input `2`. Decoder output `0`
feeds head input `0`; the application reads logits from head output `0`.

The Python split sample reads token, position, mask, and logits tensor
properties from Runtime stage schemas. The mask shape supplies the sequence
capacity, and tokenizer metadata supplies terminal token IDs. The exporter also
emits `prefill.onnx` and `runtime_pipeline.json`; the Runtime samples do not use
them.

## Placement and residency

Model placement and tensor residency are different concepts.

- Model placement selects where a Runtime stage executes.
- Tensor residency describes where a tensor is stored when the application
  binds it or reads it.

For example, a GGUF model can use an optional accelerator module while the
sample reads logits through a CPU tensor. Runtime tensor movement is part of the
pipeline execution boundary.

## Composing pipelines with different backends

Speech to LanguageModel composes an ONNX Runtime speech pipeline with either an
ONNX Runtime or llama.cpp language pipeline. The model file loaded for the
language half selects that backend; the surrounding Runtime flow does not
change.

For GGUF payload setup, see [GGUF models](gguf-models.md).
