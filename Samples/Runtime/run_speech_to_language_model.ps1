# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the composed Whisper-to-language-model scenario through ORT or llama.cpp.

.DESCRIPTION
Transcribes a WAV, then sends the transcript to the selected language path.
ORT uses ONNX for both tasks and llama uses ORT Whisper plus GGUF.

.PARAMETER Backend
Execution path: ort or llama.

.PARAMETER ModelPath
Optional ONNX/ORT/GGUF language model path.

.PARAMETER WavPath
Input WAV path. Defaults to the bundled Whisper reference sentence.

.PARAMETER Instruction
Instruction prepended to the transcript before language generation.

.PARAMETER Device
Requested execution target: cpu, gpu, or npu.

.PARAMETER Ep
Optional installed execution-provider catalog alias to register and pin.

.PARAMETER Platform
Architecture of the previously built executable.

.PARAMETER Configuration
Configuration of the previously built executable.

.EXAMPLE
.\run_speech_to_language_model.ps1

.EXAMPLE
.\run_speech_to_language_model.ps1 -WavPath D:\audio\meeting.wav -Instruction "Summarize the transcript."
#>

[CmdletBinding()]
param(
    [ValidateSet("ort", "llama")]
    [string]$Backend = "ort",
    [string]$ModelPath,
    [string]$WavPath,
    [string]$Instruction = "What color is the fox in the transcript? Reply with only the color.",
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

Assert-SampleArtifactsReady `
    -Sample $(if ($ModelPath) { "whisper" } elseif ($Backend -eq "llama") { "llm-chat-gguf" } else { "speech-to-language-model" }) `
    -ModelsDirectory (Join-Path $PSScriptRoot "models") `
    -Note "Backend: $Backend. Device: $Device."

if (-not $WavPath) {
    $WavPath = Join-Path $PSScriptRoot "speech\whisper\test_audio.wav"
}
if (-not (Test-Path -LiteralPath $WavPath)) {
    throw "WAV file was not found: $WavPath"
}

$languageModelSource = if ($ModelPath) {
    $ModelPath
}
elseif ($Backend -eq "llama") {
    Join-Path $PSScriptRoot "models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf"
}
else {
    Join-Path $PSScriptRoot "models\llm\model.onnx"
}
$languageExtension = [IO.Path]::GetExtension($languageModelSource).ToLowerInvariant()
if ($Backend -eq "llama" -and $languageExtension -ne ".gguf") {
    throw "-Backend llama requires a GGUF language model."
}
if ($Backend -eq "ort" -and $languageExtension -notin @(".onnx", ".ort")) {
    throw "-Backend ort requires an ONNX or ORT language model."
}

$arguments = @(
    (Join-Path $PSScriptRoot "models\whisper-medium-q4f16\onnx"),
    (Resolve-Path -LiteralPath $WavPath).Path,
    $languageModelSource,
    $Instruction,
    "--device", $Device
)
if ($Ep) { $arguments += @("--ep", $Ep) }

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName speech-to-language-model `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
