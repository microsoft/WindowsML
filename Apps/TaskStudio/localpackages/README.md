<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Local package override

NuGet restore checks this directory before public package sources. Normally,
leave it empty. To build Task Studio against a local copy of the
`Microsoft.Windows.AI.MachineLearning` package, copy the `.nupkg` here and set
its version, as described in the
[Runtime samples' local package README](../../../Samples/Runtime/localpackages/README.md):

```powershell
dotnet build Apps\TaskStudio\TaskStudio.csproj -p:Platform=ARM64 -p:WinMLRuntimePackageVersion=<version>
```

Package files in this directory are gitignored.
