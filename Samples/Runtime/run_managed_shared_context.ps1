# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Runs the Runtime-managed shared execution-provider context sample.

.DESCRIPTION
Generates the sample model pair when needed, runs the two-stage pipeline, and
summarizes the providers recorded in the ONNX Runtime profiles. When Provider
is omitted, the script selects an execution provider from detected NPU hardware
and falls back to the CPU provider when no supported NPU is present.

.PARAMETER Provider
The execution provider used by both pipeline stages. Omit this parameter to
select a provider automatically from the detected hardware.

.PARAMETER DeviceKind
The execution target kind passed to the Runtime. When Provider is omitted, the
automatic selection also chooses the device kind.

.PARAMETER DisableSharing
Runs both stages without assigning them to a managed shared-context group.

.EXAMPLE
.\run_managed_shared_context.ps1

.EXAMPLE
.\run_managed_shared_context.ps1 -DeviceKind cpu
#>

[CmdletBinding()]
param(
    [ValidateSet(
        "CPUExecutionProvider",
        "VitisAIExecutionProvider",
        "OpenVINOExecutionProvider",
        "QNNExecutionProvider"
    )]
    [string]$Provider,
    [ValidateSet("cpu", "gpu", "npu")]
    [string]$DeviceKind,
    [string]$FirstModel,
    [string]$SecondModel,
    [string]$OutputDirectory,
    [ValidateSet("ARM64", "x64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$DisableSharing
)

$ErrorActionPreference = "Stop"
$sampleDirectory = Join-Path $PSScriptRoot "interop\managed-shared-context"
$modelsDirectory = Join-Path $PSScriptRoot "models\managed-shared-context"
$firstModelSpecified = $PSBoundParameters.ContainsKey("FirstModel")
$secondModelSpecified = $PSBoundParameters.ContainsKey("SecondModel")
$providerSpecified = $PSBoundParameters.ContainsKey("Provider")
$deviceKindSpecified = $PSBoundParameters.ContainsKey("DeviceKind")

function Get-AutomaticProviderSelection
{
    try
    {
        $devices = @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop)
    }
    catch
    {
        throw "Unable to detect accelerator hardware. Specify Provider and DeviceKind explicitly. $($_.Exception.Message)"
    }

    $accelerators = @(
        $devices | Where-Object {
            $_.Status -eq "OK" -and
            ($_.PNPClass -eq "ComputeAccelerator" -or
             $_.Name -match "(?i)\b(NPU|Neural Processing Unit|AI Boost|Hexagon)\b")
        }
    )

    foreach ($accelerator in $accelerators)
    {
        $identity = "$($accelerator.Name) $($accelerator.Manufacturer) $($accelerator.PNPDeviceID)"
        if ($identity -match "(?i)\b(AMD|Advanced Micro Devices)\b|VEN_1022")
        {
            return [pscustomobject]@{
                Provider = "VitisAIExecutionProvider"
                DeviceKind = "npu"
                Hardware = $accelerator.Name
            }
        }

        if ($identity -match "(?i)\bIntel\b|VEN_8086")
        {
            return [pscustomobject]@{
                Provider = "OpenVINOExecutionProvider"
                DeviceKind = "npu"
                Hardware = $accelerator.Name
            }
        }

        if ($identity -match "(?i)\b(Qualcomm|Hexagon)\b|VEN_17CB")
        {
            return [pscustomobject]@{
                Provider = "QNNExecutionProvider"
                DeviceKind = "npu"
                Hardware = $accelerator.Name
            }
        }
    }

    return [pscustomobject]@{
        Provider = "CPUExecutionProvider"
        DeviceKind = "cpu"
        Hardware = "No supported NPU detected"
    }
}

if (-not $providerSpecified)
{
    if ($deviceKindSpecified)
    {
        if ($DeviceKind -eq "cpu")
        {
            $Provider = "CPUExecutionProvider"
            Write-Host "Selected provider: $Provider ($DeviceKind)"
        }
        else
        {
            throw "DeviceKind '$DeviceKind' requires an explicit Provider. Omit both parameters for automatic selection."
        }
    }
    else
    {
        $selection = Get-AutomaticProviderSelection
        $Provider = $selection.Provider
        $DeviceKind = $selection.DeviceKind
        Write-Host "Detected hardware: $($selection.Hardware)"
        Write-Host "Auto-selected provider: $Provider ($DeviceKind)"
    }
}
elseif (-not $DeviceKind)
{
    $DeviceKind = if ($Provider -eq "CPUExecutionProvider") { "cpu" } else { "npu" }
}

if (-not $FirstModel)
{
    $FirstModel = Join-Path $modelsDirectory "add_one_3x3.onnx"
}

if (-not $SecondModel)
{
    $SecondModel = Join-Path $modelsDirectory "add_one_1x1.onnx"
}

if ($firstModelSpecified -and -not (Test-Path $FirstModel))
{
    throw "First model not found: $FirstModel"
}

if ($secondModelSpecified -and -not (Test-Path $SecondModel))
{
    throw "Second model not found: $SecondModel"
}

if (-not (Test-Path $FirstModel) -or -not (Test-Path $SecondModel))
{
    & python (Join-Path $sampleDirectory "generate_models.py") `
        --output $modelsDirectory
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}

if (-not $OutputDirectory)
{
    $OutputDirectory = Join-Path $PSScriptRoot "profiles\managed-shared-context"
}

$executable = Join-Path $PSScriptRoot "out\$Platform\$Configuration\managed-shared-context\managed-shared-context.exe"
if (-not (Test-Path $executable))
{
    throw "Sample executable not found: $executable"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$runId = [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff")
$profilePrefix = Join-Path $OutputDirectory "profile-$runId"
$providerLog = Join-Path $OutputDirectory "provider-$runId.log"

$arguments = @(
    "--first-model", $FirstModel,
    "--second-model", $SecondModel,
    "--provider", $Provider,
    "--device-kind", $DeviceKind,
    "--profile-prefix", $profilePrefix
)
if ($DisableSharing)
{
    $arguments += "--disable-sharing"
}

$savedErrorActionPreference = $ErrorActionPreference
try
{
    $ErrorActionPreference = "Continue"
    & $executable @arguments *> $providerLog
    $exitCode = $LASTEXITCODE
}
finally
{
    $ErrorActionPreference = $savedErrorActionPreference
}

$output = @(Get-Content $providerLog)
$output |
    Where-Object {
        $_ -match "^(Provider|Device kind|Managed sharing|CPU fallback|Input|First output|Output elements|Expected|ORT profile prefix|Result):"
    }
Write-Host "Provider diagnostics: $providerLog"

$profiles = @(Get-ChildItem "$profilePrefix*.json")
$profileProviders = @()
foreach ($profile in $profiles)
{
    $profileProviders += @(
        Get-Content $profile.FullName -Raw |
            ConvertFrom-Json |
            Where-Object { $_.args.provider } |
            ForEach-Object { $_.args.provider }
    )
}

$profileProviders = @($profileProviders | Sort-Object -Unique)
Write-Host "ORT profiles: $($profiles.Count)"
Write-Host "Profile provider(s): $($profileProviders -join ', ')"

if ($exitCode -ne 0)
{
    throw "Sample failed with exit code $exitCode. See $providerLog."
}
