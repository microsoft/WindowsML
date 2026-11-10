# Copyright (C) Microsoft Corporation. All rights reserved.

<#
.SYNOPSIS
Generates a Windows ML rundown log (WinmlRundown.log) from an ETW trace.

.DESCRIPTION
Exports the Windows ML and ONNX Runtime events selected by the WPA profile to CSV files with
wpaexporter.exe, then writes them to WinmlRundown.log grouped by process. The log also includes
a CPU fallback summary for each session. If the Windows Performance Toolkit is not installed,
the script installs it from the Windows ADK.

.PARAMETER EtlFilePath
ETL file to analyze.

.PARAMETER WpaProfilePath
WPA profile that selects the exported events (WindowsMLProfile.wpaProfile).

.PARAMETER OutputFolder
Folder that receives WinmlRundown.log.
#>
param (
    [Parameter(Mandatory = $true)]
    [string]$EtlFilePath,

    [Parameter(Mandatory = $true)]
    [string]$WpaProfilePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputFolder
)

# Rundown sections, keyed by WPA preset name. wpaexporter writes each preset to
# Generic_Events_<preset>.csv and names TraceLogging payload fields by position ("Field N").
# ONNX Runtime events start with UTCReplace_AppSessionGuid and PartA_PrivTags, so their
# schemaVersion is Field 3. A section with several layouts uses the first one whose When
# condition matches the row.
$RundownSections = @{
    OnnxVersion = @{
        Title   = "Onnx Version"
        Layouts = @(
            @{
                # ONNX Runtime 1.29 added cpuModel, processorCount, and totalMemoryMB after runtimeVersion
                When   = { param($row) (ConvertTo-Version $row."Field 4") -ge [version]"1.29" }
                Fields = [ordered]@{
                    "Field 4"  = "Runtime Version"
                    "Field 9"  = "Is Redist"
                    "Field 10" = "Framework Name"
                }
            },
            @{
                Fields = [ordered]@{
                    "Field 4" = "Runtime Version"
                    "Field 6" = "Is Redist"
                    "Field 7" = "Framework Name"
                }
            }
        )
    }
    winmlVersion = @{
        Title    = "WindowsAppSDK.ML Version"
        Distinct = @("Version")
        Fields   = [ordered]@{
            "Field 5" = "Version"
        }
    }
    WinMLRuntimeState = @{
        # Logged by Windows ML when a trace starts or is saved (ETW capture state)
        Title    = "WinML Runtime State"
        Distinct = @("WinML Version", "ORT Version", "Registered EPs")
        Fields   = [ordered]@{
            "Field 1" = "WinML Version"
            "Field 2" = "ORT Version"
            "Field 3" = "Registered EPs"
        }
    }
    WinMLRegisteredExecutionProvider = @{
        Title    = "WinML Registered EP"
        Distinct = @("EP Name", "Package Family Name", "Library Path")
        Fields   = [ordered]@{
            "Field 1" = "EP Name"
            "Field 2" = "Package Family Name"
            "Field 3" = "Library Path"
        }
    }
    DriverInfo = @{
        Title  = "Driver Info"
        Fields = [ordered]@{
            "Field 4" = "Device Class"
            "Field 5" = "Driver Name"
            "Field 6" = "Driver Version"
        }
    }
    RegisteredProviders = @{
        Title  = "Registered Providers"
        Fields = [ordered]@{
            "Field 1" = "PackageFamilyName"
        }
    }
    EpLibraryRegistration = @{
        Title   = "EP Library Registration"
        Layouts = @(
            @{
                When   = { param($row) $row."Task Name" -eq "RegisterEpLibraryWithLibPath" }
                Fields = [ordered]@{
                    "Field 4" = "EP Name"
                    "Field 5" = "Library Path"
                }
            },
            @{
                Fields = [ordered]@{
                    "Field 4" = "EP Name"
                    "Field 5" = "Success"
                    "Field 8" = "Error Message"
                }
            }
        )
    }
    sessioncreation = @{
        Title  = "Session Creation"
        Fields = [ordered]@{
            "Field 3"  = "Schema Version"
            "Field 4"  = "Session ID"
            "Field 5"  = "IR Version"
            "Field 6"  = "ORT Programming Projection"
            "Field 10" = "Using FP16"
            "Field 14" = "Model Weight Type"
            "Field 15" = "Model Graph Hash"
            "Field 16" = "Model Weight Hash"
            "Field 19" = "EP ID"
        }
    }
    SessionCreationCaptureState = @{
        # Sessions that were open when the trace started or was saved, logged again by ETW capture state
        Title    = "Session Creation (capture state)"
        Distinct = @("Session ID", "Model Graph Hash", "EP ID")
        Layouts  = @(
            @{
                # Schema 2 (ONNX Runtime 1.28) added executionProviderVersions after hardwareVendorIds
                When   = { param($row) [int]$row."Field 3" -ge 2 }
                Fields = [ordered]@{
                    "Field 3"  = "Schema Version"
                    "Field 4"  = "Session ID"
                    "Field 5"  = "IR Version"
                    "Field 6"  = "ORT Programming Projection"
                    "Field 10" = "Using FP16"
                    "Field 14" = "Model Weight Type"
                    "Field 15" = "Model Graph Hash"
                    "Field 16" = "Model Weight Hash"
                    "Field 19" = "EP ID"
                    "Field 20" = "Device Types"
                    "Field 22" = "EP Versions"
                }
            },
            @{
                # Schema 1 (ONNX Runtime 1.27) added hardwareDeviceTypes after executionProviderIds
                When   = { param($row) $row."Field 3" -eq "1" }
                Fields = [ordered]@{
                    "Field 3"  = "Schema Version"
                    "Field 4"  = "Session ID"
                    "Field 5"  = "IR Version"
                    "Field 6"  = "ORT Programming Projection"
                    "Field 10" = "Using FP16"
                    "Field 14" = "Model Weight Type"
                    "Field 15" = "Model Graph Hash"
                    "Field 16" = "Model Weight Hash"
                    "Field 19" = "EP ID"
                    "Field 20" = "Device Types"
                }
            },
            @{
                Fields = [ordered]@{
                    "Field 3"  = "Schema Version"
                    "Field 4"  = "Session ID"
                    "Field 5"  = "IR Version"
                    "Field 6"  = "ORT Programming Projection"
                    "Field 10" = "Using FP16"
                    "Field 14" = "Model Weight Type"
                    "Field 15" = "Model Graph Hash"
                    "Field 16" = "Model Weight Hash"
                    "Field 19" = "EP ID"
                }
            }
        )
    }
    EpAutoSelection = @{
        Title  = "EP Auto Selection"
        Fields = [ordered]@{
            "Field 3" = "Schema Version"
            "Field 4" = "Session ID"
            "Field 5" = "Selection Policy"
            "Field 6" = "Requested EP"
            "Field 7" = "Available EP"
        }
    }
    EpDeviceUsage = @{
        Title    = "EP Device Usage"
        Distinct = @("Session ID", "Execution Provider", "Device Type", "Hardware Vendor", "Assigned Nodes")
        Layouts  = @(
            @{
                # Schema 0 (ONNX Runtime 1.27) has no epVersion field
                When   = { param($row) $row."Field 3" -eq "0" }
                Fields = [ordered]@{
                    "Field 3"  = "Schema Version"
                    "Field 4"  = "Session ID"
                    "Field 5"  = "Execution Provider"
                    "Field 6"  = "Device Type"
                    "Field 9"  = "Hardware Vendor"
                    "Field 11" = "Assigned Nodes"
                }
            },
            @{
                Fields = [ordered]@{
                    "Field 3"  = "Schema Version"
                    "Field 4"  = "Session ID"
                    "Field 5"  = "Execution Provider"
                    "Field 6"  = "Device Type"
                    "Field 9"  = "Hardware Vendor"
                    "Field 11" = "EP Version"
                    "Field 12" = "Assigned Nodes"
                }
            }
        )
    }
    winmlOnnxError = @{
        Title  = "====================WINML ONNX ERROR==================="
        Fields = [ordered]@{
            "Field 3"  = "Schema Version"
            "Field 4"  = "HRESULT"
            "Field 5"  = "Session ID"
            "Field 6"  = "Error Code"
            "Field 7"  = "Error Category"
            "Field 8"  = "Error Message"
            "Field 9"  = "File"
            "Field 10" = "function"
            "Field 11" = "Line"
        }
    }
    onnxerror = @{
        Title  = "====================ONNX ERROR==================="
        Fields = [ordered]@{
            "Field 3" = "Location"
            "Field 4" = "Message"
        }
    }
    providerCertification = @{
        Title  = "Provider Certification"
        Fields = [ordered]@{
            "Field 1" = "Provider"
            "Field 2" = "Ready State"
            "Field 3" = "Certification State"
        }
    }
}

