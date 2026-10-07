<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Model compilation

This sample compiles an ONNX model for one execution target, reloads the
compiled artifact as `IWinMLModel`, and runs it through the same pipeline API
used for the source model. An application can compile once and load the
compiled artifact on later runs.

The sample stores the compiled output in three ways:

| Mode | Compile output | Reload path |
|---|---|---|
| `file` | Artifact file and external-weights file | `LoadModelFromFile` |
| `sink` | Caller-owned artifact and resource buffers | `LoadModelFromBuffer` with a resource reader |
| `zerocopy` | Caller-owned buffers retained in place | `LoadModelFromBuffer` with a zero-copy reader |

The default, `-Mode all`, runs the three modes in sequence.

## Run

SqueezeNet is the small default input:

```powershell
.\check_artifacts.ps1 -Sample image-classification
.\build.ps1 -Configuration Release -PackageOnly

.\run_model_compilation.ps1 -Device cpu -Mode all
```

`-Device cpu` uses the CPU execution target for compilation and the sample run.

To compile another ONNX model, pass `-ModelPath <path>` to the PowerShell
launcher or `--model <path>` to the executable.

By default, file mode writes to a new temporary directory and removes it when
the sample exits. To keep a deployable artifact and every file the compiler
writes, pass a new or empty directory:

```powershell
.\run_model_compilation.ps1 `
  -Device cpu `
  -Mode file `
  -OutputDirectory .\compiled-squeezenet
```

The sample refuses to write into a non-empty directory and never deletes
caller-owned files.

To compile for an accelerator, add the device and provider options from
[Devices and execution providers](../../../../docs/Runtime/providers.md) and use
`-Mode file`. A provider can write extra files next to `artifact.onnx`; deploy
them together. The sink and zero-copy modes need a compiler that returns the
artifact and every resource it references through the sink. The CPU compiler
supports both modes; provider compilers vary.

Each mode releases the source model after compilation and before it loads the
compiled result. Sink and zero-copy modes keep only the captured artifact and
its resources in memory while the compiled model runs.

## Output and failures

In `file` mode, the compiler writes `artifact.onnx` to the output directory and
places any external resources in `weights.bin`.

The sample queries `IWinMLModelCompiler` from the execution target, so
compilation is available only where the selected target supports it. Results
also depend on the model: when the compiler cannot handle an operator in the
model, the sample prints the failing HRESULT, such as `ERROR_NOT_SUPPORTED`
(`0x80070032`). The source model is unchanged and still runs uncompiled.

See [Tutorial 6: compile and deploy model artifacts](../../../../docs/Runtime/tutorials/06-compile-and-deploy.md).
