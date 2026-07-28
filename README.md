# DefenseClaw for Windows

A native Windows desktop companion for [cisco-ai-defense/defenseclaw](https://github.com/cisco-ai-defense/defenseclaw), replicating the `defenseclaw tui` terminal dashboard with WPF (.NET 9, Fluent theme) — the Windows counterpart to [defenseclaw_mac](https://github.com/keitheobrien/defenseclaw_mac).

> **Status:** in active development, targeting full TUI parity against DefenseClaw 0.8.7.

## What it is

A **read-mostly companion** for a local DefenseClaw installation: a system-tray shield with live gateway/alert state, and a dashboard with 13 panels across four groups — **Monitor** (Overview, Alerts, Logs, Audit, Activity), **Govern** (Skills, MCPs, Plugins, Tools), **Discover** (Inventory, AI Discovery, Registries), and **Configure** (Setup wizards + config editor).

## Design principle

**All state changes go through the `defenseclaw` CLI.** The app never writes DefenseClaw state directly; every mutation is executed as a logged CLI invocation whose exact argv, live output, and exit status appear in the Activity panel. Secrets are passed via stdin, never argv.

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
- **Fail-mode visibility** — warns when observe mode is paired with fail-closed hooks
- **Update awareness** — checks GitHub releases, verifies SHA-256 against the signed `checksums.txt`, and reports Authenticode/sigstore provenance status honestly

## Building

```
dotnet build DefenseClaw.Win.sln
dotnet test
```

Requires .NET 9 SDK on Windows. Releases publish as a self-contained single exe with SHA-256 checksums.

## License

MIT — see [LICENSE](LICENSE). Not affiliated with Cisco; DefenseClaw is a trademark of its respective owners.