# Device policies that ask for a GPU or NPU; a session that runs only on CPU under one of them fell back
$AcceleratorPolicies = @("PREFER_NPU", "PREFER_GPU", "MAX_PERFORMANCE", "MAX_EFFICIENCY", "MIN_OVERALL_POWER")

function Install-WindowsPerformanceToolkit {
    Write-Host "Windows Performance Toolkit not found. Attempting to install minimal components..."

    try {
        # Check if running as administrator
        $currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        $isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

        if (-not $isAdmin) {
            Write-Host "Administrator privileges required to install Windows Performance Toolkit."
            Write-Host "Please run this script as Administrator or manually install just the Performance Toolkit."
            return $null
        }

        # Primary method: Install Windows Performance Toolkit via Windows ADK
        Write-Host "Attempting to install Windows Performance Toolkit via Windows ADK..."

        try {
            # Detect system architecture
            $arch = $env:PROCESSOR_ARCHITECTURE
            Write-Host "Detected architecture: $arch"

            # Select ADK download URL based on architecture
            # ARM64: Use the ARM64-specific ADK (November 2025)
            # x64/x86: Use the standard Windows 11 ADK (December 2024)
            if ($arch -eq 'ARM64') {
                $adkUrl = "https://go.microsoft.com/fwlink/?linkid=2337875"  # Windows 11 ADK for ARM64 (November 2025)
                $adkVersion = "10.1.28000.1 (ARM64, November 2025)"
            } else {
                $adkUrl = "https://go.microsoft.com/fwlink/?linkid=2289980"  # Windows 11 ADK (December 2024)
                $adkVersion = "10.1.26100.2454 (December 2024)"
            }

            Write-Host "Selected ADK version: $adkVersion"

            $installerPath = "$env:TEMP\adksetup.exe"

            Write-Host "Downloading Windows ADK installer..."
            Write-Host "  URL: $adkUrl"
            Write-Host "  Destination: $installerPath"

            try {
                Invoke-WebRequest -Uri $adkUrl -OutFile $installerPath -UseBasicParsing
                Write-Host "  [OK] Download complete" -ForegroundColor Green
            } catch {
                Write-Host "Failed to download Windows ADK installer: $_" -ForegroundColor Red
                throw
            }

            Write-Host "Installing Windows Performance Toolkit feature..."
            Write-Host "  This may take a few minutes..."

            $installArgs = "/quiet /norestart /features OptionId.WindowsPerformanceToolkit"
            Write-Host "  Running: $installerPath $installArgs"

            $process = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru

            if ($process.ExitCode -eq 0 -or $process.ExitCode -eq 3010) {
                Write-Host "  [OK] Installation complete (Exit code: $($process.ExitCode))" -ForegroundColor Green
            } else {
                Write-Host "Installation failed with exit code: $($process.ExitCode)" -ForegroundColor Red
                throw "ADK installation failed"
            }

            Write-Host "Verifying wpaexporter.exe installation..."

            # Check multiple possible installation paths for wpaexporter.exe
            $possiblePaths = @(
                "$env:ProgramFiles\Windows Kits\10\Windows Performance Toolkit\wpaexporter.exe",
                "${env:ProgramFiles(x86)}\Windows Kits\10\Windows Performance Toolkit\wpaexporter.exe"
            )

            $foundPath = $null
            foreach ($path in $possiblePaths) {
                Write-Host "  Checking: $path"
                if (Test-Path $path) {
                    $foundPath = $path
                    break
                }
            }

            if ($foundPath) {
                Write-Host "===================================================="
                Write-Host "SUCCESS: Windows Performance Toolkit Installed" -ForegroundColor Green
                Write-Host "===================================================="
                Write-Host "wpaexporter.exe found at: $foundPath"

                # Clean up installer
                Remove-Item $installerPath -Force -ErrorAction SilentlyContinue
                return $foundPath  # Return the actual path found
            } else {
                Write-Host "wpaexporter.exe not found after installation" -ForegroundColor Red
                throw "wpaexporter.exe verification failed"
            }
        }
        catch {
            Write-Host "ADK installation failed: $_" -ForegroundColor Red
        }

        Write-Host "Automatic installation failed. Manual installation options:"
        Write-Host "1. Download Windows ADK:"
        Write-Host "   - x64/x86: https://go.microsoft.com/fwlink/?linkid=2289980 (December 2024)"
        Write-Host "   - ARM64:   https://go.microsoft.com/fwlink/?linkid=2337875 (November 2025)"
        Write-Host "   - Run: adksetup.exe /quiet /norestart /features OptionId.WindowsPerformanceToolkit"

        return $null
    }
    catch {
        Write-Host "Error during installation: $_"
        return $null
    }
}

