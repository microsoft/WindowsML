# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the llama.cpp text generation sample: the app loads a GGUF file through
the llama.cpp model loader, optionally with a draft model for speculative
decoding.

.PARAMETER ModelPath
A GGUF file to use instead of the cataloged Qwen2.5 0.5B Instruct file.

.PARAMETER DraftModel
A smaller GGUF model that shares the model's tokenizer. It proposes draft
tokens that the model verifies.

.PARAMETER Device
Execution target: cpu, or gpu when a llama.cpp GPU backend is deployed.

.PARAMETER Language
cpp runs the C++ sample; csharp runs the C# sample in
model-backed\text-generation\csharp with --llama. Build it first with
.\build.ps1 -Sample model-text-generation-csharp.
#>

[CmdletBinding()]
param(
    [string]$ModelPath,
    [string]$DraftModel,
    [string]$Prompt = "Write one sentence about the ocean.",
    [ValidateRange(1, 4096)]
    [uint32]$MaxNewTokens = 64,
    [ValidateSet("cpu", "gpu")]
    [string]$Device = "cpu",
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("cpp", "csharp")]
    [string]$Language = "cpp",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
if (-not $ModelPath) {
    Assert-SampleArtifactsReady `
        -Sample "task-llama-text-generation" `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -Note "Device: $Device. Prompt: `"$Prompt`"." `
        -NonInteractive:$NonInteractive
    $ModelPath = Join-Path $PSScriptRoot "models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf"
}
if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
    throw "Model was not found: $ModelPath"
}

$sampleArguments = @(
    (Resolve-Path -LiteralPath $ModelPath).Path,
    $Prompt,
    $MaxNewTokens.ToString(),
    "--device",
    $Device.ToLowerInvariant()
)
if ($DraftModel) {
    if (-not (Test-Path -LiteralPath $DraftModel -PathType Leaf)) {
        throw "Draft model was not found: $DraftModel"
    }
    $sampleArguments += @("--draft-model", (Resolve-Path -LiteralPath $DraftModel).Path)
}

# The C# sample covers the llama.cpp route of this sample behind --llama.
$projectName = "task-llama-text-generation"
if ($Language -eq "csharp") {
    $projectName = "task-model-text-generation-csharp"
    $sampleArguments = @("--llama") + $sampleArguments
}

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName $projectName `
    -SampleArguments $sampleArguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
