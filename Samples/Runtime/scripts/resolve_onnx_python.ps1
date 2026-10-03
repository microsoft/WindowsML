# Copyright (C) Microsoft Corporation. All rights reserved.

function Invoke-NativeProbe {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList = @()
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $nativePreferenceVariable =
        Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $previousNativePreference =
        if ($nativePreferenceVariable) {
            $nativePreferenceVariable.Value
        }
        else {
            $null
        }

    try {
        $ErrorActionPreference = "Continue"
        if ($nativePreferenceVariable) {
            $PSNativeCommandUseErrorActionPreference = $false
        }
        $output = @(& $FilePath @ArgumentList 2>$null)
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = $output
        }
    }
    catch {
        return [pscustomobject]@{
            ExitCode = 1
            Output = @()
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($nativePreferenceVariable) {
            $PSNativeCommandUseErrorActionPreference = $previousNativePreference
        }
    }
}

function Test-OnnxPython([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $probe = Invoke-NativeProbe `
        -FilePath $Path `
        -ArgumentList @(
            "-c",
            "import onnx; assert onnx.__version__ == '1.22.0'")
    return $probe.ExitCode -eq 0
}

function Get-OnnxPython {
    $command = Get-Command python -ErrorAction SilentlyContinue
    if ($command -and (Test-OnnxPython $command.Source)) {
        return $command.Source
    }

    $toolRoot = Join-Path $env:LOCALAPPDATA "WinMLSampleTools\onnx-1.22"
    $toolPython = Join-Path $toolRoot "Scripts\python.exe"
    if (Test-OnnxPython $toolPython) {
        return $toolPython
    }

    $basePython = $null
    if ($command) {
        $versionProbe = Invoke-NativeProbe `
            -FilePath $command.Source `
            -ArgumentList @(
                "-c",
                "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')")
        $version = $versionProbe.Output | Select-Object -First 1
        if ($versionProbe.ExitCode -eq 0 -and
            $version -match '^3\.(11|12|13|14)$') {
            $basePython = $command.Source
        }
    }
    if (-not $basePython) {
        $uv = Get-Command uv -ErrorAction SilentlyContinue
        if ($uv) {
            $uvProbe = Invoke-NativeProbe `
                -FilePath $uv.Source `
                -ArgumentList @("python", "find", "3.11")
            $candidate = $uvProbe.Output | Select-Object -First 1
            if ($uvProbe.ExitCode -eq 0 -and
                (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                $basePython = $candidate
            }
        }
    }
    if (-not $basePython) {
        throw "Preparing ONNX assets requires Python 3.11-3.14 or uv with Python 3.11."
    }

    if (Test-Path -LiteralPath $toolRoot) {
        Remove-Item -LiteralPath $toolRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $toolRoot) | Out-Null
    & $basePython -m venv $toolRoot | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create the ONNX preparation environment."
    }
    & $toolPython -m pip install --disable-pip-version-check "onnx==1.22.0" | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-OnnxPython $toolPython)) {
        Remove-Item -LiteralPath $toolRoot -Recurse -Force -ErrorAction SilentlyContinue
        throw "Failed to install onnx==1.22.0 for model preparation."
    }
    return $toolPython
}