function Get-WpaExporterPath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\Windows Performance Toolkit\wpaexporter.exe",
        "${env:ProgramFiles}\Windows Kits\10\Windows Performance Toolkit\wpaexporter.exe"
    )

    $path = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($path) {
        return $path
    }

    Write-Host "wpaexporter.exe not found in either Program Files location."
    Write-Host "Checked paths:"
    $candidates | ForEach-Object { Write-Host "  - $_" }

    return Install-WindowsPerformanceToolkit
}

# wpaexporter.exe always loads %LOCALAPPDATA%\Windows Performance Analyzer\MyPresets.wpaPresets.
# On machines where WPA has never been launched, the file does not exist and wpaexporter emits a
# noisy FileNotFoundException (and on some setups aborts), so create a minimal valid file.
function Initialize-WpaUserPreset {
    $wpaUserDir = Join-Path $env:LOCALAPPDATA "Windows Performance Analyzer"
    $wpaPresetsFile = Join-Path $wpaUserDir "MyPresets.wpaPresets"
    if (Test-Path $wpaPresetsFile) {
        return
    }

    Write-Host "Creating empty WPA presets file at $wpaPresetsFile to work around wpaexporter quirk..."
    New-Item -ItemType Directory -Path $wpaUserDir -Force | Out-Null
    @'
<?xml version="1.0" encoding="utf-8"?>
<WpaPresetCollection>
</WpaPresetCollection>
'@ | Set-Content -Path $wpaPresetsFile -Encoding UTF8
}

