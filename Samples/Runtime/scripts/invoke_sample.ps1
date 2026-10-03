# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectName,
    [string[]]$SampleArguments = @(),
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "out\$Platform\$Configuration\$ProjectName\$ProjectName.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Sample executable was not found. Build '$ProjectName' first: $exe"
}

$displayArguments = $SampleArguments | ForEach-Object {
    if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
}
Write-Host "$exe $($displayArguments -join ' ')" -ForegroundColor Cyan

$nativeErrorPreference = Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
if ($nativeErrorPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

& $exe @SampleArguments
exit $LASTEXITCODE
