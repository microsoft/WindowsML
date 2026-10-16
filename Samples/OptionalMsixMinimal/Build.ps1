# Copyright (C) Microsoft Corporation. All rights reserved.

[CmdletBinding()]
param([switch]$NoRestore)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sdk = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64"
if (!(Test-Path -LiteralPath "$sdk\makeappx.exe")) { throw 'Install Windows SDK 10.0.26100.0.' }
$output = Join-Path $PSScriptRoot 'artifacts\unsigned'
$stage = Join-Path $PSScriptRoot ("artifacts\stage-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output, "$stage\Main\Assets", "$stage\Optional\Assets" -Force | Out-Null
Push-Location $PSScriptRoot
try {
    [string[]]$restoreArguments = @()
    if ($NoRestore) { $restoreArguments = @('--no-restore') }
    dotnet publish .\Main\Main.csproj -c Release -o "$stage\Main" @restoreArguments --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Main app publish failed.' }
    Copy-Item -LiteralPath .\Assets\StoreLogo.png, .\Assets\Square44x44Logo.png,
        .\Assets\Square150x150Logo.png -Destination "$stage\Main\Assets"
    Copy-Item -LiteralPath .\Main\AppxManifest.xml -Destination "$stage\Main\AppxManifest.xml"
    Copy-Item -LiteralPath .\Optional\AppxManifest.xml, .\Optional\hello.txt -Destination "$stage\Optional"
    New-Item -ItemType Directory -Path "$stage\Optional\Models" -Force | Out-Null
    $modelBase64 = Get-Content -LiteralPath .\Optional\demo.onnx.base64 -Raw
    [IO.File]::WriteAllBytes(
        "$stage\Optional\Models\demo.onnx",
        [Convert]::FromBase64String($modelBase64))
    Copy-Item -LiteralPath "$stage\Main\Assets\StoreLogo.png" -Destination "$stage\Optional\Assets"
    foreach ($name in @('Main', 'Optional')) {
        $packLog = & "$sdk\makeappx.exe" pack /d "$stage\$name" /p "$output\$name.msix" /o 2>&1
        if ($LASTEXITCODE -ne 0) { throw "$name packaging failed:`n$($packLog -join "`n")" }
        Write-Host "Packaged $name.msix."
    }
} finally {
    Pop-Location
    [IO.Directory]::Delete($stage, $true)
}
Write-Host "Built unsigned packages in $output. Signing, trust, and installation are separate."
