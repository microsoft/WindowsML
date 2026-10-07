# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Shared artifact checks and consent prompts for the Windows ML samples.

.DESCRIPTION
Every sample needs model files that Microsoft does not redistribute. This module
is the one place that knows what those files are, where they come from, and what
terms they carry, so each sample does not restate them.

The samples never download anything on your behalf and never run a privileged or
network step without you asking for it. A run script describes itself, reports
which artifacts are present, prints the commands for anything missing, and
waits for you to continue.
#>

Set-StrictMode -Version 3.0

$script:CatalogPath = Join-Path $PSScriptRoot 'SampleArtifacts.psd1'
$script:Catalog = $null

function Get-SampleArtifactCatalog {
    <#
    .SYNOPSIS
    Returns the parsed artifact catalog.
    #>
    [CmdletBinding()]
    param()

    if ($null -eq $script:Catalog) {
        if (-not (Test-Path -LiteralPath $script:CatalogPath -PathType Leaf)) {
            throw "The sample artifact catalog was not found: $script:CatalogPath"
        }
        $script:Catalog = Import-PowerShellDataFile -LiteralPath $script:CatalogPath
    }
    return $script:Catalog
}

function Get-SampleDefinition {
    <#
    .SYNOPSIS
    Returns the catalog entry describing one sample.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Sample)

    $catalog = Get-SampleArtifactCatalog
    if (-not $catalog.Samples.ContainsKey($Sample)) {
        $known = ($catalog.Samples.Keys | Sort-Object) -join ', '
        throw "Unknown sample '$Sample'. Known samples: $known"
    }
    return $catalog.Samples[$Sample]
}

function Format-SampleByteSize {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][int64]$Bytes)

    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N0} KB' -f ($Bytes / 1KB)) }
    return "$Bytes bytes"
}

function Get-SampleFileSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    # Hashed through .NET rather than Get-FileHash because the build environment's
    # module path can prevent the Utility module from autoloading.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
    return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}

function Get-SampleArtifactStatus {
    <#
    .SYNOPSIS
    Reports whether each artifact a sample needs is present, missing, or altered.

    .DESCRIPTION
    Size is checked first because it is cheap. The SHA-256 is verified only when
    the size already matches, so a large model is not rehashed on every run.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Sample,
        [Parameter(Mandatory = $true)][string]$ModelsDirectory,
        [switch]$SkipHash
    )

    $catalog = Get-SampleArtifactCatalog
    $definition = Get-SampleDefinition -Sample $Sample

    foreach ($id in @($definition.Artifacts)) {
        if (-not $catalog.Artifacts.ContainsKey($id)) {
            throw "Sample '$Sample' references unknown artifact '$id'."
        }
        $artifact = $catalog.Artifacts[$id]
        $destination = Join-Path $ModelsDirectory $artifact.Path
        $state = 'Missing'
        $detail = 'Not downloaded yet.'

        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $actualBytes = (Get-Item -LiteralPath $destination).Length
            if ($actualBytes -ne $artifact.SizeBytes) {
                $state = 'Altered'
                $detail = "Expected $($artifact.SizeBytes) bytes but found $actualBytes."
            }
            elseif ($SkipHash) {
                $state = 'Present'
                $detail = 'Size matches.'
            }
            else {
                $actualHash = Get-SampleFileSha256 -Path $destination
                if ($actualHash -ne $artifact.Sha256) {
                    $state = 'Altered'
                    $detail = "SHA-256 is $actualHash; expected $($artifact.Sha256)."
                }
                else {
                    $state = 'Present'
                    $detail = 'Size and SHA-256 match.'
                }
            }
        }

        [pscustomobject]@{
            Id          = $id
            DisplayName = $artifact.DisplayName
            Purpose     = $artifact.Purpose
            SourceUrl   = $artifact.SourceUrl
            ProvenanceUrl = $artifact.ProvenanceUrl
            SizeBytes   = [int64]$artifact.SizeBytes
            Sha256      = $artifact.Sha256
            Destination = $destination
            State       = $state
            Detail      = $detail
        }
    }
}

