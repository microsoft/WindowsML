# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the model-backed speech recognition sample: published Whisper tiny
weights and their tokenizer transcribe a WAV file.

.PARAMETER ModelPath
A speech model folder to use instead of the cataloged Whisper tiny folder.

.PARAMETER TokenizerPath
The model's tokenizer.json. Defaults to the one in the model folder.

.PARAMETER AudioPath
A PCM WAV file of at most 30 seconds. Defaults to the Runtime samples' test clip.

.PARAMETER Language
Language tag, such as en. Omit for the model's default.
#>

[CmdletBinding()]
param(
    [string]$ModelPath,
    [string]$TokenizerPath,
    [string]$AudioPath,
    [string]$Language,
    [ValidateSet("transcribe", "translate")]
    [string]$Mode = "transcribe",
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
if (-not $ModelPath) {
    Assert-SampleArtifactsReady `
        -Sample "task-model-speech-recognition" `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -Note "Mode: $Mode." `
        -NonInteractive:$NonInteractive
    $ModelPath = Join-Path $PSScriptRoot "models\whisper-tiny"
}
if (-not (Test-Path -LiteralPath $ModelPath -PathType Container)) {
    throw "Speech model folder was not found: $ModelPath"
}
if (-not $TokenizerPath) {
    $TokenizerPath = Join-Path $ModelPath "tokenizer.json"
}
if (-not $AudioPath) {
    $AudioPath = Join-Path $PSScriptRoot "..\Runtime\speech\whisper\test_audio.wav"
}
foreach ($path in @($TokenizerPath, $AudioPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "File was not found: $path"
    }
}

$sampleArguments = @(
    (Resolve-Path -LiteralPath $ModelPath).Path,
    (Resolve-Path -LiteralPath $TokenizerPath).Path,
    (Resolve-Path -LiteralPath $AudioPath).Path,
    "--mode",
    $Mode
)
if ($Language) {
    $sampleArguments += @("--language", $Language)
}

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName task-model-speech-recognition `
    -SampleArguments $sampleArguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
