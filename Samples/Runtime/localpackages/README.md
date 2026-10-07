<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Local package override

NuGet restore checks this directory before public package sources.

Normally, use the package versions available from the public feed and do not
place anything here. To restore from a local package copy:

1. Copy the `.nupkg` into this directory. For the GGUF language samples, copy
   `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` together with the
   `Microsoft.Windows.AI.MachineLearning` package from the same Windows ML
   release. `LibLlama.Core` requires exactly the Runtime package version it was
   built with.
2. Set or override `WinMLRuntimePackageVersion` (and
   `WinMLLibLlamaPackageVersion` for LibLlama) to the matching package
   versions.
3. Keep any required dependency packages together with it.
4. Validate the package signature and source before use.

To select versions without editing `Directory.Packages.props`:

```powershell
.\build.ps1 -RuntimePackageVersion <package-version> -LibLlamaPackageVersion <libllama-version> -PackageOnly
```

## Python wheels

The Python samples in `Samples\Runtime` and `Samples\Tasks` can also use local
wheels. Copy the `windowsml` wheel into this directory. For the GGUF samples,
also copy `windowsml-llama-core` from the same build; it requires exactly the
`windowsml` version it was built with. Then, from `Samples\Runtime`, install it
with the version pinned so pip doesn't select a `windowsml` release from PyPI:

```powershell
python.exe -m pip install --pre --find-links .\localpackages "windowsml-llama-core==<windowsml-version>" numpy

# For example, for Runtime package 2.7.2021-experimental:
python.exe -m pip install --pre --find-links .\localpackages "windowsml-llama-core==2.7.2021a0" numpy
```

The wheel version is the Runtime package version without its suffix, plus
`.dev0` for dev builds or `a0` for experimental builds. The wheel
version doesn't include the commit, so copy the wheels from the same pipeline run
as the NuGet packages.

pip still installs other dependencies, such as `comtypes`, from PyPI. Validate the wheel source before use.

Package and wheel files in this directory are gitignored.