function Get-SamplePreparationStatus {
    <#
    .SYNOPSIS
    Reports whether each locally produced artifact a sample needs already exists.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Sample,
        [Parameter(Mandatory = $true)][string]$ModelsDirectory
    )

    $catalog = Get-SampleArtifactCatalog
    $definition = Get-SampleDefinition -Sample $Sample

    foreach ($id in @($definition.Preparations)) {
        if (-not $catalog.Preparations.ContainsKey($id)) {
            throw "Sample '$Sample' references unknown preparation '$id'."
        }
        $preparation = $catalog.Preparations[$id]
        $missing = @(
            foreach ($relative in @($preparation.Produces)) {
                $produced = Join-Path $ModelsDirectory $relative
                if (-not (Test-Path -LiteralPath $produced -PathType Leaf)) { $relative }
            }
        )
        $command = $preparation.Command.Replace('{ModelsDirectory}', $ModelsDirectory.TrimEnd('\'))

        $provenanceUrl = $null
        if ($preparation.ContainsKey('ProvenanceUrl')) { $provenanceUrl = $preparation.ProvenanceUrl }

        [pscustomobject]@{
            Id               = $id
            DisplayName      = $preparation.DisplayName
            Description      = $preparation.Description
            Requires         = $preparation.Requires
            Command          = $command
            WorkingDirectory = $preparation.WorkingDirectory
            ProvenanceUrl    = $provenanceUrl
            MissingOutputs   = $missing
            State            = $(if ($missing.Count -eq 0) { 'Present' } else { 'Missing' })
        }
    }
}

function Write-SampleArtifactDisclosure {
    <#
    .SYNOPSIS
    Prints what each artifact is, where it comes from, and the terms it carries.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Artifact,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Preparation
    )

    if ($Artifact.Count -eq 0 -and $Preparation.Count -eq 0) {
        Write-Host '  This sample needs no downloaded model files.'
        return
    }

    foreach ($item in $Artifact) {
        $marker = '[ ]'
        $color = 'Yellow'
        if ($item.State -eq 'Present') { $marker = '[x]'; $color = 'Green' }
        if ($item.State -eq 'Altered') { $marker = '[!]'; $color = 'Red' }

        Write-Host "  $marker $($item.DisplayName)" -ForegroundColor $color
        Write-Host "      Purpose:     $($item.Purpose)"
        Write-Host "      Source:      $($item.SourceUrl)"
        Write-Host "      Source page: $($item.ProvenanceUrl)"
        Write-Host "      Size:        $(Format-SampleByteSize -Bytes $item.SizeBytes)"
        Write-Host "      Destination: $($item.Destination)"
        Write-Host "      Status:      $($item.State). $($item.Detail)"
        Write-Host ''
    }

    foreach ($item in $Preparation) {
        $marker = '[ ]'
        $color = 'Yellow'
        if ($item.State -eq 'Present') { $marker = '[x]'; $color = 'Green' }

        Write-Host "  $marker $($item.DisplayName) (produced on this machine)" -ForegroundColor $color
        Write-Host "      What it does: $($item.Description)"
        if ($item.ProvenanceUrl) {
            Write-Host "      Source page:  $($item.ProvenanceUrl)"
        }
        Write-Host "      Requires:     $($item.Requires)"
        Write-Host "      Status:       $($item.State)"
        Write-Host ''
    }
}

function Write-SampleAcquisitionPlan {
    <#
    .SYNOPSIS
    Prints the commands that obtain everything still missing.

    .DESCRIPTION
    The samples do not run these for you. Each command names one source and one
    destination so you can review it before running it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Artifact,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Preparation
    )

    $downloads = @($Artifact | Where-Object { $_.State -ne 'Present' })
    $preparations = @($Preparation | Where-Object { $_.State -ne 'Present' })
    if ($downloads.Count -eq 0 -and $preparations.Count -eq 0) { return }

    Write-Host 'To obtain what is missing, review and then run:' -ForegroundColor Cyan
    Write-Host ''

    foreach ($item in $downloads) {
        $directory = Split-Path -Parent $item.Destination
        Write-Host "  # $($item.DisplayName)"
        Write-Host "  #   Review the publisher's terms first: $($item.ProvenanceUrl)"
        Write-Host "  New-Item -ItemType Directory -Force -Path `"$directory`" | Out-Null"
        Write-Host "  curl.exe -L --fail --output `"$($item.Destination)`" `"$($item.SourceUrl)`""
        Write-Host ''
    }

    foreach ($item in $preparations) {
        Write-Host "  # $($item.DisplayName)"
        Write-Host "  #   $($item.Description)"
        Write-Host "  #   Run from $($item.WorkingDirectory)"
        Write-Host "  $($item.Command)"
        Write-Host ''
    }

    Write-Host 'Then run check_artifacts.ps1 to confirm the files match the catalog.' -ForegroundColor Cyan
}

function Test-SampleInteractiveHost {
    <#
    .SYNOPSIS
    Reports whether this session can prompt a person.
    #>
    [CmdletBinding()]
    param()

    if ($env:WINDOWSML_SAMPLES_NONINTERACTIVE) { return $false }
    if ([System.Console]::IsInputRedirected) { return $false }
    return $true
}

