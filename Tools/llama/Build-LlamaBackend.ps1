# Copyright (C) Microsoft Corporation. All rights reserved.

#Requires -Version 7

<#
.SYNOPSIS
Builds a llama.cpp backend for a ggml family, such as CUDA, that matches a LibLlama.Core package.

.DESCRIPTION
Windows ML ships only the CPU backend, in
Microsoft.Windows.AI.MachineLearning.LibLlama.Core. It does not ship or
redistribute GPU backends or vendor runtimes. Use this script to build a
ggml backend family such as cuda, vulkan, or opencl from upstream llama.cpp.

For cuda, you install the NVIDIA CUDA Toolkit yourself, and you are responsible
for accepting and complying with NVIDIA's license terms for the toolkit and for
any NVIDIA runtime libraries (such as cuBLAS) that you copy next to the backend
or distribute with your application. Windows ML does not accept those terms for
you.

The script reads the llama.cpp build and backend contract from an exact
Microsoft.Windows.AI.MachineLearning.LibLlama.Core NuGet, builds upstream
ggml-<family> at that commit against the WinML-namespaced core with the hybrid
CRT, verifies its imports, and stages it with a matching
WinMLLlamaBackendManifest.json beside a GGUF-capable sample.

.PARAMETER Backend
ggml backend family to build, for example cuda or vulkan. The family selects
the upstream ggml-<family> target, the GGML_<FAMILY> option, and the
LlamaBackends\<family> directory. The CPU backend ships in the Core package and
can't be built here.

.PARAMETER CMakeOption
Additional -D options for the llama.cpp configure step, for example
-DCUDAToolkit_ROOT=<toolkit directory> or -DGGML_OPENCL_USE_ADRENO_KERNELS=OFF.
Only GGML_<FAMILY>_* options for the selected family and <Package>_ROOT search
hints are accepted. Toolchain, compiler, CRT, flag, and core ggml options are
rejected because they could break the Core contract.

.PARAMETER CorePackage
Exact Microsoft.Windows.AI.MachineLearning.LibLlama.Core `.nupkg` whose
manifests and native payload must match the output directory.

.PARAMETER ExpectedPackageSha256
Required SHA-256 for the exact Core package.

.PARAMETER AllowUnsignedPackage
Explicit opt-in for unsigned packages. Signed packages are verified with
`dotnet nuget verify --all`.

.PARAMETER OutputRoot
Built sample output directory containing the Core package's llama.cpp payload.

.PARAMETER Architecture
Target architecture: x64 or ARM64. Defaults to this PC's architecture.

