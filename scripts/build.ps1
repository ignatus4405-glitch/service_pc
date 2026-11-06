[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageConfigPath = Join-Path $repositoryRoot "build\packages.config"
if (-not (Test-Path $packageConfigPath)) {
    throw "NuGet package manifest was not found: $packageConfigPath"
}

[xml] $packageConfig = [System.IO.File]::ReadAllText($packageConfigPath)
$referencePackage = $packageConfig.packages.package |
    Where-Object { $_.id -eq "Microsoft.NETFramework.ReferenceAssemblies.net45" } |
    Select-Object -First 1
if ($null -eq $referencePackage) {
    throw "Microsoft.NETFramework.ReferenceAssemblies.net45 is missing from build/packages.config."
}

$packageDirectory = Join-Path $repositoryRoot ("packages\Microsoft.NETFramework.ReferenceAssemblies.net45." + $referencePackage.version)
$referenceTargets = Join-Path $packageDirectory "build\Microsoft.NETFramework.ReferenceAssemblies.net45.targets"
if (-not (Test-Path $referenceTargets)) {
    throw "Reference assemblies targets were not restored: $referenceTargets"
}

$solution = Join-Path $repositoryRoot "PcCare.sln"
$msbuildArguments = @(
    $solution,
    "/m",
    "/p:Configuration=$Configuration",
    "/p:CustomAfterMicrosoftCommonTargets=$referenceTargets",
    "/verbosity:minimal"
)

$buildOutput = & msbuild @msbuildArguments 2>&1
$buildExitCode = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }

if ($buildExitCode -ne 0) {
    $errorLines = @($buildOutput | Where-Object {
        $_.ToString() -match '(?i)(\berror\b|\bwarning\b|MSB\d{4}|CS\d{4})'
    } | Select-Object -Last 80)
    if ($errorLines.Count -eq 0) {
        $errorLines = @($buildOutput | Select-Object -Last 30)
    }
    foreach ($line in $errorLines) {
        Write-Host "::error::$line"
    }
    exit $buildExitCode
}
