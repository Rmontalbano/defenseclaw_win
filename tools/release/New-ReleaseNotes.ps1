<#
.SYNOPSIS
    Builds the GitHub release body from .github/release-notes-template.md.

.DESCRIPTION
    Highlights come from the "## [X.Y.Z]" section of CHANGELOG.md; when that section is missing or empty, from the tag's annotated
    message (-TagMessage). When both are empty the script FAILS, so a release can never go out with no description.
    Run with -CheckOnly early in the workflow to fail before anything is built.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Repo = 'Rmontalbano/defenseclaw_win',
    [string]$Changelog = 'CHANGELOG.md',
    [string]$Template = '.github/release-notes-template.md',
    [string]$TagMessage = '',
    [string]$ChecksumsFile,
    [string]$OutFile,
    [switch]$CheckOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z (got '$Version')." }

function Get-ChangelogSection([string]$Path, [string]$Ver) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $lines = Get-Content -LiteralPath $Path
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ('^##\s+\[' + [regex]::Escape($Ver) + '\](\s|$)')) { $start = $i + 1; break }
    }
    if ($start -lt 0) { return '' }
    $body = New-Object System.Collections.Generic.List[string]
    for ($i = $start; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s+\[') { break }
        if ($lines[$i] -match '^\[[^\]]+\]:\s') { break }   # link reference definitions at the end of the file
        $body.Add($lines[$i])
    }
    return (($body -join "`n").Trim())
}

$highlights = Get-ChangelogSection $Changelog $Version
$source = "$Changelog section [$Version]"
if (-not $highlights) {
    $highlights = ($TagMessage).Trim()
    $source = 'the tag message'
}
if (-not $highlights) {
    throw "No release description for v$Version. Fill in '## [$Version]' in $Changelog (and move it out of [Unreleased]) or push an annotated tag with a message (git tag -a v$Version -m '...')."
}
Write-Host "Highlights taken from $source."
if ($CheckOnly) { return }

if (-not $ChecksumsFile -or -not (Test-Path -LiteralPath $ChecksumsFile)) { throw 'Pass -ChecksumsFile (the checksums.txt of the release).' }
if (-not $OutFile) { throw 'Pass -OutFile.' }

$sums = (Get-Content -LiteralPath $ChecksumsFile -Raw).TrimEnd()
$text = Get-Content -LiteralPath $Template -Raw
$text = $text.Replace('{{HIGHLIGHTS}}', $highlights).Replace('{{VERSION}}', $Version).Replace('{{REPO}}', $Repo).Replace('{{SHA256}}', $sums)
if ($text -match '\{\{[A-Z0-9_]+\}\}') { throw "Unreplaced placeholder in the template: $($Matches[0])" }
[IO.File]::WriteAllText($OutFile, $text, (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote $OutFile"
