# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Serves Qwen3.8-27B (NVFP4) with DFlash2 speculative decoding through
WinMLServer.exe, with a float16 or int8 key and value cache.

.DESCRIPTION
Starts WinMLServer.exe with the settings that were tuned and benchmarked for
nvidia/Qwen3.8-27B-NVFP4 with the incoai/Qwen3.8-27B-DFlash2 drafter on a
32 GB GPU. See qwen3.8-27b\README.md for the checkpoints, the package version,
and what each setting does.

Without -Client, the server runs in this console until you press Ctrl+C, and
prints its address and access key so you can connect a coding agent or another
OpenAI-compatible client.

With -Client, the script starts the server, runs the client against it, and
stops the server when the client exits.

.PARAMETER KvCache
The type the attention key and value cache is held in: float16 (default) or
int8. int8 roughly doubles the context that fits and needs
kv_cache_scales.safetensors beside the model, which
qwen3.8-27b\calibrate_kv_scales.py writes.

.PARAMETER ModelPath
Optional nvidia/Qwen3.8-27B-NVFP4 folder. Defaults to
models\Qwen3.8-27B-NVFP4, which check_artifacts.ps1 checks.

.PARAMETER DraftModelPath
Optional incoai/Qwen3.8-27B-DFlash2 folder. Defaults to
models\Qwen3.8-27B-DFlash2, which check_artifacts.ps1 checks.

.PARAMETER ModelId
Optional model id that clients send. Defaults to Qwen3.8-27B-NVFP4.

.PARAMETER ContextTokens
Context window, in tokens. Defaults to 135168 for float16 and 262144 for int8,
the largest windows that fit on a 32 GB GPU. The launch fails if the cache
doesn't fit in dedicated GPU memory; lower it on a smaller GPU.

.PARAMETER MaxOutputTokens
Output limit per request, in tokens. Defaults to 4096.

.PARAMETER DraftTokens
Tokens DFlash2 drafts per verification step. Defaults to 7.

.PARAMETER CompiledProgramDirectory
Where the server stores the GPU programs it compiles, so later starts reuse
them. Defaults to models\compiled-programs\qwen3.8-27b-<KvCache>.

.PARAMETER Client
Optional client to run: cpp, python, or csharp. Build the C++ and C# clients
with build.ps1 first.

.PARAMETER Port
Optional loopback port. Defaults to a free port.

.PARAMETER Platform
Architecture of the previously built executables.

.PARAMETER Configuration
Configuration of the previously built executables.

.PARAMETER NonInteractive
Skip the confirmation prompt before the sample runs.

.EXAMPLE
.\run_qwen3.8_27b_server.ps1

.EXAMPLE
.\run_qwen3.8_27b_server.ps1 -KvCache int8

.EXAMPLE
.\run_qwen3.8_27b_server.ps1 -KvCache int8 -Client cpp
#>

