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

## Next

Continue to [GGUF language models](07-gguf-language-models.md).
