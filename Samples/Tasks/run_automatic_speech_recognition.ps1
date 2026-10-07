# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param(
    [string]$ModelDir,
    [string]$WavPath,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
if (-not $ModelDir) {
    Assert-SampleArtifactsReady `
        -Sample "task-automatic-speech-recognition" `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -NonInteractive:$NonInteractive
    $ModelDir = Join-Path $PSScriptRoot "models\whisper-medium-q4f16\onnx"
}
if (-not $WavPath) {
    $WavPath = Join-Path $PSScriptRoot "..\Runtime\speech\whisper\test_audio.wav"
}

function Resolve-WhisperModel {
    param([string]$Stem)

    $candidate = Join-Path $ModelDir "$Stem.onnx"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }
    throw "Required Whisper model '$Stem' was not found in $ModelDir."
}

$null = Resolve-WhisperModel "encoder_model"
$null = Resolve-WhisperModel "decoder_model"
foreach ($path in @(
    (Join-Path $ModelDir "tokenizer.json"),
    $WavPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required speech recognition input was not found: $path"
    }
}

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName task-automatic-speech-recognition `
    -SampleArguments @(
        (Resolve-Path -LiteralPath $ModelDir).Path,
        (Resolve-Path -LiteralPath $WavPath).Path
    ) `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
