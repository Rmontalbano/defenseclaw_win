<#
.SYNOPSIS
    Asserts that a release folder matches the release contract. Fails (throws) on the first violation.

.DESCRIPTION
    - the zip has exactly DefenseClaw.App.exe and DefenseClaw.GatewayAdmin.exe at its root, no other entry, no paths;
    - each of those exes (extracted to a temp folder, never run) has ProductVersion == Version (the +sha build metadata is ignored);
    - Setup.exe and the zip, and install.ps1, exist with the contract names;
    - checksums.txt lists every file in the folder except itself, and every hash matches.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Dir
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ($Version -notmatch '^\d+\.\d+\.\d+(-dev)?$') { throw "Version must be X.Y.Z or 0.0.0-dev (got '$Version')." }
$Dir = (Resolve-Path -LiteralPath $Dir).Path

$setup = "DefenseClawWin-$Version-Setup-x64.exe"
$zip = "DefenseClawWin-$Version-win-x64.zip"
$expectedAssets = @($setup, $zip, 'install.ps1')
foreach ($name in $expectedAssets + 'checksums.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $Dir $name))) { throw "Missing release asset: $name" }
}

# 1. zip layout
$expectedEntries = @('DefenseClaw.App.exe', 'DefenseClaw.GatewayAdmin.exe')
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Dir $zip))
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
}
finally { $archive.Dispose() }
if (($names -join '|') -ne (($expectedEntries | Sort-Object) -join '|')) {
    throw "The zip must contain exactly $($expectedEntries -join ' and ') at its root; it has: $($names -join ', ')"
}
Write-Host "zip layout: OK ($($names -join ', '))"

# 2. versions of the exes inside the zip (extracted to a temp folder; nothing is executed)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcw-verify-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $Dir $zip), $tmp)
    foreach ($exe in $expectedEntries) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $tmp $exe))
        $product = ([string]$info.ProductVersion).Split('+')[0]
        if ($product -ne $Version) { throw "$exe has ProductVersion '$($info.ProductVersion)', expected '$Version'." }
        Write-Host "$exe ProductVersion: OK ($($info.ProductVersion))"
    }
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# 3. checksums.txt
$listed = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $Dir 'checksums.txt')) {
    if ($line.Trim() -eq '') { continue }
    if ($line -notmatch '^([0-9a-f]{64})  (\S+)$') { throw "Malformed checksums.txt line: '$line'" }
    $listed[$Matches[2]] = $Matches[1]
}
$present = @(Get-ChildItem -LiteralPath $Dir -File | Where-Object { $_.Name -ne 'checksums.txt' } | ForEach-Object { $_.Name })
foreach ($name in $present) {
    if (-not $listed.ContainsKey($name)) { throw "checksums.txt does not list $name" }
    $actual = (Get-FileHash -LiteralPath (Join-Path $Dir $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $listed[$name]) { throw "checksums.txt hash for $name does not match the file." }
}
foreach ($name in $listed.Keys) {
    if ($present -notcontains $name) { throw "checksums.txt lists $name, which is not in the release folder." }
}
Write-Host "checksums.txt: OK ($($present.Count) files)"
