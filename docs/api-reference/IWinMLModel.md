<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLModel

> Part of the [WinML Runtime API Reference](README.md).

**Loaded model artifact handle.**

`IWinMLModel` is a reference-counted identity object with no methods of its own. Runtime loading returns this object for supported source and compiled artifacts, and `IWinMLRuntime::LoadModelFromPath` also returns it for a model folder. A loaded model is prepared for execution when it is added to a pipeline and the pipeline is built, or when a Models-backed Task binds it.

Query `IWinMLModelSchema` from `IWinMLModel` to inspect declared ordinal input and output schema when available. ONNX name reflection is exposed through [`IWinMLOrtModelSchema`](IWinMLOrtModelSchema.md) for ONNX artifacts. Query [`IWinMLModelWeightFormats`](IWinMLModelWeightFormats.md) to read how the weights are stored.

```
IID: 2453f1b4-81ce-4fd4-99ce-b83136883ff4
```
