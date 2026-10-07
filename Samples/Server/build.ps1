# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Restores and builds one or all Windows ML Server samples.

.PARAMETER Platform
Target architecture. Defaults to the host architecture.

.PARAMETER Sample
The sample project to build, or all.

.PARAMETER RuntimePackageVersion
Overrides the Microsoft.Windows.AI.MachineLearning package version.

.PARAMETER LibLlamaPackageVersion
Overrides the centrally pinned Microsoft.Windows.AI.MachineLearning.LibLlama
version. It must come from the same Windows ML release as the Runtime package.
#>

[CmdletBinding()]
param(
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("all", "in-process", "executable", "client-cpp", "client-csharp")]
    [string]$Sample = "all",
    [string]$RuntimePackageVersion,
    [string]$LibLlamaPackageVersion,
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

# The C++ projects build with MSBuild. The C# client is a .NET project and
# builds with the .NET SDK.
$projects = [ordered]@{
    "in-process" = "host\in-process\server-in-process.vcxproj"
    "executable" = "host\executable\server-executable.vcxproj"
    "client-cpp" = "client\cpp\server-client-cpp.vcxproj"
    "client-csharp" = "client\csharp\ServerClient.CSharp.csproj"
}
$selectedProjects = if ($Sample -eq "all") {
    $projects.Values
}
else {
    @($projects[$Sample])
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
$dotnetArgs = @(
    "-c", $Configuration,
    "/p:Platform=$Platform",
    "--nologo"
)
if ($Rebuild) {
    $dotnetArgs += "--no-incremental"
}
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
    $dotnetArgs += @("--packages", $resolvedPackagesDir)
}

$msbuild = $null
if ($selectedProjects | Where-Object { $_.EndsWith(".vcxproj") }) {
    $msbuild = Find-MSBuild
}
if ($selectedProjects | Where-Object { $_.EndsWith(".csproj") }) {
    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
        throw "The .NET SDK was not found. Install the .NET 8 SDK to build the C# client."
    }
}

Write-Host "Building Windows ML Server samples" -ForegroundColor Cyan
Write-Host "Platform: $Platform"
Write-Host "Configuration: $Configuration"
Write-Host "Sample: $Sample"

foreach ($relativeProject in $selectedProjects) {
    $project = Join-Path $PSScriptRoot $relativeProject
    if ($relativeProject.EndsWith(".csproj")) {
        Write-Host "`nBuilding $relativeProject" -ForegroundColor Yellow
        & dotnet build $project @dotnetArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        continue
    }

    Write-Host "`nRestoring $relativeProject" -ForegroundColor Yellow
    & $msbuild $project @restoreArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Building $relativeProject" -ForegroundColor Yellow
    & $msbuild $project @buildArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "`nServer sample build completed." -ForegroundColor Green