function Export-TraceTable {
    param(
        [string]$WpaExporterPath,
        [string]$EtlFilePath,
        [string]$WpaProfilePath,
        [string]$OutputFolder
    )

    Get-ChildItem -Path $OutputFolder -Filter "Generic_Events_*.csv" | Remove-Item -Force

    Write-Host "Exporting ETL to CSV files using $WpaExporterPath..."
    & $WpaExporterPath -i $EtlFilePath -profile $WpaProfilePath -outputFolder $OutputFolder | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "wpaexporter.exe failed with exit code $LASTEXITCODE. Check that '$EtlFilePath' is a valid ETL file and '$WpaProfilePath' is a valid WPA profile."
    }

    return @(Get-ChildItem -Path $OutputFolder -Filter "Generic_Events_*.csv")
}

function ConvertTo-Version {
    param([string]$Text)

    $version = $null
    if ([version]::TryParse($Text, [ref]$version)) {
        return $version
    }

    return $null
}

function Get-FieldLayout {
    param($Section, $Row)

    if (-not $Section.Layouts) {
        return $Section.Fields
    }

    foreach ($layout in $Section.Layouts) {
        if (-not $layout.When -or (& $layout.When $Row)) {
            return $layout.Fields
        }
    }
}

# Converts the exported CSV rows to records: Process, ProcessId, Time, Title, and the named Fields
function Import-RundownRecord {
    param([System.IO.FileInfo[]]$CsvFiles)

    foreach ($file in $CsvFiles) {
        $preset = $file.BaseName -replace "^Generic_Events_", ""
        $section = $RundownSections[$preset]
        if (-not $section) {
            continue
        }

        foreach ($row in (Import-Csv $file.FullName)) {
            $layout = Get-FieldLayout -Section $section -Row $row
            $fields = [ordered]@{}
            foreach ($column in $layout.Keys) {
                $value = $row.$column
                if ($value -and $value.Trim() -ne "") {
                    $fields[$layout[$column]] = $value
                }
            }

            [pscustomobject]@{
                Process   = $row.Process
                ProcessId = $row."Process Id"
                Time      = $row."Time (Local)"
                Title     = $section.Title
                Distinct  = $section.Distinct
                Fields    = $fields
            }
        }
    }
}

