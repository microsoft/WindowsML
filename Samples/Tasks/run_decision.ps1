# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the typed decision sample: the published Laya model answers a choice
question about a state.

.PARAMETER ModelPath
A decision model folder to use instead of the cataloged Laya folder.

.PARAMETER State
The text the question is about.

.PARAMETER ProgramCache
A directory in which sessions keep the programs they compile for the GPU, so
later runs start faster.

.PARAMETER CacheWeights
Also keep the weights prepared for each program in the program cache. Requires
-ProgramCache.
#>

[CmdletBinding()]
param(
    [string]$ModelPath,
    [string]$State = "I was charged twice for my subscription.",
    [string]$ProgramCache,
    [switch]$CacheWeights,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
if ($CacheWeights -and -not $ProgramCache) {
    throw "-CacheWeights requires -ProgramCache."
}
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
if (-not $ModelPath) {
    Assert-SampleArtifactsReady `
        -Sample "task-decision" `
        -ModelsDirectory (Join-Path $PSScriptRoot "models") `
        -Note "State: `"$State`"." `
        -NonInteractive:$NonInteractive
    $ModelPath = Join-Path $PSScriptRoot "models\laya"
}
if (-not (Test-Path -LiteralPath $ModelPath -PathType Container)) {
    throw "Decision model folder was not found: $ModelPath"
}

$sampleArguments = @((Resolve-Path -LiteralPath $ModelPath).Path, $State)
if ($ProgramCache) {
    $sampleArguments += @(
        "--program-cache",
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProgramCache)
    )
    if ($CacheWeights) {
        $sampleArguments += "--cache-weights"
    }
}

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName task-decision `
    -SampleArguments $sampleArguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
