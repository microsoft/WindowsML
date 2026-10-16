<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Optional model MSIX sample

A WinForms app installs and removes a separate, content-only model package.
The bundled ONNX Runtime runs the demo model: **`y = 2x + 1`**.

The folder is standalone: it includes the app, model source, manifests, and
build/signing scripts. No other repository files, Git LFS, prebuilt packages, or
downloaded images are needed.

## Requirements

- **Windows 10 1809 (`10.0.17763.0`) or later, x64**.
- **[PowerShell 7, x64](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows)**.
- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), x64**, not just
  the runtime. `global.json` accepts 8.0.100 or a later .NET 8 feature band.
- **[Windows SDK 10.0.26100.0](https://developer.microsoft.com/windows/downloads/sdk-archive/)**
  with `MakeAppx.exe` and `SignTool.exe` in
  `C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64`.
- Access to **nuget.org** for dependency restore and vulnerability checks.
- Permission to run scripts and install development packages. Certificate trust
  requires administrator approval; follow device policy rather than bypassing it.

Visual Studio is not required. Main bundles .NET, Windows App SDK, and Windows ML,
including ONNX Runtime; they do not need separate runtime installation.
The workflow was tested on Windows 11 x64.

## Package contents and logos

`Build.ps1` copies the included PNG logos from `Assets`, decodes `Optional\demo.onnx.base64`,
and packs these files:

```text
Main.msix
  AppxManifest.xml
  OptionalMsixMinimal.exe and bundled dependencies
  Assets\StoreLogo.png
  Assets\Square44x44Logo.png
  Assets\Square150x150Logo.png
Optional.msix
  AppxManifest.xml
  Assets\StoreLogo.png
  Models\demo.onnx
  hello.txt
```

The optional manifest's `<Logo>Assets\StoreLogo.png</Logo>` is required even
though the package contains no app. The build copies `Assets\StoreLogo.png`
into **both** packages. `AllowExecution=false`
keeps Optional content-only; `MainPackageDependency` links it to Main.

## 1. Copy the complete source folder

Copy the entire `Samples\OptionalMsixMinimal` folder to `C:\OptionalMsixMinimal`.
Keep the dotfiles, `Directory.Build.props`, `NuGet.Config`, `global.json`,
`model-catalog.example.json`, scripts, and `Assets`/`Main`/`Optional` folders.
`C:\OptionalMsixMinimal\Build.ps1` should exist. Building in the original folder
also works.

## 2. Build and sign

In normal, non-administrator PowerShell:

```powershell
Set-Location C:\OptionalMsixMinimal
.\Build.ps1

# First time only; reuse this signer on subsequent builds.
.\Certificate.ps1 -Action New
$signer = Get-Content .\certificates\signer.json -Raw | ConvertFrom-Json
.\Sign.ps1 -CertificateThumbprint $signer.thumbprint
```

Build output goes to `artifacts\unsigned`; signing produces
`artifacts\signed\Main.msix` and `Optional.msix`. Neither step trusts or installs
packages. Use `.\Build.ps1 -NoRestore` after a successful restore.

The six-month development certificate uses `CN=OptionalMsixMinimal`, matching
both manifests. Its non-exportable private key stays in `CurrentUser\My`;
`certificates\signer.cer` contains only the public certificate. Keep
`certificates\signer.json` to reuse the signer. This demo does not timestamp
signatures and is not a production signing workflow.

## 3. Trust the development signer

Have your administrator review `certificates\signer.cer` and verify its
thumbprint against `signer.json`. If approved, in **Administrator PowerShell**:

```powershell
Set-Location C:\OptionalMsixMinimal
.\Certificate.ps1 -Action Trust -WhatIf
.\Certificate.ps1 -Action Trust
```

This trusts the signer in `LocalMachine\TrustedPeople`, not Root, and records
the change in `certificates\trust.json`. Keep that file for cleanup. Trust is
needed once per signer, not once per build.

## 4. Install Main, then Optional

Return to **normal PowerShell** as the user who will run the app. Close existing
sample instances. Both packages use version `1.0.0.0`; an earlier installation
may conflict. If so, use the cleanup commands below before reinstalling, noting
that removing Main can delete its app data.

```powershell
Set-Location C:\OptionalMsixMinimal
Add-AppxPackage -Path .\artifacts\signed\Main.msix
Add-AppxPackage -Path .\artifacts\signed\Optional.msix
```

## 5. Create a local model catalog

Run this block after installing both packages:

```powershell
Set-Location C:\OptionalMsixMinimal
$ErrorActionPreference = 'Stop'
$main = @(Get-AppxPackage -Name OptionalMsixMinimal.Main)
$optional = @(Get-AppxPackage -Name OptionalMsixMinimal.Optional -PackageTypeFilter Optional)
if ($main.Count -ne 1 -or $optional.Count -ne 1) {
    throw 'Install your Main.msix and Optional.msix first.'
}
if ($main[0].Publisher -ne 'CN=OptionalMsixMinimal' -or
    $optional[0].PackageFamilyName -ne "OptionalMsixMinimal.Optional_$($main[0].PublisherId)") {
    throw 'The installed packages do not match this sample.'
}
$catalog = Get-Content -LiteralPath .\model-catalog.example.json -Raw | ConvertFrom-Json
$package = $catalog.models[0].packages[0]
$package.packageFamilyName = $optional[0].PackageFamilyName
$package.sha256 = (Get-FileHash -LiteralPath .\artifacts\signed\Optional.msix -Algorithm SHA256).Hash
$catalog | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath .\artifacts\signed\model-catalog.json -Encoding utf8
```

The JSON uses your installed package family and signed MSIX hash. Its
`example.invalid` URL is acceptable **only for this installed-model test**:
inference reuses the installed package without downloading. Keep the catalog
outside the MSIX and regenerate it after signing again.

## 6. Run and test

```powershell
Start-Process "$env:LOCALAPPDATA\Microsoft\WindowsApps\optional-msix.exe"
```

Or open **Optional MSIX Minimal** from Start. Do not use `dotnet run` or the
intermediate executable; the app requires installed package identity.

Expect **Optional package: installed** and a path ending in `Models\demo.onnx`.
In **Catalog JSON URL or local file**, enter:

    C:\OptionalMsixMinimal\artifacts\signed\model-catalog.json

Select **Run model** and check:

| Input x | Expected y |
|---|---|
| 4 | 9 |
| 0 | 1 |
| -3 | -5 |
| 1.25 | 3.5 |

Expect **ORT inference completed.** Close/reopen the app and repeat with the
same catalog. Local inference needs no network after setup.

Select **Uninstall optional model**, first choosing **No**, then **Yes**.
Canceling keeps inference working; confirming removes Optional while retaining
Main and disables **Run model**. Windows may close the app during deployment;
reopen it to check status. Reinstall `artifacts\signed\Optional.msix` locally to
repeat the test. The placeholder catalog cannot download it.

In Windows Settings, open **Apps > Installed apps > Optional MSIX Minimal >
Advanced options**. Optional appears under **App add-ons & downloadable content**;
reported sizes are installed usage, not download sizes.

## Cleanup

Close the app. In normal PowerShell, remove only the model:

```powershell
Set-Location C:\OptionalMsixMinimal
.\Cleanup.ps1 -Package Optional -WhatIf
.\Cleanup.ps1 -Package Optional
Get-AppxPackage -Name OptionalMsixMinimal.Main
Get-AppxPackage -Name OptionalMsixMinimal.Optional -PackageTypeFilter Optional
```

Main should remain registered; the Optional query should return nothing.
To remove both packages and potentially Main's app data, use
`.\Cleanup.ps1 -Package All -WhatIf`, then `.\Cleanup.ps1 -Package All`.

To remove trust added in step 3, keep `certificates\trust.json` and run in
**Administrator PowerShell**:

```powershell
Set-Location C:\OptionalMsixMinimal
.\Certificate.ps1 -Action RemoveTrust -WhatIf
.\Certificate.ps1 -Action RemoveTrust
```

Cleanup preserves pre-existing certificate trust, signing keys, source, and
generated files. The signing key remains in `CurrentUser\My` for reuse.
