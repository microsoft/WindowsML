# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param(
    [ValidateSet("ort", "llama")]
    [string]$Backend,
    [string]$ModelPath,
    [string]$TokenizerSource,
    [string]$Prompt = "Name three primary colors.",
    [string]$FollowUp = "Which of those is the color of the sky?",
    [ValidateRange(1, 4096)]
    [uint32]$MaxNewTokens = 48,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
# The chat sample uses the text-generation sample's models.
$modelsDirectory = Join-Path $PSScriptRoot "models"
$Backend = Resolve-SampleBackend `
    -Scenario "task-chat-completion" `
    -ModelsDirectory $modelsDirectory `
    -Requested $Backend `
    -NonInteractive:$NonInteractive
if (-not $ModelPath) {
    Assert-SampleBackendModelPath `
        -Scenario "task-chat-completion" `
        -Backend $Backend `
        -ModelsDirectory $modelsDirectory `
        -ModelPath $ModelPath
    $sampleId = if ($Backend -eq "llama") { "task-text-generation-gguf" } else { "task-text-generation" }
    Assert-SampleArtifactsReady `
        -Sample $sampleId `
        -ModelsDirectory $modelsDirectory `
        -Note "Backend: $Backend." `
        -NonInteractive:$NonInteractive

    $ModelPath = if ($Backend -eq "llama") {
        Join-Path $modelsDirectory "gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf"
    }
    else {
        Join-Path $modelsDirectory "llm\task_model.onnx"
    }
}
if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
    throw "Language model was not found: $ModelPath"
}

$extension = [IO.Path]::GetExtension($ModelPath).ToLowerInvariant()
if ($Backend -eq "llama" -and $extension -ne ".gguf") {
    throw "The llama backend requires a GGUF model."
}
if ($Backend -eq "ort" -and $extension -notin @(".onnx", ".ort")) {
    throw "The ort backend requires an ONNX or ORT model."
}

# GGUF files carry their own tokenizer; ONNX models read it from their folder.
$resolvedTokenizer = "-"
if ($Backend -ne "llama") {
    if (-not $TokenizerSource) {
        $TokenizerSource = Split-Path -Parent $ModelPath
    }
    if (-not (Test-Path -LiteralPath $TokenizerSource)) {
        throw "Tokenizer source was not found: $TokenizerSource"
    }
    $resolvedTokenizer = (Resolve-Path -LiteralPath $TokenizerSource).Path
}

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName task-chat-completion `
    -SampleArguments @(
        (Resolve-Path -LiteralPath $ModelPath).Path,
        $resolvedTokenizer,
        $Prompt,
        $FollowUp,
        $MaxNewTokens.ToString()
    ) `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