# Removes repeated records of sections that declare Distinct fields, per process
function Select-DistinctRecord {
    param([object[]]$Records)

    $seen = @{}
    foreach ($record in $Records) {
        if ($record.Distinct) {
            $values = $record.Distinct | ForEach-Object { $record.Fields[$_] }
            $key = "$($record.ProcessId)|$($record.Process)|$($record.Title)|$($values -join '|')"
            if ($seen.ContainsKey($key)) {
                continue
            }

            $seen[$key] = $true
        }

        $record
    }
}

# ONNX Runtime logs one EP Device Usage row per device of an execution provider, and every row carries the
# provider's node count, so each provider is counted once with the largest count across its rows
function Get-ExecutionProviderUsage {
    param([object[]]$Devices)

    foreach ($provider in ($Devices | Group-Object { $_.Fields["Execution Provider"] })) {
        $deviceTypes = @($provider.Group | ForEach-Object { $_.Fields["Device Type"] } | Sort-Object -Unique)
        $nodes = $provider.Group | ForEach-Object { [int]("$($_.Fields['Assigned Nodes'])" -replace "[^0-9]", "") } |
            Measure-Object -Maximum

        [pscustomobject]@{
            Name        = $provider.Name
            DeviceTypes = $deviceTypes
            Nodes       = [int]$nodes.Maximum
            CpuOnly     = -not ($deviceTypes | Where-Object { $_ -ne "CPU" })
        }
    }
}

function Get-CpuFallbackResult {
    param([object[]]$Devices, [string]$Policy)

    $providers = @(Get-ExecutionProviderUsage $Devices)

    # A provider's single node count covers all of its devices, so a provider on both CPU and an
    # accelerator does not show how many of its nodes ran on CPU
    $mixed = @($providers | Where-Object { -not $_.CpuOnly -and $_.DeviceTypes -contains "CPU" -and $_.Nodes -gt 0 })
    if ($mixed.Count -gt 0) {
        $names = ($mixed | ForEach-Object { "$($_.Name) ($($_.DeviceTypes -join ', '))" }) -join ", "
        return "UNKNOWN - $names was assigned nodes on both CPU and an accelerator; the trace does not show how many ran on CPU"
    }

    $cpuNodes = [int]($providers | Where-Object CpuOnly | Measure-Object -Property Nodes -Sum).Sum
    $accelerators = @($providers | Where-Object { -not $_.CpuOnly })
    $acceleratorNodes = [int]($accelerators | Measure-Object -Property Nodes -Sum).Sum

    if ($accelerators.Count -gt 0 -and $acceleratorNodes -eq 0) {
        $names = ($accelerators | ForEach-Object { "$($_.Name) ($($_.DeviceTypes -join ', '))" }) -join ", "
        return "FULL - all $cpuNodes nodes ran on CPU; $names was selected but assigned 0 nodes"
    }

    if ($accelerators.Count -gt 0 -and $cpuNodes -gt 0) {
        return "PARTIAL - $cpuNodes of $($cpuNodes + $acceleratorNodes) nodes ran on CPU"
    }

    if ($accelerators.Count -gt 0) {
        $deviceTypes = ($accelerators.DeviceTypes | Where-Object { $_ -ne "CPU" } | Sort-Object -Unique) -join " and "
        return "NONE - all $acceleratorNodes nodes ran on $deviceTypes"
    }

    if ($Policy -in $AcceleratorPolicies) {
        return "FULL - all $cpuNodes nodes ran on CPU; policy $Policy selected no GPU or NPU execution provider"
    }

    return "N/A - the session uses only CPU execution providers"
}

