# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param(
    [ValidateSet("ort", "llama")]
    [string]$LanguageBackend,
    [string]$ModelDir,
    [string]$WavPath,
    [string]$LanguageModelPath,
    [string]$TokenizerSource,
    [string]$Instruction = "What color is the fox in the transcript? Reply with only the color.",
    [ValidateRange(1, 4096)]
    [uint32]$MaxNewTokens = 32,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
$modelsDirectory = Join-Path $PSScriptRoot "models"
$LanguageBackend = Resolve-SampleBackend `
    -Scenario "task-speech-to-text-generation-language" `
    -ModelsDirectory $modelsDirectory `
    -Requested $LanguageBackend `
    -NonInteractive:$NonInteractive
if (-not $ModelDir -or -not $LanguageModelPath) {
    Assert-SampleBackendModelPath `
        -Scenario "task-speech-to-text-generation-language" `
        -Backend $LanguageBackend `
        -ModelsDirectory $modelsDirectory `
        -ModelPath $LanguageModelPath
    $sampleId = if ($LanguageBackend -eq "llama") {
        "task-speech-to-text-generation-gguf"
    }
    else {
        "task-speech-to-text-generation"
    }
    Assert-SampleArtifactsReady `
        -Sample $sampleId `
        -ModelsDirectory $modelsDirectory `
        -Note "Language backend: $LanguageBackend." `
        -NonInteractive:$NonInteractive
}
if (-not $ModelDir) {
    $ModelDir = Join-Path $PSScriptRoot "models\whisper-medium-q4f16\onnx"
}
if (-not $LanguageModelPath) {
    $LanguageModelPath = switch ($LanguageBackend) {
        "llama" { Join-Path $PSScriptRoot "models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf" }
        default { Join-Path $PSScriptRoot "models\llm\task_model.onnx" }
    }
}
if (-not $WavPath) {
    $WavPath = Join-Path $PSScriptRoot "..\Runtime\speech\whisper\test_audio.wav"
}

$extension = [IO.Path]::GetExtension($LanguageModelPath).ToLowerInvariant()
if ($LanguageBackend -eq "llama" -and $extension -ne ".gguf") {
    throw "The llama backend requires a GGUF model."
}
if ($LanguageBackend -eq "ort" -and $extension -notin @(".onnx", ".ort")) {
    throw "The ort backend requires an ONNX or ORT model."
}
if ($extension -eq ".gguf") {
    $resolvedTokenizer = "-"
}
else {
    if (-not $TokenizerSource) {
        $TokenizerSource = Split-Path -Parent $LanguageModelPath
    }
    $resolvedTokenizer = (Resolve-Path -LiteralPath $TokenizerSource).Path
}

function Resolve-WhisperModel {
    param([string]$Stem)

    $candidate = Join-Path $ModelDir "$Stem.onnx"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }
    throw "Required Whisper model '$Stem' was not found in $ModelDir."
}

$null = Resolve-WhisperModel "encoder_model"
$null = Resolve-WhisperModel "decoder_model"
foreach ($path in @(
    (Join-Path $ModelDir "tokenizer.json"),
    $LanguageModelPath,
    $WavPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Task composition input was not found: $path"
    }
}

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName task-speech-to-text-generation `
    -SampleArguments @(
        (Resolve-Path -LiteralPath $ModelDir).Path,
        (Resolve-Path -LiteralPath $WavPath).Path,
        (Resolve-Path -LiteralPath $LanguageModelPath).Path,
        $resolvedTokenizer,
        $Instruction,
        $MaxNewTokens.ToString(),
        $LanguageBackend
    ) `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
