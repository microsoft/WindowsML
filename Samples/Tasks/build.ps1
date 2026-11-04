# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Restores and builds one or all Windows ML Task API samples.

.PARAMETER Platform
Target architecture. Defaults to the host architecture.

.PARAMETER Sample
The sample project to build, or all. C++ projects need Visual Studio with
Desktop development with C++. model-text-generation-csharp needs the .NET 8
SDK or later; -Sample all skips it with a warning when the SDK is missing.

.PARAMETER RuntimePackageVersion
Overrides the centrally pinned Microsoft.Windows.AI.MachineLearning version.

.PARAMETER LibLlamaPackageVersion
Overrides the centrally pinned Microsoft.Windows.AI.MachineLearning.LibLlama
version. It must come from the same Windows ML release as the Runtime package.

.PARAMETER PackageOnly
Builds against restored NuGet package contents.
#>

[CmdletBinding()]
param(
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("all", "text-generation", "chat-completion", "automatic-speech-recognition", "speech-to-text-generation",
                 "model-text-generation", "model-text-generation-csharp", "composed-text-generation",
                 "llama-text-generation", "model-speech-recognition", "decision")]
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

$projects = [ordered]@{
    "text-generation" = "language\text-generation\task-text-generation.vcxproj"
    "chat-completion" = "language\chat-completion\task-chat-completion.vcxproj"
    "automatic-speech-recognition" = "speech\automatic-speech-recognition\task-automatic-speech-recognition.vcxproj"
    "speech-to-text-generation" = "composition\speech-to-text-generation\task-speech-to-text-generation.vcxproj"
    "model-text-generation" = "model-backed\text-generation\task-model-text-generation.vcxproj"
    "model-text-generation-csharp" = "model-backed\text-generation\csharp\task-model-text-generation-csharp.csproj"
    "composed-text-generation" = "model-backed\composed-text-generation\task-composed-text-generation.vcxproj"
    "llama-text-generation" = "model-backed\llama-text-generation\task-llama-text-generation.vcxproj"
    "model-speech-recognition" = "model-backed\automatic-speech-recognition\task-model-speech-recognition.vcxproj"
    "decision" = "model-backed\decision\task-decision.vcxproj"
}
$selectedProjects = if ($Sample -eq "all") {
    $projects.Values
}
else {
    @($projects[$Sample])
}

# C++ projects build first. C# projects need the .NET SDK: when it is missing,
# -Sample all skips them with a warning, and naming one fails.
$cppProjects = @($selectedProjects | Where-Object { -not $_.EndsWith(".csproj") })
$csharpProjects = @($selectedProjects | Where-Object { $_.EndsWith(".csproj") })
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
        "/p:WinMLRuntimeLocalNativeDir=",
        "/p:WinMLTaskPackageOnly=true"
    )
    $restoreArgs += $packageOnlyArgs
    $buildArgs += $packageOnlyArgs
    $dotnetArgs += $packageOnlyArgs
}

Write-Host "Building Windows ML Task API samples" -ForegroundColor Cyan
Write-Host "Platform: $Platform"
Write-Host "Configuration: $Configuration"
Write-Host "Sample: $Sample"
Write-Host "Backends: all backends provided by the package"

foreach ($relativeProject in $cppProjects) {
    $project = Join-Path $PSScriptRoot $relativeProject
    Write-Host "`nRestoring $relativeProject" -ForegroundColor Yellow
    & $msbuild $project @restoreArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Building $relativeProject" -ForegroundColor Yellow
    & $msbuild $project @buildArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

foreach ($relativeProject in $csharpProjects) {
    $project = Join-Path $PSScriptRoot $relativeProject
    Write-Host "`nBuilding $relativeProject" -ForegroundColor Yellow
    & dotnet build $project @dotnetArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "`nTask API sample build completed." -ForegroundColor Green
