# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the minimal ONNX or GGUF LanguageModel sample.

.DESCRIPTION
Acquires the selected language assets and streams one response. Both backends
use one decoder stage from a unified model.

.PARAMETER Backend
Language execution path: ort loads the unified ONNX decoder through ONNX
Runtime; llama loads the pinned GGUF decoder.

.PARAMETER ModelPath
Optional ONNX/ORT/GGUF model path.

.PARAMETER TokenizerSource
Optional tokenizer directory/file override.

.PARAMETER Device
Requested execution target: cpu, gpu, or npu.

.PARAMETER Ep
Optional installed execution-provider catalog alias to register and pin.

.PARAMETER Prompt
Text submitted to the model. Defaults to a short greeting request.

.PARAMETER Raw
Skips the model chat template and submits the prompt text directly.

.PARAMETER Platform
Architecture of the previously built executable.

.PARAMETER Configuration
Configuration of the previously built executable.

.EXAMPLE
.\run_hello_language_model.ps1

.EXAMPLE
.\run_hello_language_model.ps1 -Prompt "The sky is" -Raw
#>

[CmdletBinding()]
param(
    [ValidateSet("ort", "llama")]
    [string]$Backend = "ort",
    [string]$ModelPath,
    [string]$TokenizerSource,
    [string]$Prompt = "Reply with one short greeting.",
    [switch]$Raw,
    [ValidateSet("cpu", "gpu", "npu")]
    [string]$Device = "cpu",
    [string]$Ep,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force

. (Join-Path $PSScriptRoot "scripts\validate_backend_request.ps1")
Assert-SampleBackendRequest -Backend $Backend -Device $Device -Ep $Ep -BundledLanguageModel:(-not $ModelPath)

if (-not $ModelPath) {
    Assert-SampleArtifactsReady `
        -Sample $(if ($Backend -eq "llama") { "hello-language-model-gguf" } else { "hello-language-model" }) `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -Note "Backend: $Backend. Device: $Device."
}

$arguments = @("--prompt", $Prompt, "--device", $Device)
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
if ($Ep) { $arguments += @("--ep", $Ep) }
if ($TokenizerSource) { $arguments += @("--tokenizer", $TokenizerSource) }
if ($Raw) { $arguments += "--raw" }

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName hello-language-model `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