function Assert-SampleArtifactsReady {
    <#
    .SYNOPSIS
    Describes a sample, verifies its artifacts, and waits for you to continue.

    .DESCRIPTION
    This is the single entry point every run script uses. It prints what the
    sample does and what it needs, reports the state of each artifact, and stops
    with actionable guidance when something is missing. Nothing is downloaded and
    no preparation command is executed.

    .PARAMETER NonInteractive
    Skip the confirmation prompt. Checks still run and still fail when an
    artifact is missing. Automation uses this; it never causes an acquisition.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Sample,
        [Parameter(Mandatory = $true)][string]$ModelsDirectory,
        [string[]]$Note = @(),
        [switch]$NonInteractive,
        [switch]$SkipHash
    )

    $definition = Get-SampleDefinition -Sample $Sample
    $artifacts = @(Get-SampleArtifactStatus -Sample $Sample -ModelsDirectory $ModelsDirectory -SkipHash:$SkipHash)
    $preparations = @(Get-SamplePreparationStatus -Sample $Sample -ModelsDirectory $ModelsDirectory)

    Write-Host ''
    Write-Host "== $($definition.Title)" -ForegroundColor Cyan
    Write-Host "   $($definition.Summary)"
    foreach ($line in $Note) { Write-Host "   $line" }
    Write-Host ''
    Write-Host 'Required artifacts' -ForegroundColor Cyan
    Write-SampleArtifactDisclosure -Artifact $artifacts -Preparation $preparations

    $blocked = @($artifacts | Where-Object { $_.State -ne 'Present' }) +
               @($preparations | Where-Object { $_.State -ne 'Present' })
    if ($blocked.Count -gt 0) {
        Write-SampleAcquisitionPlan -Artifact $artifacts -Preparation $preparations
        throw "This sample cannot run yet: $($blocked.Count) required artifact(s) are missing or altered."
    }

    $interactive = (-not $NonInteractive) -and (Test-SampleInteractiveHost)
    if ($interactive) {
        Write-Host 'Everything this sample needs is present.' -ForegroundColor Green
        Write-Host 'Press Enter to run it, or Ctrl+C to cancel: ' -ForegroundColor Cyan -NoNewline
        [void][System.Console]::ReadLine()
        Write-Host ''
    }
    else {
        Write-Host 'Everything this sample needs is present. Continuing without prompting.' -ForegroundColor Green
    }
}

function Get-SampleBackendOptions {
    <#
    .SYNOPSIS
    Returns the execution choices a sample offers, with readiness for each.

    .DESCRIPTION
    Readiness is derived from the artifact set each choice needs, so a run script
    can show every supported backend and mark which ones this machine can run
    right now instead of presenting one default as the only option.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Scenario,
        [Parameter(Mandatory = $true)][string]$ModelsDirectory
    )

    $catalog = Get-SampleArtifactCatalog
    if (-not $catalog.Backends.ContainsKey($Scenario)) {
        $known = ($catalog.Backends.Keys | Sort-Object) -join ', '
        throw "Unknown backend scenario '$Scenario'. Known scenarios: $known"
    }

    foreach ($option in @($catalog.Backends[$Scenario].Options)) {
        $ready = $true
        $reason = 'Ready.'
        $requiresModelPath = $false
        if ($option.ContainsKey('RequiresModelPath')) {
            $requiresModelPath = [bool]$option.RequiresModelPath
        }

        if ($requiresModelPath) {
            $ready = $false
            $reason = 'Needs a model path.'
        }
        elseif ($option.Sample) {
            $missing = @(
                @(Get-SampleArtifactStatus -Sample $option.Sample -ModelsDirectory $ModelsDirectory -SkipHash |
                    Where-Object { $_.State -ne 'Present' }) +
                @(Get-SamplePreparationStatus -Sample $option.Sample -ModelsDirectory $ModelsDirectory |
                    Where-Object { $_.State -ne 'Present' })
            )
            if ($missing.Count -gt 0) {
                $ready = $false
                $reason = "Missing $($missing.Count) artifact(s)."
            }
        }

        [pscustomobject]@{
            Id                = $option.Id
            Title             = $option.Title
            Summary           = $option.Summary
            Sample            = $option.Sample
            RequiresModelPath = $requiresModelPath
            Guidance          = $(if ($option.ContainsKey('Guidance')) { @($option.Guidance) } else { @() })
            IsReady           = $ready
            Reason            = $reason
        }
    }
}