# Builds one CPU Fallback record per session from the EP Device Usage records. Sessions and device
# policies are matched by process ID and process, so they stay apart when the trace has no process names.
function Get-CpuFallbackRecord {
    param([object[]]$Records)

    # Device policies by process ID, process, and session. Policies are also kept by process and session
    # for selection records without a process ID, such as ones exported with an older WPA profile.
    $policiesById = @{}
    $policiesByName = @{}
    foreach ($selection in ($Records | Where-Object { $_.Title -eq "EP Auto Selection" })) {
        $sessionId = $selection.Fields["Session ID"]
        if ($selection.ProcessId) {
            $policiesById["$($selection.ProcessId)|$($selection.Process)|$sessionId"] = $selection.Fields["Selection Policy"]
        }
        if ($selection.Process -ne "Unknown") {
            $policiesByName["$($selection.Process)|$sessionId"] = $selection.Fields["Selection Policy"]
        }
    }

    $usage = $Records | Where-Object { $_.Title -eq "EP Device Usage" }
    foreach ($session in ($usage | Group-Object { "$($_.ProcessId)|$($_.Process)|$($_.Fields['Session ID'])" })) {
        $first = $session.Group | Sort-Object Time | Select-Object -First 1
        $devices = $session.Group |
            Group-Object { "$($_.Fields['Execution Provider'])|$($_.Fields['Device Type'])" } |
            ForEach-Object { $_.Group[0] }
        $sessionId = $first.Fields["Session ID"]
        $policy = $policiesById["$($first.ProcessId)|$($first.Process)|$sessionId"]
        if (-not $policy -and $first.Process -ne "Unknown") {
            $policy = $policiesByName["$($first.Process)|$sessionId"]
        }

        [pscustomobject]@{
            Process   = $first.Process
            ProcessId = $first.ProcessId
            Time      = $first.Time
            Title     = "CPU Fallback"
            Distinct  = $null
            Fields    = [ordered]@{
                "Session ID" = $first.Fields["Session ID"]
                "Result"     = Get-CpuFallbackResult -Devices $devices -Policy $policy
            }
        }
    }
}

function Write-RundownLog {
    param([object[]]$Records, [string]$Path)

    # Without kernel events every process is named "Unknown", so records that carry a process ID are
    # grouped per process ID instead
    $lines = New-Object System.Collections.Generic.List[string]
    $processes = $Records |
        Group-Object { if ($_.Process -eq "Unknown" -and $_.ProcessId) { "Unknown (PID $($_.ProcessId))" } else { $_.Process } } |
        Sort-Object { $_.Group.Time | Where-Object { $_ } | Sort-Object | Select-Object -First 1 }

    foreach ($process in $processes) {
        $lines.Add("Process: $($process.Name)")
        $lines.Add("----------------------------------------")

        # Records without a time (for example, WindowsAppSDK.ML Version) are written last
        $sortedRecords = $process.Group | Sort-Object @{ Expression = { -not $_.Time } }, Time
        foreach ($record in $sortedRecords) {
            if ($record.Fields.Count -eq 0) {
                continue
            }

            $timeValue = if ($record.Time) { " ($($record.Time))" } else { "" }
            $lines.Add("`t$($record.Title)$timeValue :")
            foreach ($name in $record.Fields.Keys) {
                $lines.Add("`t`t$name = $($record.Fields[$name])")
            }
            $lines.Add("")
        }

        $lines.Add("")
        $lines.Add("")
    }

    try {
        Set-Content -Path $Path -Value $lines -Encoding UTF8 -ErrorAction Stop
    }
    catch {
        throw "Failed to write ${Path}: $($_.Exception.Message)"
    }
}

if (-not (Test-Path $OutputFolder)) {
    New-Item -ItemType Directory -Path $OutputFolder | Out-Null
}

$LogFilePath = Join-Path -Path $OutputFolder -ChildPath "WinmlRundown.log"

$wpaExporterPath = Get-WpaExporterPath
if (-not $wpaExporterPath) {
    Write-Host "Unable to install Windows Performance Toolkit automatically."
    exit 1
}

Initialize-WpaUserPreset

$csvFiles = Export-TraceTable -WpaExporterPath $wpaExporterPath -EtlFilePath $EtlFilePath -WpaProfilePath $WpaProfilePath -OutputFolder $OutputFolder
if ($csvFiles.Count -eq 0) {
    Set-Content -Path $LogFilePath -Value "No CSV files found."
    exit
}

$records = @(Import-RundownRecord -CsvFiles $csvFiles)
if ($records.Count -eq 0) {
    Set-Content -Path $LogFilePath -Value "No data available."
    exit
}

if (-not ($records | Where-Object { $_.Process })) {
    Set-Content -Path $LogFilePath -Value "No 'Process' column found."
    exit
}

$records = @(Select-DistinctRecord -Records $records)
$records += @(Get-CpuFallbackRecord -Records $records)

Write-RundownLog -Records $records -Path $LogFilePath
Write-Host "Log file created successfully."

$csvFiles | Remove-Item -Force
Write-Host "Intermediate CSV files deleted."
