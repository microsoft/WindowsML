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

# Starts WinMLServer.exe, reads the address and access key from its output,
# runs the client with them in the client's environment, and stops the server
# when the client exits. Returns the client's exit code.
function Invoke-ServerWithClient {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ServerExe,
        [string[]]$ServerArguments = @(),
        [Parameter(Mandatory = $true)]
        [string[]]$ClientCommand
    )

    # The script reads the server's standard output to get the address and key.
    # Errors still go to this console.
    $serverStart = New-Object System.Diagnostics.ProcessStartInfo
    $serverStart.FileName = $ServerExe
    $serverStart.Arguments = ConvertTo-CommandLine $ServerArguments
    $serverStart.UseShellExecute = $false
    $serverStart.RedirectStandardOutput = $true
    $server = [System.Diagnostics.Process]::Start($serverStart)
    try {
        # Once the server is listening, it writes the access key, then its
        # address, then one line for each model it serves. Loading the model can
        # take a while.
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

        # Only the client's environment gets the key. It never appears on a
        # command line, where other users on the computer could read it.
        $clientStart = New-Object System.Diagnostics.ProcessStartInfo
        $clientStart.FileName = $ClientCommand[0]
        $clientStart.Arguments = ConvertTo-CommandLine @($ClientCommand | Select-Object -Skip 1)
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
    return $exitCode
}
