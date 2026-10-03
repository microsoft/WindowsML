<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Devices and execution providers

Every Runtime sample runs on the CPU without extra setup. GPU and NPU execution
uses execution providers that Windows installs through the Windows ML execution
provider catalog. A sample chooses a device class with `--device` and, when you
need one specific provider, pins it with `--ep`.

## Device arguments

All Runtime samples accept the same arguments. The PowerShell run scripts take
the same options with a single dash, for example `-Device gpu -Ep <provider>`.

| Intent | Arguments |
|---|---|
| CPU | `--device cpu` |
| GPU chosen by the Runtime | `--device gpu` |
| NPU | `--device npu --ep <provider>` (`--ep` is required for NPU) |
| One specific provider | `--device <cpu\|gpu\|npu> --ep <provider>` |
| Prefer a discrete GPU adapter | Add `--performance` |
| Prefer an integrated GPU adapter | Add `--efficiency` |

`--performance` and `--efficiency` rank GPU adapters when more than one adapter
is present. They are not Runtime execution-policy settings.

## Pin a provider

`--ep` prepares and registers exactly one provider from the catalog, then pins
the requested device class to it. Pass the provider name that the catalog
reports. Pinning also narrows adapter selection to the adapters that provider
reports, and `--performance` or `--efficiency` rank adapters inside that set.
`CPUExecutionProvider` is built into ONNX Runtime and needs no catalog
registration; use it with `--device cpu`.
The sample prints the selected provider, device class, and adapter before it
builds the pipeline, and fails if the provider does not expose the requested
device class.

From `Samples\Runtime`:

```powershell
.\run_image_classification.ps1 -Device gpu -Ep <provider> -Diagnostics
```

`-Diagnostics` (`--verbose` for executables and Python) raises ONNX Runtime
logging so node-placement lines show which provider runs each part of the model.
Which combinations run depends on the providers installed on the PC, the
detected hardware, and the operators the model uses.

## Failures

| Symptom | Meaning |
|---|---|
| Target creation fails with `ERROR_NOT_FOUND` | No installed provider reports an adapter for the requested device class. |
| The requested provider is not found | The provider is not installed, or the catalog does not offer it on this PC. |
| Pipeline build fails | The model cannot run with the selected provider and device. |
| The sample succeeds, but the provider prints warnings | The provider declined some or all of the model's operators, and ONNX Runtime runs them on the CPU. `-Diagnostics` shows where each node runs. |

Start on the CPU, then change one option at a time.

## Devices and backends

`--device` does not select the backend; the model file does, as described in
[Backends and model formats](backends.md). The language samples' `--backend`
option chooses which model file the sample loads. It is a sample option, not a
Runtime setting, and the llama.cpp backend does not use an ONNX Runtime
execution provider.
