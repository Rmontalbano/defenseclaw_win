## Highlights

{{HIGHLIGHTS}}

## Downloads

| File | What it is |
| --- | --- |
| `DefenseClawWin-{{VERSION}}-Setup-x64.exe` | Installer. Per user, no administrator rights; installs to `%LOCALAPPDATA%\Programs\DefenseClawWin`, adds a Start menu shortcut and an optional start-at-sign-in entry. Recommended. |
| `DefenseClawWin-{{VERSION}}-win-x64.zip` | Portable build: `DefenseClaw.App.exe` and `DefenseClaw.GatewayAdmin.exe`, nothing else. Also what the in-app updater downloads. |
| `install.ps1` | One-line installer script: downloads the Setup for the latest (or a chosen) release, verifies it, then runs it for the current user. |
| `checksums.txt` | SHA-256 of every file above. |

Windows 10 1809 or later, x64. Both exes are self-contained: no separate .NET install is needed.

## Verification

These files are **not Authenticode-signed**, so Windows SmartScreen may warn about an unknown publisher. Trust comes from the checks below, which are the only proof a file is the one this repository's workflow built.

- SHA-256: `Get-FileHash .\<file> -Algorithm SHA256` must equal the file's line in `checksums.txt` (GitHub also shows each asset's digest).
- Provenance: every asset, `checksums.txt` included, has a GitHub build-provenance attestation. With the GitHub CLI: `gh attestation verify <file> --repo {{REPO}}`.
- Install in one step, with the checks done for you: `irm https://github.com/{{REPO}}/releases/download/v{{VERSION}}/install.ps1 | iex`

## SHA-256

```
{{SHA256}}
```
