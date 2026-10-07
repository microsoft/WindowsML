# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Exports ORT language artifacts for Hello LanguageModel and LLM chat.

.DESCRIPTION
Creates an isolated Python 3.11 environment and runs export_llm.py. The export
contains model.onnx for unified mode; emb.onnx, decoder.onnx, and head.onnx for
split mode; tokenizer assets; and related Task sample artifacts.

.PARAMETER Model
Hugging Face model ID or local compatible dense decoder. Defaults to
Qwen/Qwen2.5-0.5B-Instruct.

.PARAMETER Revision
Pinned Hugging Face model revision. The curated default is immutable.

.PARAMETER MaxSeq
Fixed Runtime sequence/KV capacity used by the generated artifacts.

.PARAMETER Dtype
Export precision: fp16 or fp32.

.PARAMETER OutputDir
Destination directory. Defaults to Samples\Runtime\models\llm.

.EXAMPLE
.\language\llm-chat\export_model.ps1

.EXAMPLE
.\language\llm-chat\export_model.ps1 -MaxSeq 4096 -Dtype fp16 -OutputDir .\models\qwen
#>
#
# Export a decoder model as fixed-dimension ONNX artifacts for the Runtime
# language samples. Sets up an isolated Python 3.11 environment under a short
# per-user tool path with uv and runs export_llm.py.
#
# Defaults to Qwen2.5-0.5B-Instruct: ungated (no Hugging Face login) and small.
# Override -Model for a compatible dense decoder.
#
# Notes:
#   - Downloads use the Xet backend (hf_xet). For gated models, authenticate
#     first: set HF_TOKEN or run `huggingface-cli login`. The default
#     Qwen2.5-0.5B is ungated.
#   - GPU is the practical inference path; CPU fp16 inference of multi-GB models
#     is very slow and memory-bound (~16 GB RAM is marginal for a 7.6 GB model).
#
# Usage:
#   .\language\llm-chat\export_model.ps1                                  # Qwen2.5-0.5B -> models\llm
#   .\language\llm-chat\export_model.ps1 -Model microsoft/Phi-3.5-mini-instruct
#   .\language\llm-chat\export_model.ps1 -MaxSeq 4096 -OutputDir .\models\my-llm

[CmdletBinding()]
param(
    [string]$Model = "Qwen/Qwen2.5-0.5B-Instruct",
    [string]$Revision,
    [int]$MaxSeq = 128,
    [ValidateSet("fp16", "fp32")]
    [string]$Dtype = "fp16",
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

if (
    -not $PSBoundParameters.ContainsKey("Revision") -and
    $Model -eq "Qwen/Qwen2.5-0.5B-Instruct"
) {
    $Revision = "7ae557604adf67be50417f59c2c2f167def9a775"
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $PSScriptRoot "..\..\models\llm"
}

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    throw "uv is required to export the LLM but is not installed. Install a trusted version of uv from https://docs.astral.sh/uv/getting-started/installation/, then rerun this script."
}

# Keep Torch and exporter dependency paths short. A repository-local venv can
# exceed Windows tool/path limits in deep PR worktrees.
if (-not $env:LOCALAPPDATA) {
    throw "LOCALAPPDATA is required to create the LLM export environment."
}
$venvDir =
    Join-Path $env:LOCALAPPDATA "WinMLSampleTools\llm-export-py311-v1"
if (-not (Test-Path "$venvDir\Scripts\python.exe")) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $venvDir) | Out-Null
    Write-Host "Creating Python 3.11 environment..." -ForegroundColor Cyan
    & uv venv --python 3.11 $venvDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to create venv (uv will download Python 3.11 if needed)" }
} else {
    Write-Host "Using existing environment: $venvDir" -ForegroundColor Green
}

Write-Host "Ensuring exporter dependencies..." -ForegroundColor Cyan
$python = Join-Path $venvDir "Scripts\python.exe"
& uv pip install --python $python `
    "torch==2.13.0" `
    "transformers==5.15.0" `
    "onnx==1.22.0" `
    "onnxscript==0.7.1" `
    "onnxruntime==1.29.0" `
    "huggingface_hub==1.27.0" `
    "hf_xet==1.6.0"
if ($LASTEXITCODE -ne 0) { throw "Failed to install dependencies" }

$exportScript = Join-Path $PSScriptRoot "export_llm.py"
$driverSha256 =
    (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "Exporting $Model (max-seq $MaxSeq, $Dtype)..." -ForegroundColor Cyan
Write-Host "  Output: $OutputDir"
$exportArguments = @(
    $exportScript,
    "--model", $Model,
    "--output", $OutputDir,
    "--max-seq", $MaxSeq,
    "--dtype", $Dtype,
    "--driver-sha256", $driverSha256,
    "--verify"
)
if ($Revision) {
    $exportArguments += @("--revision", $Revision)
}
& $python @exportArguments
if ($LASTEXITCODE -ne 0) { throw "Export failed (exit $LASTEXITCODE)." }

Write-Host "Export complete: $OutputDir" -ForegroundColor Green
