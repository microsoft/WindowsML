<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Managed shared-context sample

This sample builds and executes a two-stage ONNX pipeline. Both stages use the
same execution provider and can participate in one Runtime-managed provider
context.

The generated models use static `1x16x32x32` tensors and implement:

```text
add_one_3x3.onnx: y = Relu(ConvIdentity3x3(x) + 1)
add_one_1x1.onnx: z = Relu(ConvIdentity1x1(y) + 1)
```

For an input tensor filled with `3`, every pipeline output element is `5`. The
static Conv/Relu graphs provide a representative NPU workload instead of relying
on scalar arithmetic operators that a provider may leave on the CPU.

## Generate the models

Install the generator dependencies:

```powershell
python.exe -m pip install onnx numpy
```

Generate the model pair from `Samples\Runtime`:

```powershell
python.exe .\interop\managed-shared-context\generate_models.py `
  --output .\models\managed-shared-context
```

## Build

Build the sample from `Samples\Runtime`:

```powershell
.\build.ps1 -Configuration Release -PackageOnly `
  -Sample managed-shared-context
```

The sample is written to the following path, where `<platform>` is `x64` or
`ARM64`:

```text
out\<platform>\Release\managed-shared-context\managed-shared-context.exe
```

## Run

Run without provider arguments to let the launcher select an execution provider
from detected accelerator hardware, or fall back to CPU when no supported NPU is
present:

```powershell
.\run_managed_shared_context.ps1
```

The selected provider package must be installed and ready. Use command-line
arguments to override automatic selection. If hardware detection is unavailable,
the script reports an error instead of silently selecting CPU:

```powershell
.\run_managed_shared_context.ps1 -DeviceKind cpu
```

To execute the same pipeline without Runtime-managed sharing:

```powershell
.\run_managed_shared_context.ps1 -DisableSharing
```

Both modes check that every output element is `5`. The sample also sets
`session.disable_cpu_ep_fallback=1` on both
stages when a hardware provider is selected. Pipeline construction therefore
fails if any model node cannot be assigned to that provider instead of silently
running that node on the CPU.

The launcher redirects verbose provider compiler output to
`profiles\managed-shared-context`, prints the sample result, and summarizes the
execution providers recorded in each JSON profile. Run the
executable directly when the full provider diagnostics are needed.

## Managed-group requirements

- A group must contain at least two stages.
- Every member must select the same provider and device.
- Every member must use identical provider options.
- Configure membership before building the pipeline.
- Group names are scoped to one pipeline build.
- Runtime initializes group members consecutively and closes the provider
  sharing sequence after the final member.
