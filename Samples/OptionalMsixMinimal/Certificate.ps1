# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][ValidateSet('New', 'Trust', 'RemoveTrust')][string]$Action)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$directory = Join-Path $PSScriptRoot 'certificates'
$recordPath = Join-Path $directory 'signer.json'
$cerPath = Join-Path $directory 'signer.cer'
$statePath = Join-Path $directory 'trust.json'

if ($Action -eq 'New') {
    if ((Test-Path -LiteralPath $recordPath) -or (Test-Path -LiteralPath $cerPath)) {
        throw 'A signer record already exists. Reuse it instead of creating another signer.'
    }
    if ($PSCmdlet.ShouldProcess('Cert:\CurrentUser\My', 'Create OptionalMsixMinimal development signer')) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=OptionalMsixMinimal' `
            -FriendlyName 'Optional MSIX Minimal - Development Only' -CertStoreLocation 'Cert:\CurrentUser\My' `
            -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyUsage DigitalSignature `
            -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddMonths(6) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        Write-Host "Created signer $($certificate.Thumbprint) in CurrentUser\My."
        Export-Certificate -Cert $certificate -FilePath $cerPath -NoClobber | Out-Null
        @{ thumbprint = $certificate.Thumbprint } | ConvertTo-Json |
            Set-Content -LiteralPath $recordPath -Encoding utf8
        Write-Host 'Only the public certificate was exported. Nothing was trusted.'
    }
    return
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Trust management requires Administrator PowerShell and permission under your device policy.'
}
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
if ($record.thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Invalid signer record.' }
$trustPath = "Cert:\LocalMachine\TrustedPeople\$($record.thumbprint)"
if ($Action -eq 'RemoveTrust') {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.thumbprint -ne $record.thumbprint -or $state.addedBySample -isnot [bool]) { throw 'Invalid trust record.' }
    if (!$state.addedBySample) { Write-Host 'Trust predated this sample; it will not be removed.'; return }
    if ($PSCmdlet.ShouldProcess($trustPath, 'Remove only trust added by this sample')) {
        if (Test-Path -LiteralPath $trustPath) { Remove-Item -LiteralPath $trustPath }
        Remove-Item -LiteralPath $statePath
        Write-Host 'Sample-added trust removed. The signing key remains in CurrentUser\My.'
    }
    return
}

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)
try {
    if ($certificate.Thumbprint -ne $record.thumbprint -or $certificate.Subject -ne 'CN=OptionalMsixMinimal') {
        throw 'The public certificate does not match the sample signer record.'
    }
} finally { $certificate.Dispose() }
if ((Test-Path -LiteralPath $statePath) -and (Test-Path -LiteralPath $trustPath)) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.thumbprint -ne $record.thumbprint -or $state.addedBySample -isnot [bool]) { throw 'Invalid trust record.' }
    Write-Host 'The recorded signer is already trusted.'
    return
}
if ($PSCmdlet.ShouldProcess($trustPath, 'Trust this public certificate machine-wide in TrustedPeople, not Root')) {
    $added = !(Test-Path -LiteralPath $trustPath)
    if ($added) { Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null }
    @{ thumbprint = $record.thumbprint; addedBySample = $added } | ConvertTo-Json |
        Set-Content -LiteralPath $statePath -Encoding utf8
    Write-Host 'Signer trusted. No packages were installed.'
}
