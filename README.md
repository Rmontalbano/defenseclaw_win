# DefenseClaw for Windows

A community Windows companion app for [Cisco DefenseClaw](https://github.com/cisco-ai-defense/defenseclaw). It shows the state of a local DefenseClaw install (gateway, alerts, audit, inventory, runtime monitoring) in a tray icon and a dashboard, and runs DefenseClaw's own CLI for every change. It works with DefenseClaw 0.8.10 and 1.0.x. It is the Windows counterpart to [defenseclaw_mac](https://github.com/keitheobrien/defenseclaw_mac).

This is a community project, not an official Cisco product.

## Design principle

The app does not write DefenseClaw state itself. Every change runs through the `defenseclaw` CLI as a logged invocation, and the exact argv, live output and exit status appear in the Activity panel. Every state-changing command goes through a review first. The app refuses known secrets on argv: a value it recognises as a secret is never put on the command line, and other secrets reach the CLI through its own prompt, stdin, or one child process's environment.

Three routes can still put a value on argv, because the CLI takes it there: `mcp set --env`, `mcp set --url` and `plugin install <url>`. The review warns when a value looks like a secret in those forms. The warning does not stop the command.

The one direct writer is the config editor, which writes `config.yaml` behind a hash-checked backup and CLI validation. The audit and inventory databases are only ever opened read-only.

## Install

### Requirements

- Windows 10 or 11, x64.
- DefenseClaw installed, or installable from the app (the Setup and Updates windows show what is missing).
- Nothing else: the exes are self-contained, so no .NET runtime needs to be installed.

### Choose a download

Each release at [Releases](https://github.com/Rmontalbano/defenseclaw_win/releases) has these assets. Replace `X.Y.Z` with the version.

| Download | What it is |
|----------|------------|
| `DefenseClawWin-X.Y.Z-Setup-x64.exe` | **Recommended.** Installer. Per-user, no administrator rights. Adds a Start menu entry and an Add/Remove Programs entry. |
| `DefenseClawWin-X.Y.Z-win-x64.zip` | Portable. Contains `DefenseClaw.App.exe` and `DefenseClaw.GatewayAdmin.exe` at the zip root. No installer, no registry entries. This is the file the app's self-update downloads. |

### Steps

1. Download the Setup installer from the latest release, and check it (see [Verify a download](#verify-a-download)).
2. Run it. It installs to `%LOCALAPPDATA%\Programs\DefenseClawWin\` without asking for administrator rights.
3. Leave "Start DefenseClaw for Windows when I sign in" ticked if you want the tray app at sign-in (see below).
4. Start **DefenseClaw for Windows** from the Start menu, or tick "Launch" at the end of the installer.

Or, in PowerShell, run the one-liner. It downloads the latest Setup installer, checks it, and runs it for the current user:

```powershell
irm https://github.com/Rmontalbano/defenseclaw_win/releases/latest/download/install.ps1 | iex
```

To pin a version, run the script as a script block, which works under any PowerShell execution policy (a piped script cannot take parameters, and a downloaded `.ps1` file is blocked by the default policy on a stock Windows client). `-Yes` runs Setup silently; DefenseClaw for Windows must not be running then, so exit it from its tray icon first:

```powershell
& ([scriptblock]::Create((irm https://github.com/Rmontalbano/defenseclaw_win/releases/latest/download/install.ps1))) -Version X.Y.Z
```

The script never elevates. It checks the installer's SHA-256 against `checksums.txt`, and checks the GitHub attestation when `gh` is installed and signed in (`gh auth login`); without a signed-in `gh` it says the attestation was skipped and relies on the SHA-256 checks.

For the portable zip, unzip it into a folder you can write to and run `DefenseClaw.App.exe` from there. Keep `DefenseClaw.GatewayAdmin.exe` in the same folder if you want administrator mode.

### Start at sign-in

The installer adds a per-user sign-in entry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `DefenseClawWin`) unless you untick it. Settings has a "Start with Windows" switch for the same entry. Windows also honours Task Manager's Startup tab, which can disable it. Starting the app at sign-in does not start the gateway. That is the separate "Start gateway automatically" setting.

### Administrator mode

Some runtime monitoring (agent actions, DefenseClaw 1.0) reads the Windows Security log, which needs an elevated gateway. Administrator mode runs your installed gateway with administrator rights. It is off by default and changes nothing until you turn it on. The full design, UAC prompts and the trust limit are in [docs/GATEWAY_ADMINISTRATOR.md](docs/GATEWAY_ADMINISTRATOR.md).

### Updates

The app checks GitHub for a newer release at launch and every six hours, and on demand: "Check for app updates…" in the tray menu, or "Check for Updates…" in Settings. It downloads the portable zip and `checksums.txt`, verifies them, swaps the files and relaunches. The rules, the rollback copy and how to update by hand are in [docs/UPDATING.md](docs/UPDATING.md). A failed or unavailable check does not mean you are up to date.

DefenseClaw's own runtime is upgraded separately, from the Updates window (see "DefenseClaw 1.0" below).

### Uninstall

Use Settings > Apps > DefenseClaw for Windows, or the uninstaller in the install folder. It removes the program files, the Start menu entry and the start-at-sign-in entry. It asks before it deletes your app data in `%LOCALAPPDATA%\DefenseClaw.App`; the default is to keep it. It never touches `%USERPROFILE%\.defenseclaw` or DefenseClaw itself.

If administrator mode was ever used, turn it off first, then remove `%ProgramFiles%\DefenseClaw Companion` from an elevated prompt. The uninstaller does not remove that folder. For the portable zip, delete the folder.

## Verify a download

The release is **not Authenticode-signed**. Windows SmartScreen may show "Windows protected your PC" on first run. Choose **More info**, then **Run anyway**, only after the checks below pass.

1. Compare the SHA-256 of the file with its line in `checksums.txt` from the same release:

   ```powershell
   Get-FileHash .\DefenseClawWin-X.Y.Z-Setup-x64.exe -Algorithm SHA256
   Select-String -Path .\checksums.txt -Pattern 'DefenseClawWin-X.Y.Z-Setup-x64.exe'
   ```

   The two hashes must match. The hash is also in the release notes.

2. Check the GitHub build-provenance attestation, which ties the file to a build from this repository's workflow (needs the [GitHub CLI](https://cli.github.com/), signed in with `gh auth login` or a `GH_TOKEN`):

   ```powershell
   gh attestation verify .\DefenseClawWin-X.Y.Z-Setup-x64.exe --repo Rmontalbano/defenseclaw_win
   ```

These checks show that the file matches what the release says it is, and that GitHub's build system produced it. They do not show that a particular person published it. Read the source if that matters to you.

## Build & run

Requires the .NET 9 SDK on Windows. `global.json` pins SDK 9.0.300 and allows the latest feature band.

```powershell
dotnet build DefenseClaw.Win.sln -c Release
dotnet test DefenseClaw.App.Tests -c Release
dotnet test DefenseClaw.Tests -c Release
```

`DefenseClaw.Tests` covers DefenseClaw.Core. `DefenseClaw.App.Tests` covers the app; it renders WPF views offscreen on a dedicated UI thread, so run it whole rather than filtered when you change views.

## What it connects to

| Source | Location | How the app uses it |
|--------|----------|---------------------|
| CLI | `defenseclaw.exe` in the install's bin folder, resolved at startup (`%USERPROFILE%\.local\bin` on 1.0; the 0.8.x Setup layout otherwise) | Subprocess. Every state change, and every read the app shows that needs the CLI. |
| Gateway REST API | `http://127.0.0.1:<gateway.api_port>` (default 18970) | Unauthenticated GET endpoints such as `/health` and `/status`. Local only. |
| Audit database | `%USERPROFILE%\.defenseclaw\audit.db` | Read-only SQLite (WAL). |
| Inventory database | `%USERPROFILE%\.defenseclaw\inventory.db` | Read-only SQLite (WAL). |
| Logs | `%USERPROFILE%\.defenseclaw\gateway.log` | Incremental tail with a file watcher. |
| Configuration | `%USERPROFILE%\.defenseclaw\config.yaml` and `.env` | Watched. Token resolution follows the CLI's priority order. |

Other behaviour to know about:

- **Managed installs are read-only.** The app works out which DefenseClaw it drives (`DEFENSECLAW_CONFIG`, the developer runtime selector, `DEFENSECLAW_HOME`, the managed Windows layout, then your profile). If that install is administrator managed (`deployment_mode: managed_enterprise`, or the managed layout), or the selection cannot be trusted, every control that changes something is disabled and says why. Reads and `--dry-run` previews still run. Settings > Connection > Installation shows what was selected and why.
- **WSL.** The app warns when port 18970 is served by a WSL-side gateway relayed by `wslrelay.exe`.
- **Fail mode.** The app warns when observe mode is paired with fail-closed hooks.
- **Connector status.** `not_certified` platform status is shown before setup, not after a failure.

## Panels

- **Monitor:** Overview, Alerts, Logs, Audit, Activity (every command run and its output).
- **Govern:** Skills, MCPs, Plugins, Tools.
- **Discover:** Inventory, AI Discovery, Runtime (needs DefenseClaw 1.0 with the AI Discovery runtime planes), Registries.
- **Configure:** Setup (guided wizards for connectors, LLM and guardrails, notification routing), Policies.
- **Settings:** connection, administrator mode, start at sign-in, the app's own update settings, and the CLI and file locations.

The command palette opens with Ctrl+K.

## DefenseClaw 1.0

- **Upgrade in the app.** The Updates window upgrades the DefenseClaw runtime to 1.0 through the DefenseClaw release's own `install.ps1`. The app checks that script against `checksums.txt` and, when `cosign` is installed, its sigstore signature. It shows a read-only pre-flight, a review of the exact command, and the install log afterwards. Roll back is offered when the installer can restore the previous install.
- **Update awareness** - checks DefenseClaw's GitHub releases; when you download an upgrade it compares the file's SHA-256 with the `checksums.txt` of the same release and, if `cosign` is installed, also verifies that file's sigstore signature against the upstream release workflow's identity (a signature cosign rejects stops the upgrade). Without cosign the signature is reported as published but *not verified*, and the SHA-256 comparison only shows the download matches that file. DefenseClaw's installer is not Authenticode-signed, and the Updates window says so.
- **Runtime monitoring.** The Runtime panel shows what each monitoring plane sees and what it cannot see. Anything the app cannot check is shown as "Can't check", not as a pass.
- **Run gateway as administrator.** An optional switch under Overview > Quick Actions and in Settings > Connection. See [docs/GATEWAY_ADMINISTRATOR.md](docs/GATEWAY_ADMINISTRATOR.md).

## Known notes

- **Leftovers from a 0.8.x Setup install.** Upgrading to 1.0 with the Setup-based install leaves two things behind. The app shows both as warnings and changes neither: a stale `DefenseClawGateway` sign-in entry (it does not autostart the gateway; use the app's own setting), and the 0.8.x Setup's Add/Remove Programs entry. Do not uninstall that entry from Windows Settings.
- **No automatic rollback from a Setup install.** `install.ps1 -Rollback` refuses when the previous install was a 0.8.x Setup install it cannot restore. The app then disables Roll back and shows the installer's reason.
- **The DefenseClaw resolver script.** `defenseclaw-upgrade.ps1` upstream assumes a POSIX-style `.defenseclaw\.venv` layout and fails with "Managed Python not found" on Setup-based installs. Use the Updates window instead.
- **Not yet verified on a real machine.** Administrator mode (the UAC prompts, the elevated gateway and its file ownership) and the app's self-update swap have been checked with fakes and temp folders only. Expect rough edges until they have been run on a real install.

## Security

Report vulnerabilities privately. See [SECURITY.md](SECURITY.md). Problems in DefenseClaw itself belong to [cisco-ai-defense/defenseclaw](https://github.com/cisco-ai-defense/defenseclaw).

## License

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Portions are adapted from [defenseclaw_mac](https://github.com/keitheobrien/defenseclaw_mac) (Apache-2.0).
