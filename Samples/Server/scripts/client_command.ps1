# Copyright (C) Microsoft Corporation. All rights reserved.

# Returns the command that runs a client: the program first, then its
# arguments. Build the C++ and C# clients with build.ps1 first. The Python
# client runs with the python.exe on PATH.
function Get-ServerClientCommand {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("cpp", "python", "csharp")]
        [string]$Client,
        [Parameter(Mandatory = $true)]
        [string]$Platform,
        [Parameter(Mandatory = $true)]
        [string]$Configuration
    )

    $root = Split-Path -Parent $PSScriptRoot
    switch ($Client) {
        "cpp" {
            $exe = Join-Path $root "out\$Platform\$Configuration\server-client-cpp\server-client-cpp.exe"
            if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
                throw "The C++ client was not found. Build it first: .\build.ps1 -Sample client-cpp"
            }
            return @($exe)
        }
        "csharp" {
            $exe = Join-Path $root "client\csharp\bin\$Platform\$Configuration\net8.0\ServerClient.CSharp.exe"
            if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
                throw "The C# client was not found. Build it first: .\build.ps1 -Sample client-csharp"
            }
            return @($exe)
        }
        "python" {
            $python = Get-Command python.exe -ErrorAction SilentlyContinue
            if (-not $python) {
                throw "Python was not found. Install Python 3.10 or later, then run: python.exe -m pip install openai"
            }
            return @($python.Source, (Join-Path $root "client\python\main.py"))
        }
    }
}

# Joins arguments into one command line that the program splits back into the
# same arguments. Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList,
# so the arguments are quoted here.
function ConvertTo-CommandLine {
    param(
        [string[]]$Arguments = @()
    )

    $quoted = foreach ($argument in $Arguments) {
        if ($argument -and $argument -notmatch '[\s"]') {
            $argument
        }
        else {
            # Backslashes are literal unless they come before a quote, so
            # those are doubled and each quote is escaped.
            $escaped = ($argument -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1'
            '"' + $escaped + '"'
        }
    }
    return $quoted -join ' '
}
