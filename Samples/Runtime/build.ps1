# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Restores and builds one or all Windows ML Runtime sample projects.

.DESCRIPTION
Builds the selected C++ projects through MSBuild and C# projects through the
.NET SDK. Builds never download models; use check_artifacts.ps1 or a run
script to prepare assets separately.
PackageOnly clears any local include, library, or native payload overrides.

.PARAMETER Platform
Target architecture: x64 or ARM64. Defaults to the host architecture.

.PARAMETER Configuration
Build configuration: Debug or Release. Defaults to Release.

.PARAMETER Sample
The project to build, or all for every C++ and C# Runtime sample. C++
projects need Visual Studio with Desktop development with C++. The C# samples
(image-classification-csharp and hello-language-model-csharp) build after them
with the .NET 8 SDK or later; -Sample all skips them with a warning when the
SDK is missing.

.PARAMETER RuntimePackageVersion
Overrides the centrally pinned Microsoft.Windows.AI.MachineLearning version.

.PARAMETER LibLlamaPackageVersion
Overrides the centrally pinned Microsoft.Windows.AI.MachineLearning.LibLlama
version. It must come from the same Windows ML release as the Runtime package.

.PARAMETER PackageOnly
Clears local Runtime include/library/native overrides so the build uses only
the NuGet package.

.PARAMETER NuGetPackagesDir
Optional isolated global-packages directory for restore.

.PARAMETER Rebuild
Uses the MSBuild Rebuild target instead of incremental Build.

.EXAMPLE
.\build.ps1 -Sample image-classification -Platform x64 -Configuration Release -PackageOnly

.EXAMPLE
.\build.ps1 -Sample all -Platform ARM64 -Configuration Debug -PackageOnly

.EXAMPLE
.\build.ps1 -Sample image-classification-csharp -PackageOnly

.EXAMPLE
Get-Help .\build.ps1 -Examples
#>

[CmdletBinding()]
param(
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("all", "image-classification", "image-classification-csharp", "super-resolution", "whisper", "hello-language-model", "hello-language-model-csharp", "llm-chat", "speech-to-language-model", "model-compilation", "managed-shared-context")]
    [string]$Sample = "all",
    [string]$RuntimePackageVersion,
    [string]$LibLlamaPackageVersion,
    [switch]$PackageOnly,
    [string]$NuGetPackagesDir,
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"

function Find-MSBuild {
    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
            -find "MSBuild\Current\Bin\MSBuild.exe" | Select-Object -First 1
        if ($path) {
            return $path
        }
    }

    throw "MSBuild was not found. Install Visual Studio with Desktop development with C++."
}

$sampleProjects = [ordered]@{
    "image-classification" = "get-started\image-classification\image-classification.vcxproj"
    "image-classification-csharp" = "get-started\image-classification\csharp\image-classification-csharp.csproj"
    "super-resolution" = "vision\super-resolution\webcam-super-resolution.vcxproj"
    "whisper" = "speech\whisper\whisper-speech-to-text.vcxproj"
    "hello-language-model" = "language\hello-language-model\hello-language-model.vcxproj"
    "hello-language-model-csharp" = "language\hello-language-model\csharp\hello-language-model-csharp.csproj"
    "llm-chat" = "language\llm-chat\llm-chat.vcxproj"
    "speech-to-language-model" = "language\speech-to-language-model\speech-to-language-model.vcxproj"
    "model-compilation" = "compile-and-deploy\model-compilation\model-compilation.vcxproj"
    "managed-shared-context" = "interop\managed-shared-context\managed-shared-context.vcxproj"
}
$projects = if ($Sample -eq "all") { @($sampleProjects.Values) } else { @($sampleProjects[$Sample]) }
# C++ projects build first. C# projects need the .NET SDK: when it is missing,
# -Sample all skips them with a warning, and naming one fails.
$cppProjects = @($projects | Where-Object { -not $_.EndsWith(".csproj") })
$csharpProjects = @($projects | Where-Object { $_.EndsWith(".csproj") })
if ($csharpProjects.Count -gt 0 -and -not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    if ($Sample -ne "all") {
        throw "The .NET SDK was not found. Install the .NET 8 SDK or later to build the C# samples."
    }

    Write-Warning "The .NET SDK was not found, so the C# samples are skipped. Install the .NET 8 SDK or later to build them."
    $csharpProjects = @()
}
$msbuild = if ($cppProjects.Count -gt 0) { Find-MSBuild } else { $null }