.EXAMPLE
$expectedSha256 = '<64-character digest you verified>'
$platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }
.\Tools\llama\Build-LlamaBackend.ps1 `
  -Backend cuda `
  -CMakeOption '-DCUDAToolkit_ROOT=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4' `
  -CorePackage .\Samples\Runtime\localpackages\Microsoft.Windows.AI.MachineLearning.LibLlama.Core.<version>.nupkg `
  -ExpectedPackageSha256 $expectedSha256 `
  -OutputRoot ".\Samples\Runtime\out\$platform\Release\llm-chat"

.EXAMPLE
.\Tools\llama\Build-LlamaBackend.ps1 `
  -Backend vulkan `
  -CorePackage .\Samples\Runtime\localpackages\Microsoft.Windows.AI.MachineLearning.LibLlama.Core.<version>.nupkg `
  -ExpectedPackageSha256 $expectedSha256 `
  -OutputRoot ".\Samples\Runtime\out\$platform\Release\llm-chat"

.EXAMPLE
Get-Help .\Tools\llama\Build-LlamaBackend.ps1 -Full
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[a-z0-9][a-z0-9_-]{0,31}$")]
    [string] $Backend,

    [Parameter(Mandatory = $true)]
    [string] $CorePackage,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-fA-F]{64}$")]
    [string] $ExpectedPackageSha256,

    [Parameter(Mandatory = $true)]
    [string] $OutputRoot,

    [ValidateSet("x64", "ARM64")]
    [string] $Architecture = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),

    [string] $WorkRoot = (Join-Path $env:TEMP "winml-llama-backend-interop"),

    [string[]] $CMakeOption = @(),

    [switch] $AllowUnsignedPackage
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$corePackageId = "Microsoft.Windows.AI.MachineLearning.LibLlama.Core"
$winmlCoreNames = @("winmlggml-base.dll", "winmlggml.dll", "winmlllama.dll", "winmlmtmd.dll")
$upstreamCoreNames = @("ggml-base.dll", "ggml.dll", "llama.dll", "mtmd.dll")

# ValidatePattern is case-insensitive. Family, directory, and DLL names are
# lowercase, and NTFS would alias LlamaBackends\CPU onto the Core's cpu folder.
$Backend = $Backend.ToLowerInvariant()
if ($Backend -ceq "cpu")
{
    throw "The CPU backend ships in the Core package. Choose another backend family."
}

$backendTarget = "ggml-$Backend"
$familyOption = "GGML_$($Backend.ToUpperInvariant().Replace('-', '_'))"

# The runtime refuses a backend directory that contains a core module name.
if (($winmlCoreNames + $upstreamCoreNames + @("winmlruntimellama.dll")) -contains "$backendTarget.dll")
{
    throw "Backend family '$Backend' would produce the reserved core module name '$backendTarget.dll'."
}

foreach ($option in $CMakeOption)
{
    if ($option -notmatch '^-D[A-Za-z_][A-Za-z0-9_]*(:[A-Z]+)?=')
    {
        throw "CMakeOption '$option' must have the form -DNAME=VALUE."
    }

    # Other options, such as toolchain, compiler, CRT, flag, or core ggml
    # settings, could change the ABI or the hybrid-crt contract.
    $name = ([regex]::Match($option, '^-D([^:=]+)')).Groups[1].Value
    $isFamilyOption = $name -cmatch "^$($familyOption)_[A-Z0-9_]+$"
    $isPackageRoot = $name -cmatch '^[A-Za-z][A-Za-z0-9]*_ROOT$' -and $name -notmatch '^CMAKE_'
    if (-not $isFamilyOption -and -not $isPackageRoot)
    {
        throw "CMakeOption '$option' is not allowed. Use $($familyOption)_* options or <Package>_ROOT search hints."
    }
}

function Get-RequiredCommand([string] $Name)
{
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command)
    {
        throw "$Name was not found."
    }

    return $command.Source
}

function Get-VisualStudioConfiguration(
    [string] $CMake,
    [string] $Architecture)
{
    $vswhere = Join-Path `
        ${env:ProgramFiles(x86)} `
        "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf))
    {
        throw "Visual Studio Installer's vswhere.exe was not found."
    }

    $capabilities = @(& $CMake -E capabilities 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0)
    {
        throw "Could not query CMake generator capabilities."
    }
    $supportedGenerators = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($generator in ($capabilities | ConvertFrom-Json).generators)
    {
        $supportedGenerators.Add([string] $generator.name) | Out-Null
    }

    $installations = @(
        @(& $vswhere -products * -format json) -join [Environment]::NewLine |
            ConvertFrom-Json
    )
    if ($LASTEXITCODE -ne 0 -or $installations.Count -eq 0)
    {
        throw "A Visual Studio installation was not found."
    }

    foreach ($installation in @(
        $installations |
            Where-Object {
                $_.isComplete -and
                $_.isLaunchable -and
                (Test-Path -LiteralPath (
                    Join-Path $_.installationPath "VC\Tools\MSVC") -PathType Container)
            } |
            Sort-Object { [version] $_.installationVersion } -Descending))
    {
        $majorVersion = [int]([string] $installation.installationVersion -split '\.')[0]
        $generator = switch ($majorVersion)
        {
            18 { "Visual Studio 18 2026" }
            17 { "Visual Studio 17 2022" }
            default { $null }
        }
        if (-not $generator -or -not $supportedGenerators.Contains($generator))
        {
            continue
        }

        $targetArchitecture = if ($Architecture -eq "x64") { "x64" } else { "arm64" }
        $toolset = @(
            Get-ChildItem -LiteralPath (
                Join-Path $installation.installationPath "VC\Tools\MSVC") -Directory |
                Where-Object {
                    $version = [version] $_.Name
                    $version.Major -eq 14 -and
                        $version.Minor -ge 30 -and
                        $version.Minor -lt 50 -and
                        @(
                            Get-ChildItem -Path (
                                Join-Path $_.FullName "bin\Host*\$targetArchitecture\cl.exe"
                            ) -File -ErrorAction SilentlyContinue
                        ).Count -gt 0
                } |
                Sort-Object { [version] $_.Name } -Descending
        ) | Select-Object -First 1
        if ($toolset)
        {
            return [pscustomobject]@{
                Generator = $generator
                InstallationPath = [string] $installation.installationPath
                ToolsetVersion = [string] $toolset.Name
            }
        }
    }

    throw "No installed Visual Studio 2022 or 2026 instance has a CMake-supported generator and the v143 $Architecture tools."
}

function Replace-RequiredText(
    [string] $Path,
    [string] $OldValue,
    [string] $NewValue)
{
    $text = [System.IO.File]::ReadAllText($Path)
    if (-not $text.Contains($OldValue))
    {
        throw "The pinned source no longer contains the expected patch marker in $Path."
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $text.Replace($OldValue, $NewValue),
        [System.Text.UTF8Encoding]::new($false))
}

function Get-PeDependencies([string] $Path, [string] $Dumpbin)
{
    $output = @(& $Dumpbin /DEPENDENTS $Path 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "dumpbin /DEPENDENTS failed for '$Path' (exit $LASTEXITCODE)."
    }
    return @(
        $output |
        ForEach-Object {
            if ($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$')
            {
                $Matches[1]
            }
        } |
        Sort-Object -Unique
    )
}

function Test-ExternalDependency([string] $Name)
{
    if ($Name -match '^(api-ms-win-|ext-ms-win-).+\.dll$' -or
        $Name -match '^(vulkan-1|ucrtbase)\.dll$')
    {
        return $true
    }

    return Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) $Name)
}

function Assert-PeArchitecture(
    [string] $Path,
    [string] $Architecture,
    [string] $Dumpbin)
{
    $output = @(& $Dumpbin /HEADERS $Path 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "dumpbin /HEADERS failed for '$Path' (exit $LASTEXITCODE)."
    }
    $headers = $output -join "`n"
    $pattern = if ($Architecture -eq "x64")
    {
        '8664 machine \(x64\)'
    }
    else
    {
        'AA64 machine \(ARM64\)'
    }
    if ($headers -notmatch $pattern)
    {
        throw "'$Path' does not match target architecture $Architecture."
    }
}

