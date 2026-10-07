# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs a unified language model or a split ONNX model.

.DESCRIPTION
unified loads one ONNX/ORT or GGUF decoder. split runs the
embedding/decoder/head ONNX stages through ORT. Omit Prompt for interactive
conversation.

.PARAMETER Backend
Execution path: ort or llama for unified models; split models always use ort.

.PARAMETER ModelPath
Optional unified ONNX/ORT/GGUF model path for unified mode.

.PARAMETER TokenizerSource
Optional tokenizer directory/file override for unified mode.

.PARAMETER Mode
unified loads one decoder stage. split loads the embedding, decoder, and
language-model head stages.

.PARAMETER Device
Requested decoder device: cpu, gpu, or npu. Use -Device gpu with -Backend llama
only when a compatible llama.cpp GPU module is installed; otherwise use
-Backend ort for GPU.

.PARAMETER Ep
Optional installed execution-provider catalog alias to register and pin.

.PARAMETER Prompt
One-shot prompt. Omit for interactive chat with reset and quit commands.

.PARAMETER MaxTokens
Maximum newly generated tokens for each response.

.PARAMETER ContextCapacity
Optional sequence-capacity hint for unified models. Set this to request a
smaller sequence capacity than the artifact default. The prompt plus generated
tokens must fit. Zero uses the capacity the model's state declares, or the
backend default. Not valid for split mode.

.PARAMETER Raw
Skips the model chat template.

.PARAMETER NoStream
Uses the Runtime pull/token-stream path and prints the completed response after
generation. Applies to unified mode only.

.PARAMETER Diagnostics
Enables verbose Runtime and ORT placement diagnostics.

.PARAMETER Platform
Architecture of the previously built executable.

.PARAMETER Configuration
Configuration of the previously built executable.

.EXAMPLE
.\run_llm_chat.ps1 -Mode unified -Device cpu -Prompt "Name three primary colors."

.EXAMPLE
.\run_llm_chat.ps1 -Backend llama -Mode unified -Device cpu -MaxTokens 64

.EXAMPLE
.\run_llm_chat.ps1 -Backend llama -Mode unified -Device cpu `
  -ModelPath D:\models\model-00001-of-00003.gguf `
  -ContextCapacity 64 -MaxTokens 16

.EXAMPLE
.\run_llm_chat.ps1 -Backend ort -Mode unified -Device gpu -MaxTokens 64

.EXAMPLE
.\run_llm_chat.ps1 -Backend ort -Mode split -Device gpu -MaxTokens 64
#>

[CmdletBinding()]
param(
    [ValidateSet("ort", "llama")]
    [string]$Backend = "ort",
    [ValidateSet("unified", "split")]
    [string]$Mode = "unified",
    [string]$ModelPath,
    [string]$TokenizerSource,
    [ValidateSet("cpu", "gpu", "npu")]
    [string]$Device = "cpu",
    [string]$Ep,
    [string]$Prompt,
    [ValidateRange(1, 4096)]
    [int]$MaxTokens = 256,
    [ValidateRange(0, 1048576)]
    [int]$ContextCapacity = 0,
    [switch]$Raw,
    [switch]$NoStream,
    [switch]$Diagnostics,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force

. (Join-Path $PSScriptRoot "scripts\validate_backend_request.ps1")
Assert-SampleBackendRequest -Backend $Backend -Device $Device -Ep $Ep -BundledLanguageModel:(-not $ModelPath)

$splitMode = $Mode -eq "split"
if ($Backend -eq "llama") {
    if ($splitMode) {
        throw "-Backend llama supports unified mode, not split mode."
    }
}
if ($splitMode -and $NoStream) {
    throw "NoStream applies to unified mode, not split mode."
}
if ($splitMode -and $ContextCapacity -gt 0) {
    throw "ContextCapacity applies to unified mode, not split mode."
}
if ($splitMode -and ($ModelPath -or $TokenizerSource)) {
    throw "Split mode uses the acquired split ONNX export; ModelPath and TokenizerSource are not supported."
}

if ($splitMode -or -not $ModelPath) {
    Assert-SampleArtifactsReady `
        -Sample $(if ($Backend -eq "llama") { "llm-chat-gguf" } else { "llm-chat" }) `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -Note "Backend: $Backend. Device: $Device."
}

$arguments = @(
    "--mode", $Mode,
    "--device", $Device,
    "--max-tokens", $MaxTokens
)
if ($splitMode) {
    $arguments += @("--model-dir", (Join-Path $PSScriptRoot "models\llm"))
}
else {
    if ($ContextCapacity -gt 0) {
        $arguments += @("--context", $ContextCapacity)
    }
    $model = if ($ModelPath) {
        $ModelPath
    }
    elseif ($Backend -eq "llama") {
        Join-Path $PSScriptRoot "models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf"
    }
    else {
        Join-Path $PSScriptRoot "models\llm\model.onnx"
    }
    $extension = [IO.Path]::GetExtension($model).ToLowerInvariant()
    if ($Backend -eq "llama" -and $extension -ne ".gguf") {
        throw "-Backend llama requires a GGUF model."
    }
    if ($Backend -eq "ort" -and $extension -notin @(".onnx", ".ort")) {
        throw "-Backend ort requires an ONNX or ORT model."
    }
    $arguments += @("--model", $model)
    if ($TokenizerSource) { $arguments += @("--tokenizer", $TokenizerSource) }
}
if ($Ep) { $arguments += @("--ep", $Ep) }
if ($Prompt) { $arguments += @("--prompt", $Prompt) }
if ($Raw) { $arguments += "--raw" }
if ($NoStream) { $arguments += "--no-stream" }
if ($Diagnostics) { $arguments += "--verbose" }

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName llm-chat `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
