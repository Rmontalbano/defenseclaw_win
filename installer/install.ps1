<#
.SYNOPSIS
    Installs DefenseClaw for Windows (community) for the current user. Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
    Resolves a release of Rmontalbano/defenseclaw_win (the latest one, or -Version), downloads its Setup.exe and checksums.txt to a
    new temp folder, checks the Setup's SHA-256 against checksums.txt (and against the digest GitHub computed for the asset, when the
    API gives one), checks the GitHub build-provenance attestation when the GitHub CLI (gh) is installed and signed in (and says so
    when it is not), and then runs Setup for the current user only. It never elevates and never runs anything it did not verify
    first. The temp folder is removed when the script ends, whatever the outcome.

    The release is NOT Authenticode-signed. The SHA-256 and the attestation are how this script (and you) tell it from a copy.

    Run as a file (powershell -File .\install.ps1), a failure sets the process exit code to 1. Piped to iex it only sets
    $LASTEXITCODE, so the window you typed it in stays open.

.PARAMETER Version
    Install this release (for example 1.0.0, or v1.0.0) instead of the latest one. Only X.Y.Z versions are accepted.

.PARAMETER Yes
    Unattended: run Setup silently (/SILENT) with no questions. DefenseClaw for Windows must not be running (exit it from its tray
    icon first): an unattended Setup cannot ask to close it, so it stops with an error instead. Without it Setup opens its normal
    wizard.

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

# Everything below runs in a child scope. Piped to iex, a script runs in the caller's own session, and its strict mode,
# $ErrorActionPreference, $ProgressPreference, helper functions and variables would stay behind in the person's window.
& {
    Set-StrictMode -Version 2.0
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1 is very slow to download with its progress bar on

    $Owner = 'Rmontalbano'
    $Repo = 'defenseclaw_win'
    $ApiBase = "https://api.github.com/repos/$Owner/$Repo"
    $DownloadPrefix = "https://github.com/$Owner/$Repo/releases/download/"

    function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

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

        # A new, empty folder of our own: nothing a previous run (or anybody else) left there can be picked up. It is removed in the
        # finally below on every path out (a failed check, a cancelled Setup, Ctrl+C), so a download that did not pass is never left behind.
        $work = Join-Path ([IO.Path]::GetTempPath()) ('DefenseClawWin-install-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $work | Out-Null
        try {
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
                $ghExit = $LASTEXITCODE
                if ($ghExit -eq 4) {
                    # gh's "authentication required" exit code: it could not check, which says nothing against the file (a failed
                    # verification is exit 1). Same footing as gh not being installed.
                    Write-Host '    attestation: SKIPPED - gh is installed but not signed in. Run "gh auth login" (or set GH_TOKEN) and run this script' -ForegroundColor Yellow
                    Write-Host '    again for the stronger check; the SHA-256 checks above are the only proof of origin for this run.' -ForegroundColor Yellow
                }
                elseif ($ghExit -ne 0) {
                    throw "gh attestation verify failed (exit $ghExit). Nothing was run. (If gh reports a sign-in problem, run `"gh auth login`" and try again.)"
                }
                else {
                    Write-Host '    attestation: OK'
                }
            }
            else {
                Write-Host '    attestation: SKIPPED - the GitHub CLI (gh) is not installed. Only the SHA-256 checks above protected this run.' -ForegroundColor Yellow
                Write-Host '    For the stronger check, install gh (https://cli.github.com), run "gh auth login", and run this script again.' -ForegroundColor Yellow
            }

            Write-Host ''
            Write-Host 'Note: this release is not Authenticode-signed; Windows SmartScreen may show an "unknown publisher" warning.'

            # Per-user and never elevating is the Setup script's own doing (PrivilegesRequired=lowest), not a switch from here.
            # Without -Yes Setup shows its wizard (which is the confirmation).
            $setupArgs = @()
            if ($Yes) { $setupArgs += @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART') }
            if ($NoAutostart) { $setupArgs += '/TASKS="!autostart"' }
            $shown = if ($setupArgs.Count -gt 0) { $setupArgs -join ' ' } else { 'wizard' }

            Write-Step "Running Setup ($shown)"
            # -ArgumentList refuses an empty array, so it is only passed when there is something to pass.
            $startArgs = @{ FilePath = $setupPath; PassThru = $true }
            if ($setupArgs.Count -gt 0) { $startArgs['ArgumentList'] = $setupArgs }
            # Not -Wait: that waits for the whole process tree, and the tray app the wizard's "Launch" option starts is part of it,
            # so this script would not return until the person exited the app. Wait for Setup alone.
            $process = Start-Process @startArgs
            $null = $process.Handle   # keeps the process handle open so ExitCode can still be read after Setup is gone
            $process.WaitForExit()
            $exit = $process.ExitCode
            if ($exit -ne 0) {
                if ($exit -eq 2 -or $exit -eq 5) { throw "Setup was cancelled (exit code $exit)." }
                if ($exit -eq 7 -or $exit -eq 8) { throw "Setup could not continue (exit code $exit). If DefenseClaw for Windows is running, exit it from its tray icon and run this again." }
                throw "Setup exited with code $exit."
            }

            Write-Step "DefenseClaw for Windows $ver is installed for this user."
        }
        finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $failed = $false
    $priorProtocol = [Net.ServicePointManager]::SecurityProtocol
    try {
        # GitHub requires TLS 1.2; Windows PowerShell 5.1 may default to older protocols. Pin it (and nothing weaker) while this runs.
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        }
        catch {
            throw "Could not require TLS 1.2: $($_.Exception.Message)"
        }
        Install-DefenseClawWin
        $global:LASTEXITCODE = 0   # gh may have left its own (for example 4) behind
    }
    catch {
        Write-Host "install.ps1 failed: $($_.Exception.Message)" -ForegroundColor Red
        $global:LASTEXITCODE = 1
        $failed = $true
    }
    finally {
        # The protocol setting is process-wide, not scoped: put back what the session had.
        [Net.ServicePointManager]::SecurityProtocol = $priorProtocol
    }

    # Run as a file, the process exit code must say it failed. Piped to iex there is no file ($PSCommandPath is empty) and an exit
    # would close the person's window, so only $LASTEXITCODE is set.
    if ($failed -and $PSCommandPath) { exit 1 }
}