function Get-Sha256([string] $Path)
{
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Assert-SafeRelativePath([string] $Path, [string] $Label)
{
    if ([string]::IsNullOrWhiteSpace($Path) -or
        [System.IO.Path]::IsPathRooted($Path) -or
        $Path -match '(^|[\\/])\.\.?([\\/]|$)')
    {
        throw "$Label contains an unsafe path '$Path'."
    }
}

function Get-ManifestFileMap($Manifest, [string] $Label)
{
    if ($Manifest.files -is [string] -or $Manifest.files -isnot [array])
    {
        throw "$Label files must be a JSON array."
    }

    $map = @{}
    foreach ($file in $Manifest.files)
    {
        $path = [string] $file.path
        $hash = [string] $file.sha256
        Assert-SafeRelativePath $path $Label
        if ($hash -notmatch '^[0-9a-fA-F]{64}$')
        {
            throw "$Label has an invalid SHA-256 for '$path'."
        }
        $key = $path.Replace('/', '\').ToLowerInvariant()
        if ($map.ContainsKey($key))
        {
            throw "$Label contains duplicate file '$path'."
        }
        $map[$key] = $hash.ToLowerInvariant()
    }
    return $map
}

function Assert-ExactFile(
    [string] $ExpectedPath,
    [string] $ActualPath,
    [string] $Label)
{
    if (-not (Test-Path -LiteralPath $ExpectedPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $ActualPath -PathType Leaf))
    {
        throw "$Label is missing."
    }
    if ((Get-Sha256 $ExpectedPath) -ne (Get-Sha256 $ActualPath))
    {
        throw "$Label does not match the supplied Core package."
    }
}

$CorePackage = (Resolve-Path -LiteralPath $CorePackage).Path
if (-not $CorePackage.EndsWith(".nupkg", [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "CorePackage must be a $corePackageId .nupkg."
}

$packageHash = Get-Sha256 $CorePackage
if ($packageHash -ne $ExpectedPackageSha256.ToLowerInvariant())
{
    throw "CorePackage SHA-256 mismatch (expected $ExpectedPackageSha256, got $packageHash)."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($CorePackage)
try
{
    $duplicates = @(
        $archive.Entries |
            Group-Object FullName |
            Where-Object Count -gt 1
    )
    if ($duplicates.Count -gt 0)
    {
        throw "CorePackage contains duplicate ZIP entry '$($duplicates[0].Name)'."
    }
    foreach ($entry in $archive.Entries)
    {
        Assert-SafeRelativePath $entry.FullName "CorePackage"
    }

    $nuspecEntries = @(
        $archive.Entries |
            Where-Object {
                $_.Name -and
                $_.FullName -notmatch '/' -and
                $_.FullName.EndsWith(".nuspec", [System.StringComparison]::OrdinalIgnoreCase)
            }
    )
    if ($nuspecEntries.Count -ne 1)
    {
        throw "CorePackage must contain exactly one root nuspec."
    }
    $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
    try
    {
        [xml] $nuspec = $reader.ReadToEnd()
    }
    finally
    {
        $reader.Dispose()
    }

    $packageId = [string] $nuspec.package.metadata.id
    $packageVersion = [string] $nuspec.package.metadata.version
    if ($packageId -cne $corePackageId -or
        [string]::IsNullOrWhiteSpace($packageVersion))
    {
        throw "CorePackage has unexpected identity '$packageId' version '$packageVersion'."
    }
    $expectedPackageName = "$packageId.$packageVersion.nupkg"
    if (-not (Split-Path -Leaf $CorePackage).Equals(
            $expectedPackageName,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "CorePackage filename does not match nuspec identity '$expectedPackageName'."
    }

    $packageIsSigned = @(
        $archive.Entries |
            Where-Object {
                $_.FullName.Equals(
                    ".signature.p7s",
                    [System.StringComparison]::OrdinalIgnoreCase)
            }
    ).Count -eq 1
}
finally
{
    $archive.Dispose()
}

if ($packageIsSigned)
{
    $dotnet = Get-RequiredCommand "dotnet.exe"
    & $dotnet nuget verify --all $CorePackage
    if ($LASTEXITCODE -ne 0)
    {
        throw "CorePackage signature verification failed."
    }
}
elseif (-not $AllowUnsignedPackage)
{
    throw "CorePackage is unsigned. Use a signed package or explicitly pass -AllowUnsignedPackage."
}

$normalizedArchitecture = if ($Architecture -eq "x64") { "x64" } else { "arm64" }
$runtimeIdentifier = "win-$normalizedArchitecture"
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$workBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    $WorkRoot)
$invocationKey = "$($packageHash.Substring(0, 16))-$Backend-$normalizedArchitecture-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$invocationRoot = Join-Path $workBase $invocationKey
$packageRoot = Join-Path $invocationRoot "package"
$sourceRoot = Join-Path $invocationRoot "source"
$buildRoot = Join-Path $invocationRoot "build"
$stagingBackendRoot = Join-Path `
    (Join-Path $OutputRoot "LlamaBackends") `
    ".$Backend-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$lockPath = Join-Path `
    $OutputRoot `
    ".winml-llama-$Backend-$normalizedArchitecture.lock"

New-Item -ItemType Directory -Force -Path $invocationRoot,$packageRoot | Out-Null

$operationError = $null
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$outputLock = $null
try
{
[System.IO.Compression.ZipFile]::ExtractToDirectory($CorePackage, $packageRoot)

$nativeRoot = Join-Path $packageRoot "runtimes\$runtimeIdentifier\native"
$coreManifestPath = Join-Path $nativeRoot "WinMLLlamaCoreManifest.json"
$cpuManifestPath = Join-Path $nativeRoot "LlamaBackends\cpu\WinMLLlamaBackendManifest.json"
if (-not (Test-Path -LiteralPath $coreManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $cpuManifestPath -PathType Leaf))
{
    throw "The package does not contain the llama.cpp core and CPU backend manifests for $runtimeIdentifier."
}

$coreManifest = Get-Content -LiteralPath $coreManifestPath -Raw | ConvertFrom-Json
$contract = Get-Content -LiteralPath $cpuManifestPath -Raw | ConvertFrom-Json
if (-not $contract.llamaCommit -or -not $contract.llamaBuild)
{
    throw "The package CPU backend manifest is missing the llama.cpp build."
}

foreach ($field in @(
    "architecture",
    "llamaCommit",
    "llamaBuild",
    "backendApiVersion",
    "runtimeContract"))
{
    if ([string] $coreManifest.$field -cne [string] $contract.$field)
    {
        throw "Package core/CPU contract mismatch for '$field'."
    }
}
if ([string] $contract.architecture -cne $normalizedArchitecture -or
    [string] $contract.family -cne "cpu")
{
    throw "Package backend architecture/family does not match '$runtimeIdentifier'."
}
if ([int] $coreManifest.schemaVersion -ne 1 -or
    [int] $contract.schemaVersion -ne 1 -or
    [string] $coreManifest.flavor -cne "winml" -or
    [string] $contract.runtimeContract -cne "hybrid-crt")
{
    throw "Package declares an unsupported llama.cpp core flavor or runtime contract."
}

$outputCoreManifestPath = Join-Path $OutputRoot "WinMLLlamaCoreManifest.json"
$outputCpuManifestPath = Join-Path `
    $OutputRoot `
    "LlamaBackends\cpu\WinMLLlamaBackendManifest.json"
Assert-ExactFile `
    $coreManifestPath `
    $outputCoreManifestPath `
    "OutputRoot core manifest"
Assert-ExactFile `
    $cpuManifestPath `
    $outputCpuManifestPath `
    "OutputRoot CPU backend manifest"

$dumpbin = Get-RequiredCommand "dumpbin.exe"
$coreFiles = Get-ManifestFileMap $coreManifest "package core manifest"
foreach ($relativePath in $coreFiles.Keys)
{
    $outputPath = Join-Path $OutputRoot $relativePath
    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf) -or
        (Get-Sha256 $outputPath) -ne $coreFiles[$relativePath])
    {
        throw "OutputRoot payload does not match CorePackage: $relativePath"
    }
    if ($relativePath -match '(?i)\.dll$')
    {
        Assert-PeArchitecture $outputPath $Architecture $dumpbin
    }
}

$git = Get-RequiredCommand "git.exe"
$cmake = Get-RequiredCommand "cmake.exe"
$visualStudio = Get-VisualStudioConfiguration $cmake $Architecture

& $git clone -c core.longpaths=true --filter=blob:none --no-checkout https://github.com/ggml-org/llama.cpp $sourceRoot
if ($LASTEXITCODE -ne 0)
{
    throw "Failed to clone llama.cpp."
}

& $git -C $sourceRoot fetch --quiet origin $contract.llamaCommit
if ($LASTEXITCODE -ne 0)
{
    throw "Failed to fetch llama.cpp commit $($contract.llamaCommit)."
}

& $git -C $sourceRoot checkout --detach $contract.llamaCommit
if ($LASTEXITCODE -ne 0)
{
    throw "Failed to check out the pinned llama.cpp source."
}

    $ggmlCMake = Join-Path $sourceRoot "ggml\src\CMakeLists.txt"
    $llamaCMake = Join-Path $sourceRoot "src\CMakeLists.txt"
    Replace-RequiredText $ggmlCMake @'
add_library(ggml-base
'@ @'
add_library(ggml-base
'@
    [System.IO.File]::AppendAllText(
        $ggmlCMake,
        @'

if (WINML_LLAMA_NAMESPACED_CORE)
    set_target_properties(ggml-base PROPERTIES OUTPUT_NAME "WinMLggml-base")
    set_target_properties(ggml PROPERTIES OUTPUT_NAME "WinMLggml")
endif()
'@)
    [System.IO.File]::AppendAllText(
        $llamaCMake,
        @'

if (WINML_LLAMA_NAMESPACED_CORE)
    set_target_properties(llama PROPERTIES OUTPUT_NAME "WinMLllama")
endif()
'@)

    # Build only the selected family.
    $backendOptions = @("-D$familyOption=ON")
    foreach ($knownFamily in @("GGML_CUDA", "GGML_VULKAN", "GGML_HIP", "GGML_SYCL", "GGML_METAL"))
    {
        if ($knownFamily -cne $familyOption)
        {
            $backendOptions += "-D$knownFamily=OFF"
        }
    }

    $generatorArchitecture = if ($Architecture -eq "x64") { "x64" } else { "ARM64" }
    $generatorToolset = "v143,version=$($visualStudio.ToolsetVersion)"
    if ($familyOption -ceq "GGML_CUDA")
    {
        # The Visual Studio generator needs the CUDA toolkit location when the
        # toolkit's Visual Studio integration isn't installed.
        $cudaRoot = $null
        foreach ($option in $CMakeOption)
        {
            if ($option -match "^-DCUDAToolkit_ROOT=(.+)$")
            {
                $cudaRoot = $Matches[1]
            }
        }
        if (-not $cudaRoot)
        {
            $cudaRoot = $env:CUDA_PATH
        }
        if (-not $cudaRoot -or -not (Test-Path -LiteralPath (Join-Path $cudaRoot "bin\nvcc.exe")))
        {
            throw "The cuda backend needs an NVIDIA CUDA Toolkit that you have installed. Set CUDA_PATH or pass -CMakeOption '-DCUDAToolkit_ROOT=<toolkit directory>'."
        }
        $generatorToolset += ",cuda=$cudaRoot"
    }
    $configureArguments = @(
        "-S", $sourceRoot,
        "-B", $buildRoot,
        "-G", $visualStudio.Generator,
        "-A", $generatorArchitecture,
        "-T", $generatorToolset,
        "-DCMAKE_GENERATOR_INSTANCE=$($visualStudio.InstallationPath)",
        "-DBUILD_SHARED_LIBS=ON",
        "-DGGML_BACKEND_DL=ON",
        "-DGGML_CPU=OFF",
        "-DGGML_NATIVE=OFF",
        "-DGGML_OPENMP=OFF",
        "-DGGML_BUILD_TESTS=OFF",
        "-DGGML_BUILD_EXAMPLES=OFF",
        "-DLLAMA_BUILD_NUMBER=$($contract.llamaBuild)",
        "-DLLAMA_BUILD_TESTS=OFF",
        "-DLLAMA_BUILD_TOOLS=OFF",
        "-DLLAMA_BUILD_EXAMPLES=OFF",
        "-DLLAMA_BUILD_SERVER=OFF",
        "-DLLAMA_BUILD_APP=OFF",
        "-DLLAMA_BUILD_COMMON=OFF",
        "-DLLAMA_BUILD_UI=OFF",
        "-DLLAMA_CURL=OFF",
        "-DLLAMA_OPENSSL=OFF",
        "-DWINML_LLAMA_NAMESPACED_CORE=ON",
        "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded",
        "-DCMAKE_C_FLAGS=/guard:cf /guard:ehcont",
        "-DCMAKE_CXX_FLAGS=/guard:cf /guard:ehcont",
        "-DCMAKE_SHARED_LINKER_FLAGS=/NODEFAULTLIB:libucrt.lib /DEFAULTLIB:ucrt.lib",
        "-DCMAKE_MODULE_LINKER_FLAGS=/NODEFAULTLIB:libucrt.lib /DEFAULTLIB:ucrt.lib"
    ) + $backendOptions + $CMakeOption

    & $cmake @configureArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "CMake configuration failed."
    }

    & $cmake --build $buildRoot --config Release --target $backendTarget --parallel
    if ($LASTEXITCODE -ne 0)
    {
        throw "CMake build failed for $backendTarget."
    }

    $pluginCandidates = @(
        Get-ChildItem -LiteralPath $buildRoot -Filter "$backendTarget.dll" -File -Recurse
    )
    if ($pluginCandidates.Count -ne 1)
    {
        throw "Expected one $backendTarget.dll, found $($pluginCandidates.Count)."
    }

    $plugin = $pluginCandidates[0].FullName
    Assert-PeArchitecture $plugin $Architecture $dumpbin
    $imports = @(Get-PeDependencies $plugin $dumpbin | ForEach-Object { $_.ToLowerInvariant() })
    if ($imports -notcontains "winmlggml-base.dll")
    {
        throw "$backendTarget.dll does not import the WinML-namespaced ggml core."
    }
    foreach ($import in $imports)
    {
        if ($upstreamCoreNames -contains $import)
        {
            throw "$backendTarget.dll imports the upstream core module '$import'."
        }
        if ($import -like "vcruntime140*" -or $import -like "msvcp140*")
        {
            throw "$backendTarget.dll imports '$import'; the hybrid-crt contract requires the static VC++ runtime."
        }
        # Vendor runtimes such as vulkan-1.dll or OpenCL.dll normally come from
        # System32. The runtime also resolves imports from the backend directory.
        if ($winmlCoreNames -notcontains $import -and -not (Test-ExternalDependency $import))
        {
            Write-Warning "$backendTarget.dll imports '$import', which isn't a system module. Copy it into LlamaBackends\$Backend before running."
        }
    }

    $backendRoot = Join-Path $OutputRoot "LlamaBackends\$Backend"
    if (Test-Path -LiteralPath $stagingBackendRoot)
    {
        Remove-Item -LiteralPath $stagingBackendRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $stagingBackendRoot | Out-Null
    $stagedPlugin = Join-Path $stagingBackendRoot "$backendTarget.dll"
    Copy-Item -LiteralPath $plugin -Destination $stagedPlugin

    # Same schema as the WinMLLlamaBackendManifest.json files in the LibLlama packages.
    $manifest = [ordered]@{
        schemaVersion = 1
        family = $Backend
        architecture = $normalizedArchitecture
        llamaBuild = [uint32]$contract.llamaBuild
        llamaCommit = ([string]$contract.llamaCommit).ToLowerInvariant()
        backendApiVersion = [uint32]$contract.backendApiVersion
        runtimeContract = [string]$contract.runtimeContract
        backend = "$backendTarget.dll"
        sha256 = Get-Sha256 $stagedPlugin
    }

    $manifestPath = Join-Path $stagingBackendRoot "WinMLLlamaBackendManifest.json"
    [System.IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    try
    {
        $outputLock = [System.IO.File]::Open(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch
    {
        throw "Another backend staging operation is active for '$backendRoot'."
    }

    if (Test-Path -LiteralPath $backendRoot)
    {
        Remove-Item -LiteralPath $backendRoot -Recurse -Force
    }
    Move-Item -LiteralPath $stagingBackendRoot -Destination $backendRoot
    Write-Host "Staged $backendTarget.dll and matched manifest under $backendRoot"
}
catch
{
    $operationError = $_
}
finally
{
    if ($outputLock)
    {
        $outputLock.Dispose()
        try
        {
            Remove-Item -LiteralPath $lockPath -Force -ErrorAction Stop
        }
        catch
        {
            $cleanupFailures.Add("Could not remove output lock '$lockPath': $($_.Exception.Message)")
        }
    }
    foreach ($path in @($stagingBackendRoot, $invocationRoot))
    {
        if (Test-Path -LiteralPath $path)
        {
            try
            {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            }
            catch
            {
                $cleanupFailures.Add("Could not remove '$path': $($_.Exception.Message)")
            }
        }
    }
}

if ($operationError)
{
    if ($cleanupFailures.Count -gt 0)
    {
        throw "$($operationError | Out-String)`nCleanup failures:`n$($cleanupFailures -join [Environment]::NewLine)"
    }
    throw $operationError
}
if ($cleanupFailures.Count -gt 0)
{
    throw ($cleanupFailures -join [Environment]::NewLine)
}
