# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Publishes Windows ML Task Studio as a self-contained app, one zip for each architecture.

.DESCRIPTION
For each runtime identifier, publishes TaskStudio.csproj in Release as a self-contained app, with .NET, the
Windows App SDK, the Windows ML Runtime and the Direct3D 12 Agility SDK beside TaskStudio.exe. It then writes
dist\TaskStudio-<version>-<rid>.zip, with the app in a studio folder, a README.txt and MODELS.md.

The person who runs the app needs no .NET installation or build tools. Models are not included.

.PARAMETER Version
The version in the zip names and in README.txt.

.PARAMETER RuntimeIdentifiers
The architectures to publish, win-x64, win-arm64 or both.

.PARAMETER OutputDirectory
Where the zips are written. The default is the dist folder beside the project, which git ignores.

.EXAMPLE
.\Apps\TaskStudio\scripts\Pack-TaskStudio.ps1 -Version 0.1.0
#>
param(
    [string] $Version = "0.1.0",
    [ValidateSet("win-x64", "win-arm64")]
    [string[]] $RuntimeIdentifiers = @("win-x64", "win-arm64"),
    [string] $OutputDirectory = (Join-Path (Split-Path $PSScriptRoot) "dist")
)

$ErrorActionPreference = "Stop"
$appRoot = Split-Path $PSScriptRoot
$project = Join-Path $appRoot "TaskStudio.csproj"
$staging = Join-Path $OutputDirectory "staging"
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

foreach ($rid in $RuntimeIdentifiers) {
    $platform = if ($rid -eq "win-arm64") { "ARM64" } else { "x64" }
    $root = Join-Path $staging $rid
    $app = Join-Path $root "studio"
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }

    Write-Host "Publishing $rid..."
    dotnet publish $project -c Release -r $rid --self-contained true -p:Platform=$platform -o $app --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid." }
    foreach ($required in "TaskStudio.exe", "coreclr.dll", "Microsoft.WindowsAppRuntime.dll", "WinMLRuntimeCore.dll",
        "WinMLTasks.dll", "WinMLTasksModels.dll", "dxcgc.dll", "D3D12\D3D12Core.dll") {
        if (-not (Test-Path (Join-Path $app $required))) { throw "Published $rid output is missing $required." }
    }
    $runtimeVersion = (Get-Item (Join-Path $app "WinMLRuntimeCore.dll")).VersionInfo.ProductVersion

    @"
Windows ML Task Studio $Version ($rid)
Windows ML Runtime $runtimeVersion

Run studio\TaskStudio.exe. Everything the app needs is in the studio folder,
and no .NET installation or build tools are required.

Task Studio needs Windows on $platform and a Direct3D 12 GPU that the Windows
ML CGC backend supports. Models run only on the GPU, with no CPU fallback.

Models are not included. MODELS.md lists models known to work, with Hugging
Face links. In the app, the ? button beside MODEL explains what each tab loads.

If Windows blocks the app after you download the zip, open the zip's
Properties, select Unblock, then extract it again.

Compiled programs are cached in %LOCALAPPDATA%\WindowsMLTaskStudio\CompiledPrograms.
Settings > Advanced deletes them.

Task Studio is a sample app, provided as is.
"@ | Set-Content (Join-Path $root "README.txt") -Encoding utf8
    Copy-Item (Join-Path $appRoot "MODELS.md") (Join-Path $root "MODELS.md")

    $zip = Join-Path $OutputDirectory "TaskStudio-$Version-$rid.zip"
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    Write-Host "Writing $zip..."
    Compress-Archive -Path (Join-Path $root "*") -DestinationPath $zip -CompressionLevel Optimal
}
