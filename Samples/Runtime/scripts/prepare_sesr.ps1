# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputModel,

    [Parameter(Mandatory)]
    [string]$OutputModel
)

$ErrorActionPreference = "Stop"

$tool = Join-Path (Split-Path $PSScriptRoot -Parent) "tools\strip_onnx_identity.py"
if (-not (Test-Path -LiteralPath $tool)) {
    throw "SESR preparation tool was not found: $tool"
}

. (Join-Path $PSScriptRoot "resolve_onnx_python.ps1")
$python = Get-OnnxPython
& $python $tool --input $InputModel --output $OutputModel

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputModel)) {
    throw "Failed to prepare the SESR model."
}

exit 0
