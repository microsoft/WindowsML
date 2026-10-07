<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Local package override

NuGet restore checks this directory before public package sources. Normally,
leave it empty. To build the Server samples against a local package copy,
follow the steps in the
[Runtime samples' local package README](../../Runtime/localpackages/README.md);
`build.ps1` in `Samples\Server` accepts the same `-RuntimePackageVersion` and
`-LibLlamaPackageVersion` options.

Package files in this directory are gitignored.