$restoreArgs = @(
    "/t:Restore",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/v:minimal"
)
$buildArgs = @(
    $(if ($Rebuild) { "/t:Rebuild" } else { "/t:Build" }),
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/m",
    "/v:minimal"
)
# C# samples build with the .NET SDK, as Samples\Server\build.ps1 does.
$dotnetArgs = @(
    "-c", $Configuration,
    "/p:Platform=$Platform",
    "--nologo",
    "-v:minimal"
)
if ($Rebuild) {
    $dotnetArgs += "--no-incremental"
}
if ($RuntimePackageVersion) {
    $restoreArgs += "/p:WinMLRuntimePackageVersion=$RuntimePackageVersion"
    $buildArgs += "/p:WinMLRuntimePackageVersion=$RuntimePackageVersion"
    $dotnetArgs += "/p:WinMLRuntimePackageVersion=$RuntimePackageVersion"
}
if ($LibLlamaPackageVersion) {
    $restoreArgs += "/p:WinMLLibLlamaPackageVersion=$LibLlamaPackageVersion"
    $buildArgs += "/p:WinMLLibLlamaPackageVersion=$LibLlamaPackageVersion"
    $dotnetArgs += "/p:WinMLLibLlamaPackageVersion=$LibLlamaPackageVersion"
}
if ($NuGetPackagesDir) {
    New-Item -ItemType Directory -Force -Path $NuGetPackagesDir | Out-Null
    $resolvedPackagesDir =
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
            $NuGetPackagesDir)
    $restoreArgs += @(
        "/p:RestorePackagesPath=$resolvedPackagesDir",
        "/p:RestoreNoCache=true",
        "/p:RestoreForce=true",
        "/p:RestoreForceEvaluate=true",
        "/p:RestoreFallbackFolders=",
        "/p:RestoreAdditionalProjectFallbackFolders="
    )
    $buildArgs += "/p:RestorePackagesPath=$resolvedPackagesDir"
    $dotnetArgs += @("--packages", $resolvedPackagesDir)
}
if ($PackageOnly) {
    $packageOnlyArgs = @(
        "/p:WinMLRuntimeLocalIncludeDir=",
        "/p:WinMLRuntimeLocalLibDir=",
        "/p:WinMLRuntimeLocalNativeDir="
    )
    $restoreArgs += $packageOnlyArgs
    $buildArgs += $packageOnlyArgs
    $dotnetArgs += $packageOnlyArgs
}

Write-Host "Building Windows ML Runtime samples" -ForegroundColor Cyan
Write-Host "Platform: $Platform"
Write-Host "Configuration: $Configuration"
Write-Host "Sample: $Sample"
Write-Host "Backends: all backends provided by the package"
if ($NuGetPackagesDir) {
    Write-Host "NuGet packages: $resolvedPackagesDir"
}
if ($RuntimePackageVersion) {
    Write-Host "Runtime package: $RuntimePackageVersion"
}
if ($LibLlamaPackageVersion) {
    Write-Host "LibLlama package: $LibLlamaPackageVersion"
}
if ($PackageOnly) {
    Write-Host "Runtime source: NuGet package only"
}

foreach ($relativeProject in $cppProjects) {
    $project = Join-Path $PSScriptRoot $relativeProject
    Write-Host "`nRestoring $relativeProject" -ForegroundColor Yellow
    & $msbuild $project @restoreArgs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Write-Host "Building $relativeProject" -ForegroundColor Yellow
    & $msbuild $project @buildArgs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

foreach ($relativeProject in $csharpProjects) {
    $project = Join-Path $PSScriptRoot $relativeProject
    Write-Host "`nBuilding $relativeProject" -ForegroundColor Yellow
    & dotnet build $project @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Write-Host "`nAll samples built successfully." -ForegroundColor Green
Write-Host "Output: $(Join-Path $PSScriptRoot "out\$Platform\$Configuration")"
