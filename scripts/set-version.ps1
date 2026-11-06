param(
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$normalized = $Version.Trim()
$normalized = $normalized -replace '^[vV]', ''
if ($normalized -notmatch '^\d+\.\d+\.\d+$') {
    throw "Release version must be a semantic X.Y.Z value, optionally prefixed with v. Received: $Version"
}

$assemblyInfo = Join-Path $PSScriptRoot '..\src\PcCare\Properties\AssemblyInfo.cs'
$assemblyInfo = [System.IO.Path]::GetFullPath($assemblyInfo)
$content = [System.IO.File]::ReadAllText($assemblyInfo)
$fourPart = $normalized + '.0'

$content = [regex]::Replace($content, 'AssemblyVersion[(]"[^"]+"[)]', ('AssemblyVersion(' + [char]34 + $fourPart + [char]34 + ')'))
$content = [regex]::Replace($content, 'AssemblyFileVersion[(]"[^"]+"[)]', ('AssemblyFileVersion(' + [char]34 + $fourPart + [char]34 + ')'))
$content = [regex]::Replace($content, 'AssemblyInformationalVersion[(]"[^"]+"[)]', ('AssemblyInformationalVersion(' + [char]34 + $normalized + [char]34 + ')'))

$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($assemblyInfo, $content, $utf8)
Write-Host "PC Care version set to $normalized ($fourPart file version)."
