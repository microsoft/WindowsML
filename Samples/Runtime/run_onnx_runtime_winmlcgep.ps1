# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the ONNX Runtime sample with the Windows ML CG execution provider.

.DESCRIPTION
Verifies the cataloged SqueezeNet assets, locates the built executable, and runs
inference on a physical GPU. Never downloads or modifies model assets.

.PARAMETER Recompile
Replaces the cache for the current model, Runtime package, architecture, and GPU.

.PARAMETER AdapterIndex
Selects a physical GPU from the candidates listed by the sample.

.PARAMETER AdapterName
Selects one physical GPU by case-insensitive description substring.

.PARAMETER ModelsDir
Root containing the squeezenet assets. Defaults to Samples\Runtime\models.
The executable loads from this same verified root without searching elsewhere.

.PARAMETER Platform
Architecture of the previously built executable.

.PARAMETER Configuration
Configuration of the previously built executable.

.EXAMPLE
.\run_onnx_runtime_winmlcgep.ps1 -AdapterName "NVIDIA" -Recompile
#>

[CmdletBinding()]
param(
    [switch]$Recompile,
    [int]$AdapterIndex = 0,
    [string]$AdapterName,
    [ValidateNotNullOrEmpty()]
    [string]$ModelsDir = (Join-Path $PSScriptRoot "models"),
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
if ($AdapterName -and $PSBoundParameters.ContainsKey("AdapterIndex")) {
    throw "Specify at most one of -AdapterName or -AdapterIndex."
}

$modelsDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ModelsDir)
Assert-SampleArtifactsReady `
    -Sample "onnx-runtime-winmlcgep" `
    -ModelsDirectory $modelsDirectory

$arguments = if ($AdapterName) {
    @("--adapter-name", $AdapterName)
}
else {
    @("--adapter-index", $AdapterIndex)
}
$arguments += @("--models-dir", $modelsDirectory)
if ($Recompile) { $arguments += "--recompile" }

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName onnx-runtime-winmlcgep `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
