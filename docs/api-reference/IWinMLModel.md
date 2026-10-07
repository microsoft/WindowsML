<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLModel

> Part of the [WinML Runtime API Reference](README.md).

**Loaded model artifact handle.**

`IWinMLModel` is a reference-counted identity object with no methods of its own. Runtime loading returns this object for supported source and compiled artifacts. A loaded model is prepared for execution when it is added to a pipeline and the pipeline is built.

Query `IWinMLModelSchema` from `IWinMLModel` to inspect declared ordinal input and output schema when available. ONNX name reflection is exposed through [`IWinMLOrtModelSchema`](IWinMLOrtModelSchema.md) for ONNX artifacts.

```
IID: 2453f1b4-81ce-4fd4-99ce-b83136883ff4
```
