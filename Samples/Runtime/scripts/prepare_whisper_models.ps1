# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$contractVersion = 1

$marker = Join-Path $OutputDirectory ".fixed-shapes"
$encoder = Join-Path $OutputDirectory "encoder_model.onnx"
$decoder = Join-Path $OutputDirectory "decoder_model.onnx"
$stagedMarker = Join-Path $OutputDirectory ".fixed-shapes.staged"

$tool = Join-Path (Split-Path $PSScriptRoot -Parent) "tools\fix_onnx_dimensions.py"
$sourceEncoder = Join-Path $SourceDirectory "encoder_model.onnx"
$sourceDecoder = Join-Path $SourceDirectory "decoder_model.onnx"
$preparedEncoder = Join-Path $OutputDirectory "encoder_model.fixed.onnx"
$preparedDecoder = Join-Path $OutputDirectory "decoder_model.fixed.onnx"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if (-not (Test-Path -LiteralPath $sourceEncoder) -or
    -not (Test-Path -LiteralPath $sourceDecoder)) {
    throw "Pinned Whisper source models are missing from $SourceDirectory."
}

function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "").ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

if ((Test-Path -LiteralPath $marker) -and
    (Test-Path -LiteralPath $encoder) -and
    (Test-Path -LiteralPath $decoder)) {
    try {
        $record = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($record.contractVersion -eq $contractVersion -and
            $record.sourceEncoderSha256 -eq (Get-Sha256 $sourceEncoder) -and
            $record.sourceDecoderSha256 -eq (Get-Sha256 $sourceDecoder) -and
            $record.encoderSha256 -eq (Get-Sha256 $encoder) -and
            $record.decoderSha256 -eq (Get-Sha256 $decoder)) {
            exit 0
        }
    }
    catch {
    }
    Remove-Item -LiteralPath $marker -Force
}
if ((Test-Path -LiteralPath $stagedMarker) -and
    (-not (Test-Path -LiteralPath $preparedEncoder) -or
     -not (Test-Path -LiteralPath $preparedDecoder))) {
    Remove-Item -LiteralPath $stagedMarker -Force
}

. (Join-Path $PSScriptRoot "resolve_onnx_python.ps1")
$python = Get-OnnxPython

function Invoke-Fixer {
    param([string[]]$Arguments)

    & $python $tool @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to prepare a fixed-shape Whisper ONNX model."
    }
}

if (-not (Test-Path -LiteralPath $stagedMarker)) {
    Remove-Item -LiteralPath $preparedEncoder, $preparedDecoder -Force -ErrorAction SilentlyContinue

    Invoke-Fixer @(
        "--input", $sourceEncoder,
        "--output", $preparedEncoder,
        "--dim", "batch_size=1"
    )

    Invoke-Fixer @(
        "--input", $sourceDecoder,
        "--output", $preparedDecoder,
        "--dim", "batch_size=1",
        "--dim", "decoder_sequence_length=128",
        "--dim", "decoder_sequence_length + 1=129",
        "--dim", "encoder_sequence_length / 2=1500"
    )

    Set-Content -LiteralPath $stagedMarker -Value "Prepared encoder and decoder are ready." -Encoding ASCII
}

if (Test-Path -LiteralPath $preparedEncoder) {
    Move-Item -LiteralPath $preparedEncoder -Destination $encoder -Force
}
if (Test-Path -LiteralPath $preparedDecoder) {
    Move-Item -LiteralPath $preparedDecoder -Destination $decoder -Force
}
if (-not (Test-Path -LiteralPath $encoder) -or -not (Test-Path -LiteralPath $decoder)) {
    throw "Prepared Whisper model files are incomplete."
}

$record = [ordered]@{
    contractVersion = $contractVersion
    sourceEncoderSha256 = Get-Sha256 $sourceEncoder
    sourceDecoderSha256 = Get-Sha256 $sourceDecoder
    encoderSha256 = Get-Sha256 $encoder
    decoderSha256 = Get-Sha256 $decoder
}
[IO.File]::WriteAllText(
    $marker,
    ($record | ConvertTo-Json) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
Remove-Item -LiteralPath $stagedMarker -Force -ErrorAction SilentlyContinue
exit 0
