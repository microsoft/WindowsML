# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Renders the curated asset table in the artifact documentation.

.DESCRIPTION
Samples\shared\SampleArtifacts.psd1 is the single source of truth for what each
sample downloads. This script regenerates the table in
docs\Runtime\artifacts.md from that catalog so the documentation and the
artifact check cannot point at different sources.

Run it after changing the catalog. Use -Check to verify the documentation is
current without writing.

.PARAMETER Check
Compare only. Exits non-zero when the documentation is out of date.

.EXAMPLE
.\Export-ArtifactTable.ps1

Rewrites the table from the catalog.

.EXAMPLE
.\Export-ArtifactTable.ps1 -Check

Fails when the table no longer matches the catalog.
#>

[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SampleArtifacts.psm1') -Force

$documentPath = Join-Path $PSScriptRoot '..\..\docs\Runtime\artifacts.md'
if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) {
    throw "The artifact documentation was not found: $documentPath"
}

$beginMarker = '<!-- BEGIN GENERATED ASSET TABLE -->'
$endMarker = '<!-- END GENERATED ASSET TABLE -->'

$catalog = Get-SampleArtifactCatalog
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add('| Asset | Purpose | Source page | Size |')
$rows.Add('|---|---|---|---|')

foreach ($id in ($catalog.Artifacts.Keys | Sort-Object)) {
    $artifact = $catalog.Artifacts[$id]
    $provenance = "[$($artifact.ProvenanceUrl)]($($artifact.ProvenanceUrl))"
    $size = Format-SampleByteSize -Bytes ([int64]$artifact.SizeBytes)
    $rows.Add("| $($artifact.DisplayName) | $($artifact.Purpose) | $provenance | $size |")
}

$rows.Add('')
$rows.Add('Artifacts produced on your machine rather than downloaded:')
$rows.Add('')
$rows.Add('| Artifact | What it produces | Source page |')
$rows.Add('|---|---|---|')

foreach ($id in ($catalog.Preparations.Keys | Sort-Object)) {
    $preparation = $catalog.Preparations[$id]
    $source = 'Derived from downloaded artifacts'
    if ($preparation.ContainsKey('ProvenanceUrl')) {
        $source = "[$($preparation.ProvenanceUrl)]($($preparation.ProvenanceUrl))"
    }
    $rows.Add("| $($preparation.DisplayName) | $($preparation.Description) | $source |")
}

$generated = ($rows -join [Environment]::NewLine)
$document = Get-Content -LiteralPath $documentPath -Raw

$pattern = [regex]::Escape($beginMarker) + '.*?' + [regex]::Escape($endMarker)
$replacement = $beginMarker + [Environment]::NewLine + $generated + [Environment]::NewLine + $endMarker
if (-not [regex]::IsMatch($document, $pattern, 'Singleline')) {
    throw "The generated table markers were not found in $documentPath."
}
$updated = [regex]::Replace($document, $pattern, { $replacement }, 'Singleline')

if ($Check) {
    if ($updated -ne $document) {
        Write-Host 'The curated asset table is out of date.' -ForegroundColor Red
        Write-Host 'Regenerate it with Samples\shared\Export-ArtifactTable.ps1' -ForegroundColor Yellow
        exit 1
    }
    Write-Host 'The curated asset table matches the artifact catalog.' -ForegroundColor Green
    exit 0
}

Set-Content -LiteralPath $documentPath -Value $updated -NoNewline -Encoding utf8
Write-Host "Regenerated the curated asset table in $documentPath" -ForegroundColor Green
