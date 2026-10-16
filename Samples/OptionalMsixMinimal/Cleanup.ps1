# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding(SupportsShouldProcess)]
param([ValidateSet('Optional', 'Main', 'All')][string]$Package = 'All')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Package -eq 'Optional') {
    $main = Get-AppxPackage -Name 'OptionalMsixMinimal.Main' |
        Where-Object { $_.Publisher -eq 'CN=OptionalMsixMinimal' }
    if ($main) {
        $manifest = Get-AppxPackageManifest -Package $main.PackageFullName
        $visibleApps = @($manifest.SelectNodes("//*[local-name()='Application']/*[local-name()='VisualElements']") |
            Where-Object { $_.GetAttribute('AppListEntry') -ne 'none' })
        if ($visibleApps.Count -eq 0) {
            throw 'The installed main package is headless. Windows would also remove it with its last optional package. Update Main.msix to version 1.0.0.3 or later first.'
        }
    }
}
if ($Package -eq 'Main' -and (Get-AppxPackage -Name 'OptionalMsixMinimal.Optional' -PackageTypeFilter Optional |
    Where-Object { $_.Publisher -eq 'CN=OptionalMsixMinimal' })) {
    throw 'Remove the optional package first with Cleanup.ps1 -Package Optional, inspect Settings, then run Cleanup.ps1 -Package Main.'
}
$names = switch ($Package) {
    'Optional' { 'OptionalMsixMinimal.Optional' }
    'Main' { 'OptionalMsixMinimal.Main' }
    'All' { 'OptionalMsixMinimal.Optional'; 'OptionalMsixMinimal.Main' }
}
foreach ($name in $names) {
    $packages = @(Get-AppxPackage -Name $name -PackageTypeFilter Main, Optional |
        Where-Object { $_.Publisher -eq 'CN=OptionalMsixMinimal' })
    if ($packages.Count -eq 0) { Write-Host "$name is already absent."; continue }
    foreach ($installedPackage in $packages) {
        if ($PSCmdlet.ShouldProcess($installedPackage.PackageFullName, 'Uninstall for the current user')) {
            Remove-AppxPackage -Package $installedPackage.PackageFullName
            if (Get-AppxPackage -Name $name -PackageTypeFilter Main, Optional |
                Where-Object { $_.Publisher -eq 'CN=OptionalMsixMinimal' }) {
                throw "$name is still registered."
            }
            Write-Host "Removed $name."
        }
    }
}
Write-Host 'Certificate trust, signing keys, and project files were not changed.'
