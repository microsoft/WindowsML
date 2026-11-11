<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Tutorial 6: compile and deploy model artifacts

This tutorial uses the image-classification model to demonstrate Runtime model
compilation and reload.

## 1. Prepare and build

```powershell
.\check_artifacts.ps1 -Sample image-classification
.\build.ps1 -Configuration Release -PackageOnly `
  -Sample model-compilation
```

If files are missing, the script prints the commands that acquire them. Review
the publisher's terms, run the commands, then run the check again. See
[Models and artifacts](../artifacts.md) for details.

## 2. Run all CPU storage modes

```powershell
.\run_model_compilation.ps1 -Device cpu -Mode all
```

The sample runs:

| Mode | Compile destination | Reload path |
|---|---|---|
| `file` | Artifact and resources on disk | `LoadModelFromFile` |
| `sink` | Caller-owned buffers | `LoadModelFromBuffer` + reader |
| `zerocopy` | App-held resources | Buffer load without copying resource ownership |

## 3. Trace the code

Read `compile-and-deploy\model-compilation\main.cpp`.

Key interfaces:

- `IWinMLModelCompiler`;
- `IWinMLCompileOutputSink`;
- `IWinMLResourceMapReader`;
- `LoadModelFromFile`;
- `LoadModelFromBuffer`.

The compiled artifact is still an `IWinMLModel` and returns to the same pipeline
builder used by source ONNX.

## 4. Provider-backed compilation

When a provider and model support compilation:

```powershell
.\run_model_compilation.ps1 `
  -Device gpu `
  -Ep <provider> `
  -Mode file `
  -Diagnostics
```

Deploy the produced artifact with every backend-generated sidecar.

The sample uses a unique temporary output directory, reloads the compiled
artifact while that directory and any generated sidecars remain alive, and
then removes the temporary directory. It never deletes fixed filenames
from the caller's working directory.

Pass `-OutputDirectory <new-or-empty-directory>` with file or all mode to
retain the artifact and sidecars for deployment.

## 5. Compile for the CGC backend

A GPU target that isn't pinned to an execution provider compiles an ONNX model
for the [CGC backend](../backends.md#the-cgc-backend). The sample above always
compiles through a provider, so this step uses a few lines of Python from
`Samples\Runtime`:

```python
from windowsml.runtime import (
    CompiledModelForm,
    ExecutionTargetKind,
    Runtime,
)

with (
    Runtime() as runtime,
    runtime.create_target(ExecutionTargetKind.GPU) as target,
    runtime.load_model(r"models\squeezenet\SqueezeNet.onnx") as model,
    target.model_compiler() as compiler,
):
    # Device-targeted: loads fastest, on this device and software version only.
    compiler.compile_to_file(model, "squeezenet.oir")
    # Durable: finishes compiling when it loads, on any supported device.
    if compiler.supports_form(CompiledModelForm.DURABLE):
        compiler.compile_to_file(
            model, "squeezenet.mlirbc", form=CompiledModelForm.DURABLE
        )
```

In C++, set the form on the options object from
`IWinMLModelCompiler::CreateCompileOptions` with `SetCompiledModelForm`, then
call `CompileToFileWithOptions`. See
[`IWinMLModelCompiler`](../../api-reference/IWinMLModelCompiler.md#compiled-model-form).

Keep the `.oir` and `.mlirbc` extensions; `LoadModelFromFile` recognizes a CGC
artifact by them. Load the artifact with `runtime.load_model` and add it to a
pipeline stage on a GPU target like any other model. The image-classification
sample checks that its stage runs on ONNX Runtime, so don't pass the artifact
to its `--model` option. Numeric results can differ slightly from the ONNX
source, as with any change of backend. With execution diagnostics enabled, the
stage reports its work as compiled subgraphs on the `D3D12_DRIVER` and
`DIRECTML` technologies.

A durable artifact keeps loading after a driver or Windows ML update. A
device-targeted artifact from another device or software version fails to load;
compile it again, or ship the durable form and accept the longer first load.

## Next

Continue to [GGUF language models](07-gguf-language-models.md).
