# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the in-process server sample with one of the clients.

.DESCRIPTION
server-in-process.exe loads a GGUF model, hosts the OpenAI-compatible server in
its own process, and starts the client with the server's address and access key
in the client's environment. The server stops when the client exits.

.PARAMETER Client
The client to run: cpp, python, or csharp. Build the C++ and C# clients with
build.ps1 first. The Python client needs Python 3.10 or later and the OpenAI
Python library.

.PARAMETER ModelPath
Optional GGUF model path. Defaults to the model in models\gguf.

.PARAMETER ModelId
Optional model id that clients send. Defaults to the model file name.

.PARAMETER ContextTokens
Optional context window, in tokens. Defaults to the model's default.

.PARAMETER Device
Where llama.cpp runs the model: cpu (default) or gpu. On a GPU, llama.cpp uses
a GPU backend for the device the Runtime selects.

.PARAMETER Platform
Architecture of the previously built executables.

.PARAMETER Configuration
Configuration of the previously built executables.

.PARAMETER NonInteractive
Skip the confirmation prompt before the sample runs.

.EXAMPLE
.\run_in_process_server.ps1

.EXAMPLE
.\run_in_process_server.ps1 -Client python

.EXAMPLE
.\run_in_process_server.ps1 -Device gpu
#>

[CmdletBinding()]
param(
    [ValidateSet("cpp", "python", "csharp")]
    [string]$Client = "cpp",
    [string]$ModelPath,
    [string]$ModelId,
    [uint32]$ContextTokens,
    [ValidateSet("cpu", "gpu")]
    [string]$Device = "cpu",
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
. (Join-Path $PSScriptRoot "scripts\client_command.ps1")

if (-not $ModelPath) {
    $modelsDirectory = Join-Path $PSScriptRoot "models"
    Assert-SampleArtifactsReady `
        -Sample server-in-process `
        -ModelsDirectory $modelsDirectory `
        -Note "Client: $Client." `
        -NonInteractive:$NonInteractive
    $ModelPath = Join-Path $modelsDirectory "gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf"
}
if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
    throw "Model was not found: $ModelPath"
}
if ([IO.Path]::GetExtension($ModelPath) -ne ".gguf") {
    throw "The in-process server sample requires a GGUF model."
}

$clientCommand = @(Get-ServerClientCommand -Client $Client -Platform $Platform -Configuration $Configuration)

# Everything after -- is the client command line.
$arguments = @((Resolve-Path -LiteralPath $ModelPath).Path)
if ($ModelId) { $arguments += @("--model-id", $ModelId) }
if ($ContextTokens) { $arguments += @("--context-tokens", $ContextTokens.ToString()) }
$arguments += @("--device", $Device.ToLowerInvariant())
$arguments += "--"
$arguments += $clientCommand

& (Join-Path $PSScriptRoot "scripts\invoke_sample.ps1") `
    -ProjectName server-in-process `
    -SampleArguments $arguments `
    -Platform $Platform `
    -Configuration $Configuration
exit $LASTEXITCODE
