# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the SqueezeNet image-classification sample.

.DESCRIPTION
Ensures the pinned SqueezeNet assets exist, locates the built executable, and
runs classification on the requested Runtime device/provider.

.PARAMETER Device
Requested execution device: cpu, gpu, or npu.

.PARAMETER Ep
Optional installed execution-provider catalog alias to register and pin.

.PARAMETER Performance
Prefers a discrete/performance GPU adapter when multiple adapters exist.

.PARAMETER Efficiency
Prefers an integrated/efficiency GPU adapter when multiple adapters exist.

.PARAMETER Diagnostics
Enables verbose Runtime and ORT placement diagnostics.

.PARAMETER Platform
Architecture of the previously built executable.

.PARAMETER Configuration
Configuration of the previously built executable.

.EXAMPLE
.\run_image_classification.ps1 -Device cpu

.EXAMPLE
.\run_image_classification.ps1 -Device gpu -Ep <execution-provider-name> -Diagnostics
#>

[CmdletBinding()]
param(
    [ValidateSet("cpu", "gpu", "npu")]
    [string]$Device = "cpu",
    [string]$Ep,
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
    -Sample "image-classification" `
    -ModelsDirectory (Join-Path $PSScriptRoot "models")

$arguments = @("--device", $Device)
if ($Ep) { $arguments += @("--ep", $Ep) }
if ($Performance) { $arguments += "--performance" }
if ($Efficiency) { $arguments += "--efficiency" }
if ($Diagnostics) { $arguments += "--verbose" }

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName image-classification `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
