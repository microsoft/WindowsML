# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the C# text generation features sample: continuation, constrained JSON
output, speculative decoding, watermarking, and segmented prompts on a GGUF
file that the llama.cpp model loader loads for the CPU.

.PARAMETER Scenario
all runs every scenario; continuation, constraint, speculative, watermark, or
segments runs one.

.PARAMETER ModelPath
A GGUF file to use instead of the cataloged Qwen2.5 0.5B Instruct file.

.NOTES
Build the sample first with
.\build.ps1 -Sample text-generation-features-csharp.
#>

[CmdletBinding()]
param(
    [ValidateSet("all", "continuation", "constraint", "speculative", "watermark", "segments")]
    [string]$Scenario = "all",
    [string]$ModelPath,
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
        -Sample "task-llama-text-generation" `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -Note "Scenario: $Scenario." `
        -NonInteractive:$NonInteractive
    $ModelPath = Join-Path $PSScriptRoot "models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf"
}
if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
    throw "Model was not found: $ModelPath"
}

$sampleArguments = @(
    (Resolve-Path -LiteralPath $ModelPath).Path,
    $Scenario.ToLowerInvariant()
)

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName "task-text-generation-features-csharp" `
    -SampleArguments $sampleArguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
