# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the Runtime model-compilation sample.

.DESCRIPTION
Ensures the default SqueezeNet source model exists, then demonstrates file,
memory-sink, zero-copy, or all compile/load storage contracts.

.PARAMETER Mode
Compilation/storage path: file, sink, zerocopy, or all.

.PARAMETER ModelPath
Optional source ONNX model path. Omit it to use the pinned SqueezeNet ONNX
model.

.PARAMETER OutputDirectory
Optional empty directory that retains file-mode artifacts and sidecars.
Without it, file mode uses and cleans a unique temporary directory.

.PARAMETER Device
Execution target used for compilation and the validation run.

.PARAMETER Ep
Optional installed execution-provider catalog alias to register and pin.

.PARAMETER Diagnostics
Enables verbose Runtime and ORT placement diagnostics.

.PARAMETER Platform
Architecture of the previously built executable.

.PARAMETER Configuration
Configuration of the previously built executable.

.EXAMPLE
.\run_model_compilation.ps1 -Device cpu -Mode all

.EXAMPLE
.\run_model_compilation.ps1 -Device gpu -Ep <execution-provider-name> -Mode file -Diagnostics
#>

[CmdletBinding()]
param(
    [ValidateSet("file", "sink", "zerocopy", "all")]
    [string]$Mode = "all",
    [string]$ModelPath,
    [string]$OutputDirectory,
    [ValidateSet("cpu", "gpu", "npu")]
    [string]$Device = "cpu",
    [string]$Ep,
    [switch]$Diagnostics,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force

if ($OutputDirectory -and $Mode -notin @("file", "all")) {
    throw "-OutputDirectory applies only to -Mode file or -Mode all."
}
if ($ModelPath) {
    $extension = [IO.Path]::GetExtension($ModelPath).ToLowerInvariant()
    if ($extension -ne ".onnx") {
        throw "Model compilation requires an ONNX source model."
    }
}

if (-not $ModelPath) {
    Assert-SampleArtifactsReady `
        -Sample "model-compilation" `
        -ModelsDirectory (Join-Path $PSScriptRoot "models")
}

$arguments = @("--device", $Device, "--mode", $Mode)
if ($ModelPath) { $arguments += @("--model", $ModelPath) }
if ($OutputDirectory) { $arguments += @("--output-dir", $OutputDirectory) }
if ($Ep) { $arguments += @("--ep", $Ep) }
if ($Diagnostics) { $arguments += "--verbose" }

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName model-compilation `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
