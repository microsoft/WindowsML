# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Restores and builds one or all Windows ML Runtime sample projects.

.DESCRIPTION
Builds the selected native C++ project through MSBuild. Builds never download
models; use check_artifacts.ps1 or a run script to prepare assets separately.
PackageOnly clears any local include, library, or native payload overrides.

.PARAMETER Platform
Target architecture: x64 or ARM64. Defaults to the host architecture.

.PARAMETER Configuration
Build configuration: Debug or Release. Defaults to Release.

.PARAMETER Sample
The project to build, or all for the complete Runtime sample solution.

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
Get-Help .\build.ps1 -Examples
#>

[CmdletBinding()]
param(
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("all", "image-classification", "super-resolution", "whisper", "hello-language-model", "llm-chat", "speech-to-language-model", "model-compilation", "managed-shared-context")]
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

$msbuild = Find-MSBuild
$allProjects = @(
    "get-started\image-classification\image-classification.vcxproj",
    "vision\super-resolution\webcam-super-resolution.vcxproj",
    "speech\whisper\whisper-speech-to-text.vcxproj",
    "language\hello-language-model\hello-language-model.vcxproj",
    "language\llm-chat\llm-chat.vcxproj",
    "language\speech-to-language-model\speech-to-language-model.vcxproj",
    "compile-and-deploy\model-compilation\model-compilation.vcxproj",
    "interop\managed-shared-context\managed-shared-context.vcxproj"
)
$projects = switch ($Sample) {
    "image-classification" { @($allProjects[0]); break }
    "super-resolution" { @($allProjects[1]); break }
    "whisper" { @($allProjects[2]); break }
    "hello-language-model" { @($allProjects[3]); break }
    "llm-chat" { @($allProjects[4]); break }
    "speech-to-language-model" { @($allProjects[5]); break }
    "model-compilation" { @($allProjects[6]); break }
    "managed-shared-context" { @($allProjects[7]); break }
    default { $allProjects }
}

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
if ($RuntimePackageVersion) {
    $restoreArgs += "/p:WinMLRuntimePackageVersion=$RuntimePackageVersion"
    $buildArgs += "/p:WinMLRuntimePackageVersion=$RuntimePackageVersion"
}
if ($LibLlamaPackageVersion) {
    $restoreArgs += "/p:WinMLLibLlamaPackageVersion=$LibLlamaPackageVersion"
    $buildArgs += "/p:WinMLLibLlamaPackageVersion=$LibLlamaPackageVersion"
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
}
if ($PackageOnly) {
    $packageOnlyArgs = @(
        "/p:WinMLRuntimeLocalIncludeDir=",
        "/p:WinMLRuntimeLocalLibDir=",
        "/p:WinMLRuntimeLocalNativeDir="
    )
    $restoreArgs += $packageOnlyArgs
    $buildArgs += $packageOnlyArgs
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

foreach ($relativeProject in $projects) {
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

Write-Host "`nAll samples built successfully." -ForegroundColor Green
Write-Host "Output: $(Join-Path $PSScriptRoot "out\$Platform\$Configuration")"
