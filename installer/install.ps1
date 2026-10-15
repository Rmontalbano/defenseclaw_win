<#
.SYNOPSIS
    Installs DefenseClaw for Windows (community) for the current user. Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
    Resolves a release of Rmontalbano/defenseclaw_win (the latest one, or -Version), downloads its Setup.exe and checksums.txt to a
    new temp folder, checks the Setup's SHA-256 against checksums.txt (and against the digest GitHub computed for the asset, when the
    API gives one), checks the GitHub build-provenance attestation when the GitHub CLI (gh) is installed (and says so when it is
    not), and then runs Setup for the current user only. It never elevates and never runs anything it did not verify first.

    The release is NOT Authenticode-signed. The SHA-256 and the attestation are how this script (and you) tell it from a copy.

.PARAMETER Version
    Install this release (for example 1.0.0, or v1.0.0) instead of the latest one. Only X.Y.Z versions are accepted.

.PARAMETER Yes
    Unattended: run Setup silently (/SILENT) with no questions. Without it Setup opens its normal wizard.

.PARAMETER NoAutostart
    Do not start DefenseClaw for Windows at sign-in (passes /TASKS="!autostart" to Setup). The default is on.

.EXAMPLE
    irm https://github.com/Rmontalbano/defenseclaw_win/releases/latest/download/install.ps1 | iex

.EXAMPLE
    # With parameters (a piped script cannot take any), and review the script first if you like:
    $s = irm https://github.com/Rmontalbano/defenseclaw_win/releases/latest/download/install.ps1
    & ([scriptblock]::Create($s)) -Version 1.0.0 -Yes
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Yes,
    [switch]$NoAutostart
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1 is very slow to download with its progress bar on

$Owner = 'Rmontalbano'
$Repo = 'defenseclaw_win'
$ApiBase = "https://api.github.com/repos/$Owner/$Repo"
$DownloadPrefix = "https://github.com/$Owner/$Repo/releases/download/"

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# GitHub requires TLS 1.2; Windows PowerShell 5.1 may default to older protocols. Pin it (and nothing weaker).
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}
catch {
    throw "Could not require TLS 1.2: $($_.Exception.Message)"
}

function Get-Json([string]$Uri) {
    if (-not $Uri.StartsWith('https://', [StringComparison]::Ordinal)) { throw "Refusing a non-HTTPS address: $Uri" }
    Invoke-RestMethod -Uri $Uri -UseBasicParsing -Headers @{
        'Accept'               = 'application/vnd.github+json'
        'User-Agent'           = 'DefenseClawWin-install.ps1'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
}

function Save-File([string]$Uri, [string]$Path) {
    # Only release assets of this repository. The address comes from the GitHub API response, so it is checked, not trusted.
    if (-not $Uri.StartsWith($DownloadPrefix, [StringComparison]::Ordinal)) { throw "Refusing to download from an unexpected address: $Uri" }
    Invoke-WebRequest -Uri $Uri -OutFile $Path -UseBasicParsing -Headers @{ 'User-Agent' = 'DefenseClawWin-install.ps1' }
}

function Install-DefenseClawWin {
    if ($Version) {
        $normalized = $Version.Trim()
        if ($normalized.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { $normalized = $normalized.Substring(1) }
        if ($normalized -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must look like 1.2.3 (got '$Version')." }
        Write-Step "Looking up release v$normalized"
        $release = Get-Json "$ApiBase/releases/tags/v$normalized"
    }
    else {
        Write-Step 'Looking up the latest release'
        $release = Get-Json "$ApiBase/releases/latest"
    }

    if ($release.draft -or $release.prerelease) { throw "Release $($release.tag_name) is a draft or pre-release; refusing it." }
    $tag = [string]$release.tag_name
    if ($tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw "Unexpected release tag '$tag'." }
    $ver = $Matches[1]
    Write-Host "    DefenseClaw for Windows $ver"

    $setupName = "DefenseClawWin-$ver-Setup-x64.exe"
    $setupAsset = @($release.assets | Where-Object { $_.name -eq $setupName }) | Select-Object -First 1
    $sumsAsset = @($release.assets | Where-Object { $_.name -eq 'checksums.txt' }) | Select-Object -First 1
    if (-not $setupAsset) { throw "Release $tag has no asset named $setupName." }
    if (-not $sumsAsset) { throw "Release $tag has no checksums.txt." }

    # A new, empty folder of our own: nothing a previous run (or anybody else) left there can be picked up.
    $work = Join-Path ([IO.Path]::GetTempPath()) ('DefenseClawWin-install-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    $setupPath = Join-Path $work $setupName
    $sumsPath = Join-Path $work 'checksums.txt'

    Write-Step "Downloading to $work"
    Save-File $setupAsset.browser_download_url $setupPath
    Save-File $sumsAsset.browser_download_url $sumsPath

    Write-Step 'Verifying SHA-256'
    $actual = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = $null
    foreach ($line in Get-Content -LiteralPath $sumsPath) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $setupName) { $expected = $Matches[1].ToLowerInvariant() }
    }
    if (-not $expected) { throw "checksums.txt has no line for $setupName." }
    if ($actual -ne $expected) { throw "SHA-256 mismatch for $setupName (expected $expected, got $actual). Nothing was run." }
    Write-Host "    checksums.txt: OK ($actual)"

    $digest = $setupAsset.PSObject.Properties['digest']
    if ($digest -and $digest.Value -and ([string]$digest.Value) -match '^sha256:([0-9a-fA-F]{64})$') {
        if ($Matches[1].ToLowerInvariant() -ne $actual) { throw "SHA-256 does not match the digest GitHub reports for the asset. Nothing was run." }
        Write-Host '    GitHub asset digest: OK'
    }
    else {
        Write-Host '    GitHub asset digest: not provided for this release; checksums.txt alone was used.'
    }

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        Write-Step 'Verifying the build provenance attestation (gh attestation verify)'
        & $gh.Source attestation verify $setupPath --repo "$Owner/$Repo"
        if ($LASTEXITCODE -ne 0) { throw "gh attestation verify failed (exit $LASTEXITCODE). Nothing was run." }
        Write-Host '    attestation: OK'
    }
    else {
        Write-Host '    attestation: SKIPPED - the GitHub CLI (gh) is not installed. Install it and run' -ForegroundColor Yellow
        Write-Host "      gh attestation verify `"$setupPath`" --repo $Owner/$Repo" -ForegroundColor Yellow
        Write-Host '    for the stronger check; the SHA-256 above is the only proof of origin for this run.' -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host 'Note: this release is not Authenticode-signed; Windows SmartScreen may show an "unknown publisher" warning.'

    # /CURRENTUSER: per-user install, never elevates. Without -Yes Setup shows its wizard (which is the confirmation).
    $setupArgs = @('/CURRENTUSER')
    if ($Yes) { $setupArgs += @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART') }
    if ($NoAutostart) { $setupArgs += '/TASKS="!autostart"' }

    Write-Step "Running Setup ($($setupArgs -join ' '))"
    $process = Start-Process -FilePath $setupPath -ArgumentList $setupArgs -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Setup exited with code $($process.ExitCode)." }

    Write-Step "DefenseClaw for Windows $ver is installed for this user."
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

try {
    Install-DefenseClawWin
}
catch {
    Write-Host "install.ps1 failed: $($_.Exception.Message)" -ForegroundColor Red
    $global:LASTEXITCODE = 1
}
