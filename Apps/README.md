<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Apps

Complete applications built on Windows ML. Each [sample](../Samples/) shows one
API or scenario in a small program. An app combines them the way a product
would, with a user interface, device selection, settings, and error handling.

The apps are samples. They're provided as is, to show how an application can
use Windows ML end to end, and they aren't supported products. Use them as a
reference or as a starting point for your own application.

| App | What it shows |
|---|---|
| [Task Studio](TaskStudio/) | A WinUI 3 desktop app, written in C#, for the Models-backed Task APIs. Chat and completion with checkpoint folders and GGUF files, speculative decoding, recorded and live speech recognition, typed decisions, and performance measurements, all on the GPU through the CGC backend. |

## Package versions

The apps build against the Windows ML package versions that
[`Samples\Directory.Packages.props`](../Samples/Directory.Packages.props) pins,
so the apps and the samples always use the same Windows ML release.
[`Directory.Packages.props`](Directory.Packages.props) imports those versions
and adds the packages that only the apps use.
