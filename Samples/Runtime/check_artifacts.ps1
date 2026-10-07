# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Reports which model artifacts a sample needs and how to obtain them.

.DESCRIPTION
This script never downloads anything and never runs a preparation command. It
inspects what is already on disk, verifies each file against the pinned size and
SHA-256 in Samples\shared\SampleArtifacts.psd1, and prints the exact command for
anything missing so you can review the source before running it yourself.

The model files are published by third parties, not by Microsoft. You are
responsible for reviewing the terms each publisher attaches and determining
whether your use is permitted.

.PARAMETER Sample
The sample to report on. Omit to report on every sample.

.PARAMETER ModelsDir
Destination root for model artifacts. Defaults to Samples\Runtime\models.

.PARAMETER SkipHash
Check file sizes only. Useful for a fast check over very large models.

.EXAMPLE
.\check_artifacts.ps1

Reports the state of every artifact the Runtime samples can use.

.EXAMPLE
.\check_artifacts.ps1 -Sample whisper

Reports only what the speech recognition sample needs.
#>

[CmdletBinding()]
param(
    [ValidateSet(
        'image-classification',
        'super-resolution',
        'whisper',
        'hello-language-model',
        'llm-chat',
        'llm-chat-gguf',
        'speech-to-language-model',
        'model-compilation',
        'all')]
    [string]$Sample = 'all',
    [string]$ModelsDir,
    [switch]$SkipHash
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\shared\SampleArtifacts.psm1') -Force

if (-not $ModelsDir) {
    $ModelsDir = Join-Path $PSScriptRoot 'models'
}

$samples = if ($Sample -eq 'all') {
    @(
        'image-classification',
        'super-resolution',
        'whisper',
        'hello-language-model',
        'llm-chat',
        'llm-chat-gguf',
        'speech-to-language-model',
        'model-compilation'
    )
}
else {
    @($Sample)
}

Write-Host ''
Write-Host 'Windows ML Runtime samples: artifact check' -ForegroundColor Cyan
Write-Host "Model root: $ModelsDir"
Write-Host ''
Write-Host 'These model files are published by third parties, not by Microsoft.' -ForegroundColor Yellow
Write-Host 'Review each publisher''s terms before downloading or using a model.' -ForegroundColor Yellow

$outstanding = 0
foreach ($id in $samples) {
    $definition = Get-SampleDefinition -Sample $id
    $artifacts = @(Get-SampleArtifactStatus -Sample $id -ModelsDirectory $ModelsDir -SkipHash:$SkipHash)
    $preparations = @(Get-SamplePreparationStatus -Sample $id -ModelsDirectory $ModelsDir)

    Write-Host ''
    Write-Host "== $($definition.Title)  [$id]" -ForegroundColor Cyan
    Write-Host "   $($definition.Summary)"
    Write-Host ''
    Write-SampleArtifactDisclosure -Artifact $artifacts -Preparation $preparations

    $blocked = @($artifacts | Where-Object { $_.State -ne 'Present' }) +
               @($preparations | Where-Object { $_.State -ne 'Present' })
    if ($blocked.Count -gt 0) {
        $outstanding += $blocked.Count
        Write-SampleAcquisitionPlan -Artifact $artifacts -Preparation $preparations
    }
}

Write-Host ''
if ($outstanding -eq 0) {
    Write-Host 'All artifacts for the selected samples are present and match the catalog.' -ForegroundColor Green
    exit 0
}

Write-Host "$outstanding artifact(s) still need to be obtained. Commands are listed above." -ForegroundColor Yellow
exit 1
