# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param(
    [ValidateSet("ort", "llama", "hybrid-ort")]
    [string]$Backend,
    [string]$ModelPath,
    [string]$TokenizerSource,
    [string]$Prompt,
    [ValidateRange(1, 4096)]
    [uint32]$MaxNewTokens = 32,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Chat,
    [ValidateRange(0.0, 5.0)]
    [double]$Temperature = -1,
    [ValidateRange(0.0, 1.0)]
    [double]$TopP = -1,
    [ValidateRange(0, 1000)]
    [int]$TopK = -1,
    [ValidateRange(0.0, 5.0)]
    [double]$RepetitionPenalty = -1,
    [long]$Seed = -1,
    [switch]$Greedy,
    [ValidateSet("cpu", "gpu")]
    [string]$Device = "cpu",
    [ValidateSet("none", "model", "draft-model", "prompt-lookup")]
    [string]$Speculative = "none",
    [ValidateRange(1, 16)]
    [int]$DraftTokens = -1,
    [string]$DraftModel,
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
if (-not $PSBoundParameters.ContainsKey("Prompt")) {
    $Prompt = "The sky is often"
}
$Backend = Resolve-SampleBackend `
    -Scenario "task-text-generation" `
    -ModelsDirectory (Join-Path $PSScriptRoot "models") `
    -Requested $Backend `
    -NonInteractive:$NonInteractive
if (-not $ModelPath) {
    Assert-SampleBackendModelPath `
        -Scenario "task-text-generation" `
        -Backend $Backend `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -ModelPath $ModelPath
    $sampleId = if ($Backend -eq "llama") {
        "task-text-generation-gguf"
    }
    else {
        "task-text-generation"
    }
    Assert-SampleArtifactsReady `
        -Sample $sampleId `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -Note "Backend: $Backend. Prompt: `"$Prompt`"." `
        -NonInteractive:$NonInteractive

    $ModelPath = switch ($Backend) {
        "llama" { Join-Path $PSScriptRoot "models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf" }
        "hybrid-ort" { Join-Path $PSScriptRoot "models\llm" }
        default { Join-Path $PSScriptRoot "models\llm\task_model.onnx" }
    }
}
if (-not (Test-Path -LiteralPath $ModelPath)) {
    throw "Language model was not found: $ModelPath"
}

$extension = if (Test-Path -LiteralPath $ModelPath -PathType Leaf) {
    [IO.Path]::GetExtension($ModelPath).ToLowerInvariant()
}
else {
    ""
}
if ($Backend -eq "llama" -and $extension -ne ".gguf") {
    throw "The llama backend requires a GGUF model."
}
if ($Backend -eq "ort" -and
    $extension -notin @(".onnx", ".ort")) {
    throw "The ort backend requires an ONNX or ORT model."
}
if ($Backend -eq "hybrid-ort" -and
    -not (Test-Path -LiteralPath $ModelPath -PathType Container)) {
    throw "The hybrid-ort backend requires a prepared model directory."
}
if ($Backend -eq "llama") {
    $resolvedTokenizer = "-"
}
else {
    if (-not $TokenizerSource) {
        $TokenizerSource = if (Test-Path -LiteralPath $ModelPath -PathType Container) {
            $ModelPath
        }
        else {
            Split-Path -Parent $ModelPath
        }
    }
    if (-not (Test-Path -LiteralPath $TokenizerSource)) {
        throw "Tokenizer source was not found: $TokenizerSource"
    }
    $resolvedTokenizer = (Resolve-Path -LiteralPath $TokenizerSource).Path
}

$sampleArguments = @(
    (Resolve-Path -LiteralPath $ModelPath).Path,
    $resolvedTokenizer,
    $Prompt,
    $MaxNewTokens.ToString(),
    $Backend
)
# Sampling. The sample samples by default so a small model does not repeat
# itself; pass -Greedy for deterministic, reproducible output.
$culture = [Globalization.CultureInfo]::InvariantCulture
if ($Temperature -ge 0) { $sampleArguments += @("--temperature", $Temperature.ToString($culture)) }
if ($TopP -ge 0) { $sampleArguments += @("--top-p", $TopP.ToString($culture)) }
if ($TopK -ge 0) { $sampleArguments += @("--top-k", $TopK.ToString($culture)) }
if ($RepetitionPenalty -ge 0) { $sampleArguments += @("--repetition-penalty", $RepetitionPenalty.ToString($culture)) }
if ($Seed -ge 0) { $sampleArguments += @("--seed", $Seed.ToString($culture)) }
if ($Greedy) { $sampleArguments += "--greedy" }
# An instruct model ends its reply on its own only when the prompt uses its chat
# format. -Chat has the sample apply the model's chat template.
if ($Chat) { $sampleArguments += "--chat" }
if ($Device -ne "cpu") { $sampleArguments += @("--device", $Device) }
# Speculative decoding applies to GGUF models on the llama backend. With
# -Speculative model, -DraftModel names an optional block draft model trained
# for the target; with -Speculative draft-model, it names the required draft.
if ($Speculative -ne "none") { $sampleArguments += @("--speculative", $Speculative) }
if ($DraftTokens -ge 0) { $sampleArguments += @("--draft-tokens", $DraftTokens.ToString($culture)) }
if ($DraftModel) {
    if (-not (Test-Path -LiteralPath $DraftModel -PathType Leaf)) {
        throw "Draft model was not found: $DraftModel"
    }
    $sampleArguments += @("--draft-model", (Resolve-Path -LiteralPath $DraftModel).Path)
}

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName task-text-generation `
    -SampleArguments $sampleArguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
