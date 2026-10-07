# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs WinMLServer.exe on its own or with one of the clients.

.DESCRIPTION
Without -Client, WinMLServer.exe serves the model in this console until you
press Ctrl+C. It prints its address and access key so you can connect a coding
agent or another OpenAI-compatible client.

With -Client, the script starts WinMLServer.exe, reads the address and access
key from its output, and starts the client with them in the client's
environment. The script stops the server when the client exits.

.PARAMETER Client
Optional client to run: cpp, python, or csharp. Build the C++ and C# clients
with build.ps1 first. The Python client needs Python 3.10 or later and the
OpenAI Python library.

.PARAMETER ModelPath
Optional GGUF model path. Defaults to the model in models\gguf.

.PARAMETER ModelId
Optional model id that clients send. Defaults to the model file name.

.PARAMETER ContextTokens
Optional context window, in tokens. Defaults to the model's default.

.PARAMETER Device
Where llama.cpp runs the model: cpu (default) or gpu. On a GPU, llama.cpp uses
a GPU backend for the device the Runtime selects.

.PARAMETER Port
Optional loopback port. Defaults to a free port.

.PARAMETER Platform
Architecture of the previously built executables.

.PARAMETER Configuration
Configuration of the previously built executables.

.PARAMETER NonInteractive
Skip the confirmation prompt before the sample runs.

.EXAMPLE
.\run_server_executable.ps1

.EXAMPLE
.\run_server_executable.ps1 -Client csharp

.EXAMPLE
.\run_server_executable.ps1 -Client cpp -Device gpu
#>

[CmdletBinding()]
param(
    [ValidateSet("cpp", "python", "csharp")]
    [string]$Client,
    [string]$ModelPath,
    [string]$ModelId,
    [uint32]$ContextTokens,
    [ValidateSet("cpu", "gpu")]
    [string]$Device = "cpu",
    [ValidateRange(1, 65535)]
    [int]$Port,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\shared\SampleArtifacts.psm1") -Force
. (Join-Path $PSScriptRoot "scripts\client_command.ps1")

$serverExe = Join-Path $PSScriptRoot "out\$Platform\$Configuration\server-executable\WinMLServer.exe"
if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    throw "WinMLServer.exe was not found. Build it first: .\build.ps1 -Sample executable"
}

if (-not $ModelPath) {
    $modelsDirectory = Join-Path $PSScriptRoot "models"
    Assert-SampleArtifactsReady `
        -Sample server-executable `
        -ModelsDirectory $modelsDirectory `
        -Note $(if ($Client) { "Client: $Client." } else { "Press Ctrl+C to stop the server." }) `
        -NonInteractive:$NonInteractive
    $ModelPath = Join-Path $modelsDirectory "gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf"
}
if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
    throw "Model was not found: $ModelPath"
}

$serverArguments = @((Resolve-Path -LiteralPath $ModelPath).Path)
if ($ModelId) { $serverArguments += @("--model-id", $ModelId) }
if ($ContextTokens) { $serverArguments += @("--context-tokens", $ContextTokens.ToString()) }
$serverArguments += @("--target", $Device.ToLowerInvariant())
if ($Port) { $serverArguments += @("--port", $Port.ToString()) }
$commandLine = ConvertTo-CommandLine $serverArguments
Write-Host "$serverExe $commandLine" -ForegroundColor Cyan

$nativeErrorPreference =
    Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
if ($nativeErrorPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# On its own, the server writes its address and key to this console, where only
# you can read them, and runs until you press Ctrl+C.
if (-not $Client) {
    & $serverExe @serverArguments
    exit $LASTEXITCODE
}

$clientCommand = @(Get-ServerClientCommand -Client $Client -Platform $Platform -Configuration $Configuration)

# The script reads the server's standard output to get the address and key.
# Errors still go to this console.
$serverStart = New-Object System.Diagnostics.ProcessStartInfo
$serverStart.FileName = $serverExe
$serverStart.Arguments = $commandLine
$serverStart.UseShellExecute = $false
$serverStart.RedirectStandardOutput = $true
$server = [System.Diagnostics.Process]::Start($serverStart)
try {
    # Once the server is listening, it writes the access key, then its address,
    # then one line for each model it serves. Loading the model can take a
    # while.
    $accessKey = $null
    $baseUrl = $null
    while (-not $baseUrl) {
        $line = $server.StandardOutput.ReadLine()
        if ($null -eq $line) {
            $server.WaitForExit()
            throw "WinMLServer.exe exited with code $($server.ExitCode) before it was ready."
        }

        if ($line.StartsWith("WINMLSERVER_ACCESS_KEY ")) {
            $accessKey = $line.Substring("WINMLSERVER_ACCESS_KEY ".Length)
        }
        elseif ($line.StartsWith("WINMLSERVER_READY ")) {
            $baseUrl = $line.Substring("WINMLSERVER_READY ".Length)
        }
    }
    if (-not $accessKey) {
        throw "WinMLServer.exe did not report an access key."
    }

    Write-Host $server.StandardOutput.ReadLine()
    Write-Host "Serving at $baseUrl"
    Write-Host ""

    # Keep reading the server's output so it can never block on a full pipe.
    $null = $server.StandardOutput.ReadToEndAsync()

    # Only the client's environment gets the key. It never appears on a command
    # line, where other users on the computer could read it.
    $clientStart = New-Object System.Diagnostics.ProcessStartInfo
    $clientStart.FileName = $clientCommand[0]
    $clientStart.Arguments = ConvertTo-CommandLine @($clientCommand | Select-Object -Skip 1)
    $clientStart.UseShellExecute = $false
    $clientStart.EnvironmentVariables["WINMLSERVER_BASE_URL"] = $baseUrl
    $clientStart.EnvironmentVariables["WINMLSERVER_ACCESS_KEY"] = $accessKey
    $accessKey = $null
    $clientProcess = [System.Diagnostics.Process]::Start($clientStart)
    $clientProcess.WaitForExit()
    $exitCode = $clientProcess.ExitCode
}
finally {
    # The access key is valid only while this server process runs.
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    $server.WaitForExit()
}

Write-Host ""
Write-Host "Client exited with code $exitCode. Server stopped."
exit $exitCode