[CmdletBinding()]
param(
    [ValidateSet("float16", "int8")]
    [string]$KvCache = "float16",
    [string]$ModelPath,
    [string]$DraftModelPath,
    [string]$ModelId = "Qwen3.8-27B-NVFP4",
    [uint32]$ContextTokens,
    [uint32]$MaxOutputTokens = 4096,
    [ValidateRange(1, 16)]
    [int]$DraftTokens = 7,
    [string]$CompiledProgramDirectory,
    [ValidateSet("cpp", "python", "csharp")]
    [string]$Client,
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

$nativeErrorPreference =
    Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
if ($nativeErrorPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# Without arguments the executable prints its options. Check for the key and
# value cache option before loading 22 GB of weights.
$ErrorActionPreference = "Continue"
$usage = (& $serverExe 2>&1 | Out-String)
$ErrorActionPreference = "Stop"
if ($usage -notmatch '--kv-cache-type' -or $usage -notmatch '--compiled-program-directory') {
    $version = (Get-Item -LiteralPath $serverExe).VersionInfo.ProductVersion
    throw "WinMLServer.exe $version has no --kv-cache-type or --compiled-program-directory option. Rebuild with 2.9.42-experimental or later: .\build.ps1 -Sample executable"
}

$modelsDirectory = Join-Path $PSScriptRoot "models"
# The catalog describes the default folders; checkpoints passed with -ModelPath
# or -DraftModelPath are checked only for the files the server needs.
if (-not $ModelPath -and -not $DraftModelPath) {
    Assert-SampleArtifactsReady `
        -Sample server-qwen3.8-27b `
        -ModelsDirectory $modelsDirectory `
        -Note "KV cache: $KvCache.", $(if ($Client) { "Client: $Client." } else { "Press Ctrl+C to stop the server." }) `
        -NonInteractive:$NonInteractive
}
if (-not $ModelPath) { $ModelPath = Join-Path $modelsDirectory "Qwen3.8-27B-NVFP4" }
if (-not $DraftModelPath) { $DraftModelPath = Join-Path $modelsDirectory "Qwen3.8-27B-DFlash2" }
foreach ($required in @(
        @($ModelPath, "config.json", "model.safetensors.index.json", "tokenizer.json", "chat_template.jinja"),
        @($DraftModelPath, "config.json", "model.safetensors"))) {
    $folder = $required[0]
    foreach ($file in ($required | Select-Object -Skip 1)) {
        if (-not (Test-Path -LiteralPath (Join-Path $folder $file) -PathType Leaf)) {
            throw "$file was not found in $folder."
        }
    }
}
$ModelPath = (Resolve-Path -LiteralPath $ModelPath).Path
$DraftModelPath = (Resolve-Path -LiteralPath $DraftModelPath).Path

if ($KvCache -eq "int8") {
    $modelScales = Join-Path $ModelPath "kv_cache_scales.safetensors"
    if (-not (Test-Path -LiteralPath $modelScales -PathType Leaf)) {
        throw "An int8 cache needs kv_cache_scales.safetensors beside the model. Calibrate it once with qwen3.8-27b\calibrate_kv_scales.py; see `"INT8 KV cache calibration`" in qwen3.8-27b\README.md."
    }
}

if (-not $ContextTokens) {
    $ContextTokens = if ($KvCache -eq "int8") { 262144 } else { 135168 }
}
if (-not $CompiledProgramDirectory) {
    $CompiledProgramDirectory = Join-Path $modelsDirectory "compiled-programs\qwen3.8-27b-$KvCache"
}
New-Item -ItemType Directory -Force -Path $modelsDirectory, $CompiledProgramDirectory | Out-Null

$serverArguments = @(
    $ModelPath,
    "--model-id", $ModelId,
    "--composition", "model",
    "--target", "gpu",
    "--kv-cache-type", $KvCache,
    "--context-tokens", $ContextTokens.ToString(),
    "--max-output-tokens", $MaxOutputTokens.ToString(),
    "--speculative", "model",
    "--draft-model", $DraftModelPath,
    "--draft-tokens", $DraftTokens.ToString(),
    "--concurrency", "1",
    "--compiled-program-directory", (Resolve-Path -LiteralPath $CompiledProgramDirectory).Path,
    "--max-request-body-bytes", "16777216",
    "--diagnostics")
if ($Port) { $serverArguments += @("--port", $Port.ToString()) }

# A model folder with tokenizer.json has no exact-vocabulary tokenizer to
# constrain tool calls, so the server only serves tool calls when the recipe
# selects natural tool calls: the chat template renders the tools and the
# server parses the calls the model writes. The command line has no option for
# it, so print the recipe these options resolve to and add the setting.
$printed = & $serverExe @serverArguments --print-recipe | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "WinMLServer.exe could not resolve the options (exit code $LASTEXITCODE)."
}
$recipe = $printed | ConvertFrom-Json
$recipe.models[0] | Add-Member -NotePropertyName naturalToolCalls -NotePropertyValue $true
$recipePath = Join-Path $modelsDirectory "qwen3.8-27b-$KvCache.recipe.json"
[System.IO.File]::WriteAllText($recipePath, ($recipe | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))

# Qwen3.8's chat template accepts these reasoning efforts. The server admits
# only "none" unless the launch lists them, and recipes don't carry them.
$serverArguments = @("--recipe", $recipePath, "--reasoning-efforts", "none,low,medium,xhigh")
Write-Host "Recipe: $recipePath" -ForegroundColor Cyan
Write-Host "$serverExe $(ConvertTo-CommandLine $serverArguments)" -ForegroundColor Cyan
Write-Host "The first start compiles the GPU programs, which takes several minutes. Later starts reuse them."

# On its own, the server writes its address and key to this console, where only
# you can read them, and runs until you press Ctrl+C.
if (-not $Client) {
    & $serverExe @serverArguments
    exit $LASTEXITCODE
}

$clientCommand = @(Get-ServerClientCommand -Client $Client -Platform $Platform -Configuration $Configuration)
exit (Invoke-ServerWithClient -ServerExe $serverExe -ServerArguments $serverArguments -ClientCommand $clientCommand)
