# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the composed text generation sample: the app loads the exported Qwen ONNX
decoder and its tokenizer and describes them as a UNIFIED composition.

.PARAMETER ModelPath
The exported task_model.onnx to use instead of the prepared one.

.PARAMETER TokenizerPath
The tokenizer.json of the model. Defaults to the one beside the model.

.PARAMETER Device
Execution target of the composition: cpu or gpu.
#>

[CmdletBinding()]
param(
    [string]$ModelPath,
    [string]$TokenizerPath,
    [string]$Prompt = "Write one sentence about the ocean.",
    [ValidateRange(1, 4096)]
    [uint32]$MaxNewTokens = 64,
    [ValidateSet("cpu", "gpu")]
    [string]$Device = "cpu",
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
        -Sample "task-composed-text-generation" `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -Note "Device: $Device. Prompt: `"$Prompt`"." `
        -NonInteractive:$NonInteractive
    $ModelPath = Join-Path $PSScriptRoot "models\llm\task_model.onnx"
}
if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
    throw "Model was not found: $ModelPath"
}
# A bare file name has no parent path, so resolve it before looking beside it.
$ModelPath = (Resolve-Path -LiteralPath $ModelPath).Path
if (-not $TokenizerPath) {
    $TokenizerPath = Join-Path (Split-Path -Parent $ModelPath) "tokenizer.json"
}
if (-not (Test-Path -LiteralPath $TokenizerPath -PathType Leaf)) {
    throw "Tokenizer was not found: $TokenizerPath"
}

$sampleArguments = @(
    $ModelPath,
    (Resolve-Path -LiteralPath $TokenizerPath).Path,
    $Prompt,
    $MaxNewTokens.ToString(),
    "--device",
    $Device
)

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName task-composed-text-generation `
    -SampleArguments $sampleArguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
