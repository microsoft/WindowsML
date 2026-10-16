# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CertificateThumbprint)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if ($certificate.Subject -ne 'CN=OptionalMsixMinimal' -or !$certificate.HasPrivateKey -or
    $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
    throw 'Use a valid signing certificate with a private key and Subject CN=OptionalMsixMinimal.'
}
$signTool = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
if (!(Test-Path -LiteralPath $signTool)) { throw 'Install Windows SDK 10.0.26100.0.' }
foreach ($name in @('Main', 'Optional')) {
    if (!(Test-Path -LiteralPath "$PSScriptRoot\artifacts\unsigned\$name.msix")) { throw 'Run Build.ps1 first.' }
}
$output = Join-Path $PSScriptRoot 'artifacts\signed'
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($name in @('Main', 'Optional')) {
    $path = "$output\$name.msix"
    Copy-Item -LiteralPath "$PSScriptRoot\artifacts\unsigned\$name.msix" -Destination $path -Force
    & $signTool sign /fd SHA256 /s My /sha1 $CertificateThumbprint $path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $path" }
}
Write-Host "Signed packages in $output. Nothing was trusted or installed."
