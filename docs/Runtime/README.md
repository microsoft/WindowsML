<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Windows ML Runtime documentation

These pages explain the concepts behind the Runtime samples, how to set up
Python and GGUF models, and where to find API details.

Start with the [Runtime API overview](overview.md) for how the Runtime, Task
API, and Windows ML Server fit together.

## Tutorials

The [tutorials](tutorials/README.md) run the samples in order, from a first
pipeline to language models, speech, accelerators, and compiled artifacts.

## Concepts

| Page | Covers |
|---|---|
| [Backends and model formats](backends.md) | How the model file selects ONNX Runtime or llama.cpp, and the unified and split language layouts. |
| [Devices and execution providers](providers.md) | Device arguments, provider pinning, adapter preference, and failures. |
| [Models and artifacts](artifacts.md) | How samples find, verify, and prepare their models, and the model catalog. |

## Setup

| Page | Covers |
|---|---|
| [Python samples](python-samples.md) | Python setup and the command for each sample. |
| [GGUF language models](gguf-models.md) | Running GGUF models from C++ and Python. |
| [llama.cpp backends](../../Tools/llama/README.md) | Building a llama.cpp backend for any ggml family that matches LibLlama.Core. |

## Reference

| Page | Covers |
|---|---|
| [Runtime API reference](../api-reference/README.md) | Interfaces, structures, and common patterns. |
| [Task and Runtime lifecycle](../Tasks/task-lifecycle.md) | The objects a Task API application owns, and how sessions, streams, and cancellation fit together. |
| [Windows ML Server](../api-reference/IWinMLServer.md) | Serving Text Generation lanes through the OpenAI-compatible endpoint: registration, lifecycle, access key, and HTTP behavior. |
