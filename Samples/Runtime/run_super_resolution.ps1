# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the SESR 2x super-resolution sample.

.DESCRIPTION
Ensures the pinned SESR model and reference JPEG exist, then runs the
NV12-to-tensor, inference, and tensor-to-image path.

.PARAMETER Device
Requested inference device: cpu, gpu, or npu. Tensorization remains on CPU.

.PARAMETER Ep
Optional installed execution-provider catalog alias to register and pin.

.PARAMETER Output
PNG path for the 512x512 upscaled result.

.PARAMETER Show
Opens the generated PNG with the default desktop viewer.

.PARAMETER Performance
Prefers a discrete/performance GPU adapter.

.PARAMETER Efficiency
Prefers an integrated/efficiency GPU adapter.

.PARAMETER Diagnostics
Enables verbose Runtime and ORT placement diagnostics.

.PARAMETER Platform
Architecture of the previously built executable.

.PARAMETER Configuration
Configuration of the previously built executable.

.EXAMPLE
.\run_super_resolution.ps1 -Device cpu -Output .\super-resolution-output.png

.EXAMPLE
.\run_super_resolution.ps1 -Device gpu -Performance -Diagnostics -Show
#>

[CmdletBinding()]
param(
    [ValidateSet("cpu", "gpu", "npu")]
    [string]$Device = "cpu",
    [string]$Ep,
    [string]$Output = ".\super-resolution-output.png",
    [switch]$Show,
    [switch]$Performance,
    [switch]$Efficiency,
    [switch]$Diagnostics,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
if ($Performance -and $Efficiency) {
    throw "Specify at most one of -Performance or -Efficiency."
}

Assert-SampleArtifactsReady `
    -Sample "super-resolution" `
    -ModelsDirectory (Join-Path $PSScriptRoot "models")

$arguments = @("--device", $Device, "--output", $Output)
if ($Ep) { $arguments += @("--ep", $Ep) }
if ($Show) { $arguments += "--show" }
if ($Performance) { $arguments += "--performance" }
if ($Efficiency) { $arguments += "--efficiency" }
if ($Diagnostics) { $arguments += "--verbose" }

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName webcam-super-resolution `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
