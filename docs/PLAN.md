# DefenseClaw for Windows — Full TUI-Parity Desktop App

## Context

Cisco DefenseClaw 0.8.7 ships natively on Windows (CLI/TUI, gateway, hook runtime, scanners) but has no graphical interface beyond the Setup wizard. The macOS community app [keitheobrien/defenseclaw_mac](https://github.com/keitheobrien/defenseclaw_mac) sets the bar: a native SwiftUI menu-bar companion with verified `defenseclaw tui` feature parity — 13 panels, 22 setup wizards, read-mostly architecture where **all state changes go through the CLI** with exact argv logged. This plan specs the Windows equivalent, tracked in Linear project [Windows DefenseClaw GUI](https://linear.app/remosclaws/project/windows-defenseclaw-gui-6e9330a6d849) (Client Work team). User chose **full parity in one push** (no staged v1).

We have a live dev/test environment on this machine: native 0.8.7 at `%LOCALAPPDATA%\Programs\DefenseClaw`, gateway on `127.0.0.1:18970`, `claudecode` connector in observe mode, populated audit DB.

## Stack decision: WPF on .NET 9 (researched WPF vs WinUI 3)

The app's "deep reach" needs are: tray-resident lifecycle (menu-bar analog), file tailing, SQLite reads, subprocess orchestration, a dynamic config editor, and charts. Framework comparison against those needs:

| Need | WPF (.NET 9) | WinUI 3 (Windows App SDK) |
|---|---|---|
| System tray / tray-first lifecycle | Mature (Hardcodet/H.NotifyIcon); tray-only apps trivial | **No native support** ([open issue #713](https://github.com/microsoft/WindowsAppSDK/issues/713)); third-party shims ([H.NotifyIcon.WinUI](https://www.nuget.org/packages/H.NotifyIcon.WinUI), [WinuiTrayIcon](https://libraries.io/nuget/WinuiTrayIcon)); tray popovers = positioned borderless-window hacks |
| Fluent / Win11 look | [Official Fluent theme GA in .NET 9](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90) via `ThemeMode`, [refined further in .NET 10](https://github.com/dotnet/wpf/discussions/10387); gaps coverable with WPF-UI library | Native, best-in-class |
| Config/YAML editor | AvalonEdit (syntax highlighting, folding) — WPF-only, ideal for the config editor | No equivalent-maturity editor control |
| Charts | LiveCharts2 / OxyPlot / ScottPlot, all mature | LiveCharts2/ScottPlot ports exist, younger |
| File tail, SQLite, subprocess | Same .NET BCL either way | Same |
| Tooling / ecosystem | XAML designer, 18 yrs of controls & answers | No designer, younger ecosystem |
| Distribution | **Single self-contained exe** (no runtime install, no MSIX cert needed) | Pushes MSIX + WinAppSDK runtime |

**Verdict: WPF.** The tray-first lifecycle is the load-bearing requirement (it's the menu-bar analog), and WinUI 3 still fights it in 2026. AvalonEdit and single-exe distribution seal it — the latter matters given DefenseClaw's own unsigned-installer saga: a portable exe sidesteps installer trust questions entirely. Fluent styling via .NET 9 `ThemeMode.System` + [WPF-UI](https://github.com/lepoco/wpfui) closes most of the looks gap.

## Architecture (mirrors the proven macOS design)

**Read-mostly companion.** Reads come from four channels; **every mutation shells out to the CLI** and is recorded in the Activity panel with exact argv, live output, and exit code — the GUI never writes DefenseClaw state directly.

| Channel | Windows source | Access |
|---|---|---|
| Gateway REST | `http://127.0.0.1:{gateway.api_port}` (default 18970) | `HttpClient`; GETs unauthenticated (`/health`, `/status`, `/alerts`, `/skills`, `/mcps`, `/tools/catalog`, `/enforce/blocked`, `/enforce/allowed`) |
| Audit DB | `%USERPROFILE%\.defenseclaw\audit.db` (+`inventory.db`) | `Microsoft.Data.Sqlite`, **read-only + WAL mode** (DB is actively written; schema known: `audit_events`, `findings`, `scan_results`, `correlation_*`, …) |
| Logs/stream | `%USERPROFILE%\.defenseclaw\gateway.log` (confirm `gateway.jsonl` exists on Windows — the mac app tails it) | `FileSystemWatcher` + incremental tail |
| Config | `config.yaml` + `.env` | YamlDotNet; watch for changes; replicate the CLI's **token priority ladder** (env var → `.env` → config literal) |
| CLI | `%LOCALAPPDATA%\Programs\DefenseClaw\bin\defenseclaw.exe`, `defenseclaw-gateway.exe` | `Process` with async stdout/stderr capture; secrets via stdin, never argv |

**Solution layout** (`C:\Dev\defenseclaw-win`):
- `DefenseClaw.Core` — no-UI class lib: REST client, SQLite readers, log tailer, config/env resolver, CLI runner, models. Unit-testable.
- `DefenseClaw.App` — WPF, MVVM via `CommunityToolkit.Mvvm`. Views/ViewModels per panel; `NavigationService` with 4 sidebar groups.
- `DefenseClaw.Tests` — xUnit against Core; fixture SQLite DBs and canned REST payloads captured from the live 0.8.7 install.

## Feature surface (full parity)

**Tray (menu-bar analog):** shield icon reflecting gateway/alert state (running/stopped/alerting); left-click flyout with at-a-glance summary (gateway state, active connectors, recent alert count) + open-dashboard / start-stop-gateway actions; toast notifications for new CRITICAL/HIGH findings (CommunityToolkit AppNotifications). Starts minimized to tray, autostart-on-login optional.

**13 panels in 4 groups:**
- **Monitor** — Overview (TUI parity boxes: What Needs Attention, Services, Scanners, Enforcement, Doctor findings), Alerts (live from `/alerts` + audit DB, severity filters, acknowledge via CLI), Logs (gateway/watchdog tail with level filter), Audit (audit_events browser: filter by bucket/severity/connector/time; detail pane renders `structured_json`), Activity (every CLI invocation the app made: argv, live output, exit status).
- **Govern** — Skills, MCPs, Plugins, Tools: list + scan results + block/allow/enable/disable via CLI (`defenseclaw skill|mcp|plugin|tool …`), enforcement override display.
- **Discover** — Inventory (AI components/SDK rollup from `inventory.db` + `defenseclaw agent components`), AI Discovery (discovered agents **with evidence detail and confidence** — directly addressing the false-positive/no-evidence gap we found), Registries (catalog sources).
- **Configure** — Setup hub launching the wizards.

**22 setup wizards:** one flow per `defenseclaw setup <target>` (connectors incl. claude-code/codex/cursor/…, skill-scanner, mcp-scanner, guardrail, LLM credentials, Cisco AI Defense, observability, webhooks, local-observability stack, …). Each wizard ends in a **review screen showing the exact command** before execution; runs it via the CLI runner; output streams into the wizard and Activity panel. Wizard catalog is data-driven (a `WizardDefinition` model) so new setups are additions, not new screens.

**Config editor:** AvalonEdit-based `config.yaml` editor with a typed, sectioned form view; section catalog generated at runtime from `defenseclaw config show --effective` so new runtime settings appear without an app update (mac-app pattern). Hash-checked backup before save; save then validate via `defenseclaw config` and surface errors.

**Connector filter:** global connector scope selector that propagates across Alerts/Audit/Govern views (mac-app pattern).

## Windows-specific behaviors (from hands-on 0.8.7 experience — differentiators, not afterthoughts)

1. **WSL coexistence detector:** if port 18970's owner is `wslrelay.exe`, the gateway shown is a WSL instance, not native — banner with one-click guidance (this exact collision cost us a morning).
2. **Connector certification surfacing:** parse each connector's `not_certified` platform status; badge uncertified connectors in wizards instead of letting setup fail like the codex ghost did.
3. **Fail-mode guardrails in UI:** surface `fail-mode` per connector prominently; warn when observe mode pairs with fail-closed (the recurring bad default).
4. **Install/update awareness:** detect installed version vs latest GitHub release; verify download SHA-256 against `checksums.txt` and show Authenticode + sigstore provenance status honestly (unsigned today).
5. **Path/runtime detection:** locate install via PATH → `%LOCALAPPDATA%\Programs\DefenseClaw\bin` fallback; handle "installed but not initialized" (offer init wizard) and "gateway stopped" states — all states we hit for real.

## Build order (single release, dependency-ordered)

1. `DefenseClaw.Core`: config/env resolver + REST client + SQLite readers + CLI runner (+ tests with fixtures captured from live install)
2. App shell: navigation, Fluent theming, tray icon + flyout, gateway state machine
3. Monitor panels (Overview → Alerts → Logs → Audit → Activity)
4. Govern panels + block/allow CLI actions
5. Discover panels (inventory.db reader, evidence detail)
6. Wizard engine + all 22 wizard definitions
7. Config editor (AvalonEdit + generated section forms)
8. Windows-specific behaviors (WSL detector, certification badges, update checker)
9. Toasts, autostart, polish; packaging (self-contained single exe; MSIX deferred)

## GitHub repository

- Create **`defenseclaw_win`** via `gh repo create` (name mirrors the community convention set by `defenseclaw_mac`), **private initially — flip to public when it's demo-ready** (say the word if you want it public from day one).
- Init git in `C:\Dev\defenseclaw-win`, MIT license (matches the mac app), README modeled on defenseclaw_mac's (what it is, screenshots, data-source table, "all mutations via CLI" principle), `.gitignore` for .NET + `*.user`, and a `docs/` folder holding this spec as `docs/PLAN.md`.
- **CI (GitHub Actions):** `windows-latest` build + `dotnet test` on PR; on tag, publish self-contained single-exe release with a `checksums.txt` of SHA-256 sums — practicing the provenance stance we've been auditing upstream for.
- Commit cadence: one commit per build-order step (1–9 above) so the history tracks the spec; link the repo from the Linear project.

## Verification

- **Unit:** xUnit on Core — token ladder resolution, YAML round-trip, audit query builders, CLI argv construction (secrets never in argv).
- **Live parity checks** against the running local 0.8.7: app Overview matches `defenseclaw status` field-for-field; Alerts matches `defenseclaw alerts --limit 25`; Audit counts match direct SQLite queries; wizard review screens emit byte-identical commands to the documented CLI.
- **Mutation safety:** run a wizard (e.g., guardrail toggle) and confirm the change is visible in `defenseclaw status` and reversible; confirm every mutation appears in Activity with argv + exit code.
- **Failure modes:** stop the gateway (`defenseclaw-gateway stop`) → app degrades to file/SQLite sources with a clear banner; start WSL gateway → coexistence banner fires.
- **Tray lifecycle:** close-to-tray, toast on new HIGH finding (generate one — trivially reproducible here: any `$env:`-referencing command trips CMD-ENV-DUMP), autostart.

## Risks / notes

- **API drift:** 0.8.x releases weekly with breaking churn; pin a tested runtime version (0.8.7), gate features on `/health` protocol version, degrade gracefully.
- **TUI parity source of truth:** docs don't fully enumerate TUI panels; verify against live `defenseclaw tui` and the mac app's panel list during build.
- **`gateway.jsonl` existence on Windows** unconfirmed — fall back to REST polling + audit DB if absent.
- **Signing:** ship unsigned exe initially (documented SHA-256), matching upstream reality; revisit Authenticode later.
