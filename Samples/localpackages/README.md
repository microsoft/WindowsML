# Local NuGet package source

Drop `.nupkg` files in this folder to test pre-release or locally built packages without pushing them to a remote feed. NuGet restore picks them up automatically via `Samples/nuget.config`.

Example:

```powershell
# Override Microsoft.Windows.AI.MachineLearning with a local build
copy C:\path\to\Microsoft.Windows.AI.MachineLearning.2.1.2-preview.nupkg .
```

Then bump the version reference in `Directory.Packages.props` (C#) or in the relevant `packages.config` / `.vcxproj` (C++) to match the local `.nupkg`.

This folder is intentionally tracked (via `.gitkeep`) but its `.nupkg` contents are ignored.
