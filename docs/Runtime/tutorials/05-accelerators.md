<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Tutorial 5: accelerators and execution providers

Start on CPU before testing an accelerator. This separates general
model/sample problems from provider-specific failures.

## 1. CPU baseline

```powershell
.\run_image_classification.ps1 -Device cpu
```

Record the expected classification before changing the target.

## 2. Default GPU target

```powershell
.\run_image_classification.ps1 -Device gpu -Diagnostics
```

Without `-Ep`, the Runtime selects a GPU execution provider. The command fails
if no hardware GPU target can be created.

## 3. Pin a provider

```powershell
.\run_image_classification.ps1 `
  -Device gpu `
  -Ep <provider> `
  -Diagnostics
```

The sample prepares that provider, creates a provider-pinned ONNX Runtime execution
target, and fails if it cannot satisfy the requested device class.

## 4. Adapter preference

On a multi-GPU system:

```powershell
.\run_super_resolution.ps1 -Device gpu -Performance
.\run_super_resolution.ps1 -Device gpu -Efficiency
```

These flags choose between performance and efficiency adapter preference. They
are adapter selection, not general Runtime performance policy.

## 5. Check the run

A requested command-line value is not enough. Check:

- requested device/provider;
- selected adapter;
- resolved stage target;
- ONNX Runtime provider diagnostics where available;
- correct scenario output.

Successful environment setup without successful inference does not validate the
sample run.

## 6. Common failures

| Failure | Meaning |
|---|---|
| `ERROR_NOT_FOUND` during GPU target creation | No suitable hardware adapter |
| Requested provider missing | Provider package is not installed or available |
| Build fails for target | The selected model, provider, and device cannot run together |
| Correct output but unexpected provider | The run did not use the requested provider |

## Next

Continue to [Compile and deploy](06-compile-and-deploy.md).