function Assert-SampleBackendModelPath {
    <#
    .SYNOPSIS
    Explains what a backend still needs before the sample can run.

    .DESCRIPTION
    Prints guidance when the selected backend needs a model path supplied by the
    caller.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Scenario,
        [Parameter(Mandatory = $true)][string]$Backend,
        [Parameter(Mandatory = $true)][string]$ModelsDirectory,
        [string]$ModelPath
    )

    $option = @(Get-SampleBackendOptions -Scenario $Scenario -ModelsDirectory $ModelsDirectory |
        Where-Object { $_.Id -eq $Backend })
    if ($option.Count -eq 0) { return }
    if (-not $option[0].RequiresModelPath) { return }
    if ($ModelPath) { return }

    Write-Host ''
    Write-Host "The '$Backend' backend needs a model you supply." -ForegroundColor Yellow
    Write-Host "  $($option[0].Summary)"
    Write-Host ''
    foreach ($line in $option[0].Guidance) { Write-Host "  $line" }
    Write-Host ''

    $ready = @(Get-SampleBackendOptions -Scenario $Scenario -ModelsDirectory $ModelsDirectory |
        Where-Object { $_.IsReady })
    if ($ready.Count -gt 0) {
        Write-Host "Backends ready on this machine: $(($ready.Id) -join ', ')" -ForegroundColor Cyan
        Write-Host ''
    }

    throw "The '$Backend' backend requires a model path. See the guidance above."
}

function Resolve-SampleBackend {
    <#
    .SYNOPSIS
    Shows every backend a sample supports and returns the one to run.

    .DESCRIPTION
    When a backend was requested, it is returned unchanged. Otherwise the
    supported choices are listed with their readiness, and an interactive session
    is asked to pick one.

    .PARAMETER Requested
    Backend supplied on the command line. Empty selects interactively.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Scenario,
        [Parameter(Mandatory = $true)][string]$ModelsDirectory,
        [string]$Requested,
        [switch]$NonInteractive
    )

    $catalog = Get-SampleArtifactCatalog
    $options = @(Get-SampleBackendOptions -Scenario $Scenario -ModelsDirectory $ModelsDirectory)

    if ($Requested) {
        if ($options.Id -notcontains $Requested) {
            throw "Backend '$Requested' is not supported here. Supported: $(($options.Id) -join ', ')"
        }
        return $Requested
    }

    Write-Host ''
    Write-Host 'Supported backends' -ForegroundColor Cyan
    for ($index = 0; $index -lt $options.Count; $index++) {
        $option = $options[$index]
        $state = 'ready'
        $color = 'Green'
        if (-not $option.IsReady) { $state = $option.Reason; $color = 'Yellow' }
        Write-Host ("  {0}. {1} [-Backend {2}]" -f ($index + 1), $option.Title, $option.Id) -ForegroundColor $color
        Write-Host "     $($option.Summary)"
        Write-Host "     $state"
    }
    Write-Host ''

    $default = $catalog.Backends[$Scenario].Default
    $firstReady = @($options | Where-Object { $_.IsReady -and $_.Id -eq $default })
    if ($firstReady.Count -eq 0) {
        $anyReady = @($options | Where-Object { $_.IsReady })
        if ($anyReady.Count -gt 0) { $default = $anyReady[0].Id }
    }

    if ($NonInteractive -or -not (Test-SampleInteractiveHost)) {
        Write-Host "No backend was requested. Using -Backend $default." -ForegroundColor Cyan
        return $default
    }

    Write-Host "Choose a backend by number or name, or press Enter for '$default': " -ForegroundColor Cyan -NoNewline
    $answer = [System.Console]::ReadLine()
    Write-Host ''
    if (-not $answer) { return $default }

    $answer = $answer.Trim()
    $number = 0
    if ([int]::TryParse($answer, [ref]$number) -and $number -ge 1 -and $number -le $options.Count) {
        return $options[$number - 1].Id
    }
    if ($options.Id -contains $answer) { return $answer }
    throw "'$answer' is not one of the supported backends: $(($options.Id) -join ', ')"
}

Export-ModuleMember -Function @(
    'Get-SampleArtifactCatalog',
    'Get-SampleDefinition',
    'Get-SampleArtifactStatus',
    'Get-SamplePreparationStatus',
    'Get-SampleFileSha256',
    'Get-SampleBackendOptions',
    'Resolve-SampleBackend',
    'Assert-SampleBackendModelPath',
    'Write-SampleArtifactDisclosure',
    'Write-SampleAcquisitionPlan',
    'Test-SampleInteractiveHost',
    'Assert-SampleArtifactsReady',
    'Format-SampleByteSize'
)
