<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Local package override

NuGet restore checks this directory before public package sources. Normally,
leave it empty. To build the Task API samples against a local package copy,
follow the steps in the
[Runtime samples' local package README](../../Runtime/localpackages/README.md);
`build.ps1` in `Samples\Tasks` accepts the same `-RuntimePackageVersion`,
`-LibLlamaPackageVersion`, and `-PackageOnly` options.

Package files in this directory are gitignored.
