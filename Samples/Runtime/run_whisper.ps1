# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs deterministic Whisper speech-to-text inference.

.DESCRIPTION
Ensures Whisper-medium Q4F16 assets exist, selects a WAV file, sets the model directory
for the executable, and runs transcription on the requested device/provider.

.PARAMETER ModelDir
Directory containing encoder_model.onnx, decoder_model.onnx, and vocab.json.
Defaults to the acquired Whisper-medium Q4F16 assets.

.PARAMETER WavPath
Input WAV path. Defaults to the bundled reference sentence.

.PARAMETER Device
Requested execution device: cpu, gpu, or npu.

.PARAMETER Ep
Optional installed execution-provider catalog alias to register and pin.

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
.\run_whisper.ps1 -Device cpu

.EXAMPLE
.\run_whisper.ps1 -WavPath D:\audio\sample.wav -Device gpu -Ep <execution-provider-name> -Diagnostics
#>

[CmdletBinding()]
param(
    [string]$ModelDir,
    [string]$WavPath,
    [ValidateSet("cpu", "gpu", "npu")]
    [string]$Device = "cpu",
    [string]$Ep,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Performance,
    [switch]$Efficiency,
    [switch]$Diagnostics
)

if ($Performance -and $Efficiency) {
    throw "Specify at most one of -Performance or -Efficiency."
}

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force

if (-not $ModelDir) {
    Assert-SampleArtifactsReady `
        -Sample "whisper" `
        -ModelsDirectory (Join-Path $PSScriptRoot "models")

    $ModelDir = Join-Path $PSScriptRoot "models\whisper-medium-q4f16\onnx"
}

foreach ($name in @("encoder_model.onnx", "decoder_model.onnx", "vocab.json")) {
    if (-not (Test-Path -LiteralPath (Join-Path $ModelDir $name))) {
        throw "ModelDir is missing ${name}: $ModelDir"
    }
}
$env:WINML_WHISPER_MODEL_DIR = (Resolve-Path -LiteralPath $ModelDir).Path

if (-not $WavPath) {
    $WavPath = Join-Path $PSScriptRoot "speech\whisper\test_audio.wav"
}
if (-not (Test-Path -LiteralPath $WavPath)) {
    throw "WAV file was not found: $WavPath"
}

$arguments = @($WavPath, "--device", $Device)
if ($Ep) { $arguments += @("--ep", $Ep) }
if ($Performance) { $arguments += "--performance" }
if ($Efficiency) { $arguments += "--efficiency" }
if ($Diagnostics) { $arguments += "--verbose" }

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName whisper-speech-to-text `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
