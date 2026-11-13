# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the model-backed text generation sample: published weights, no pipeline
and no tokenizer argument.

.PARAMETER Format
checkpoint uses the cataloged Qwen2.5 0.5B Instruct checkpoint folder; gguf
uses the cataloged Qwen2.5 0.5B Instruct GGUF file.

.PARAMETER ModelPath
A checkpoint folder or GGUF file to use instead of the cataloged model.

.PARAMETER SequenceCapacity
Largest sequence, in tokens, that the session holds. Zero keeps the model's
default.

.PARAMETER ProgramCache
A directory in which sessions keep the programs they compile for the GPU, so
later runs start faster. It can grow larger than the model.

.PARAMETER CacheWeights
Also keep the weights prepared for each program in the program cache. Requires
-ProgramCache.
#>

[CmdletBinding()]
param(
    [ValidateSet("checkpoint", "gguf")]
    [string]$Format = "checkpoint",
    [string]$ModelPath,
    [string]$Prompt = "Write one sentence about the ocean.",
    [ValidateRange(1, 4096)]
    [uint32]$MaxNewTokens = 64,
    [ValidateRange(0, 1048576)]
    [uint64]$SequenceCapacity = 0,
    [string]$ProgramCache,
    [switch]$CacheWeights,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
if ($CacheWeights -and -not $ProgramCache) {
    throw "-CacheWeights requires -ProgramCache."
}
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
$modelsDirectory = Join-Path $PSScriptRoot "models"
if (-not $ModelPath) {
    $sampleId = if ($Format -eq "gguf") {
        "task-model-text-generation-gguf"
    }
    else {
        "task-model-text-generation"
    }
    Assert-SampleArtifactsReady `
        -Sample $sampleId `
        -ModelsDirectory $modelsDirectory `
        -Note "Prompt: `"$Prompt`"." `
        -NonInteractive:$NonInteractive
    $ModelPath = if ($Format -eq "gguf") {
        Join-Path $modelsDirectory "gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf"
    }
    else {
        Join-Path $modelsDirectory "qwen2.5-0.5b-instruct"
    }
}
if (-not (Test-Path -LiteralPath $ModelPath)) {
    throw "Model was not found: $ModelPath"
}

$sampleArguments = @(
    (Resolve-Path -LiteralPath $ModelPath).Path,
    $Prompt,
    $MaxNewTokens.ToString()
)
if ($SequenceCapacity -gt 0) {
    $sampleArguments += @("--capacity", $SequenceCapacity.ToString())
}
if ($ProgramCache) {
    $sampleArguments += @(
        "--program-cache",
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProgramCache)
    )
    if ($CacheWeights) {
        $sampleArguments += "--cache-weights"
    }
}

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName task-model-text-generation `
    -SampleArguments $sampleArguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
