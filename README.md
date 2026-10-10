# DefenseClaw for Windows

A native Windows desktop companion for [cisco-ai-defense/defenseclaw](https://github.com/cisco-ai-defense/defenseclaw), replicating the `defenseclaw tui` terminal dashboard with WPF (.NET 9, Fluent theme) — the Windows counterpart to [defenseclaw_mac](https://github.com/keitheobrien/defenseclaw_mac).

> **Status:** in active development. Built and live-verified against DefenseClaw 0.8.10 on Windows 11.
>
> A community project, not an official Cisco product.

## What it is

A **read-mostly companion** for a local DefenseClaw installation: a system-tray shield with live gateway/alert state, and a dashboard with panels across four groups — **Monitor** (Overview, Alerts, Logs, Audit, Activity), **Govern** (Skills, MCPs, Plugins, Tools), **Discover** (Inventory, AI Discovery, Registries, and - on a DefenseClaw runtime that has the AI Discovery runtime planes - Runtime), and **Configure** (Setup wizards, guardrail controls, config editor) — plus Settings and a command palette (Ctrl+K).

## Design principle

**All state changes go through the `defenseclaw` CLI.** The app never writes DefenseClaw state directly; every mutation is executed as a logged CLI invocation whose exact argv, live output, and exit status appear in the Activity panel. The app refuses known secrets on argv: a value it recognises as a secret is never put on the command line. Other secrets reach the CLI through its own console prompt, stdin, or, for commands with an environment-variable fallback (Splunk and observability tokens), that one child process's environment. Three routes can still carry a secret on argv, because the CLI takes the value there: `mcp set --env`, `mcp set --url` and `plugin install <url>`. The review warns when a value typed into one of those forms looks like a secret; the warning does not stop the command.

## Data sources

| Source | Location | Method |
|--------|----------|--------|
| Gateway REST API | `http://127.0.0.1:<gateway.api_port>` (default 18970) | Unauthenticated GET endpoints (`/health`, `/status`, `/alerts`, …) |
| Audit database | `%USERPROFILE%\.defenseclaw\audit.db` | Read-only SQLite (WAL) |
| Inventory | `%USERPROFILE%\.defenseclaw\inventory.db` | Read-only SQLite (WAL) |
| Logs | `%USERPROFILE%\.defenseclaw\gateway.log` | Incremental tail via file watcher |
| Configuration | `config.yaml` + `.env` | Watched; token resolution follows the CLI's priority ladder |
| CLI | `%LOCALAPPDATA%\Programs\DefenseClaw\bin\defenseclaw.exe` | Subprocess for all state changes |

## Windows-specific features

- **WSL coexistence detection** — warns when port 18970 is actually a WSL-side gateway relayed by `wslrelay.exe`
- **Connector certification badges** — surfaces `not_certified` platform status before setup, not after failure
- **Managed installations are read-only** — the app works out which DefenseClaw it drives (`DEFENSECLAW_CONFIG`, the developer runtime selector, `DEFENSECLAW_HOME`, the Windows managed layout under Program Files and ProgramData, then your profile). When that installation is administrator managed (`deployment_mode: managed_enterprise`, or the managed layout) or the selection cannot be trusted, the app only reads it: every control that changes something is disabled with the reason, the Overview says *State-changing actions disabled*, Settings → Connection → Installation shows what was selected and why, and the command runner refuses any command that is not a read, whatever asked for it (the config editor, which writes `config.yaml` itself, asks the same guard). Previews (`--dry-run`) and reads still run. See [docs/PARITY-FOUNDATIONS.md](docs/PARITY-FOUNDATIONS.md) section 10
- **Fail-mode visibility** — warns when observe mode is paired with fail-closed hooks
- **Update awareness** — checks GitHub releases; when you download an upgrade it compares the file's SHA-256 with the `checksums.txt` of the same release and, if `cosign` is installed, also verifies that file's sigstore signature against the upstream release workflow's identity (a signature cosign rejects stops the upgrade). Without cosign the signature is reported as published but *not verified*, and the SHA-256 comparison only shows the download matches that file. The installer itself is not Authenticode-signed, and the Updates window says so

The in-app upgrade flow supports two channels: the release's Setup installer (recommended on Setup-based installs) and the `defenseclaw-upgrade.ps1` resolver script. The script is known broken on Setup-based installs upstream — it assumes a `.defenseclaw\.venv` POSIX-style layout and fails with "Managed Python not found"; it is byte-identical between 0.8.9 and 0.8.10, so this is not fixed by upgrading.

## Installing

Download `DefenseClaw.App.exe` and `checksums.txt` from [Releases](../../releases), then check the hash before running it:

```powershell
Get-FileHash .\DefenseClaw.App.exe -Algorithm SHA256
```

The value must match the line for `DefenseClaw.App.exe` in `checksums.txt`. Each release also carries a GitHub build-provenance attestation (`gh attestation verify DefenseClaw.App.exe --repo Rmontalbano/defenseclaw_win`). The exe is not Authenticode-signed yet, so Windows SmartScreen may warn on first run.

## Building

```
dotnet build DefenseClaw.Win.sln
dotnet test
```

Requires .NET 9 SDK on Windows. Releases publish as a self-contained single exe with SHA-256 checksums.

## Security

Please report vulnerabilities privately; see [SECURITY.md](SECURITY.md). Issues in DefenseClaw itself belong to [cisco-ai-defense/defenseclaw](https://github.com/cisco-ai-defense/defenseclaw).

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE). Portions are adapted from [defenseclaw_mac](https://github.com/keitheobrien/defenseclaw_mac) (Apache-2.0).
