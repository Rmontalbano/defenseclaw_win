# Parity foundations (CUST-200)

Four small building blocks the Mac-parity panels share. Each lives on `AppServices` (`Services.` from a panel view-model), has no UI, and
is documented in its own XML summary; this page is what each is *for* and how to reach for it. Everything is UI-thread-friendly and
nothing here starts work until something asks.

| Block | Reach it as | Files |
|---|---|---|
| Settings store | `Services.Settings` | `Services/Settings/*` |
| Navigation requests | `Services.Navigation`, `RequestNavigation(...)` on `PanelViewModelBase` | `Services/Shell/NavigationRequest.cs`, `PanelCatalog` |
| Alert counts | `Services.AlertCounts` (kept fresh) / `Services.AlertQueue` (raw reader) | `Core/Audit/AlertQueueReader.cs`, `AlertCounts.cs`, `Services/AlertCountsService.cs` |
| Connector scope | `Services.ConnectorScope` | `Services/ConnectorScope.cs` |
| TUI command catalogue | `ShellActions.CliCatalogue`, `TuiRegistryCatalogues.For(...)` | `Core/Cli/TuiRegistry*.cs`, `Services/Shell/CuratedCliCommands.cs`, `tools/gen-tui-registry.py` |
| Docker look (local observability stack) | `Services.LocalStack` | `Services/Wizards/LocalStackAvailability.cs`, `LocalStackReview.cs`, `DockerProbe.cs` |
| Change probe | `Services.AuditChanges`; `RowsDecoded` / `UnchangedReads` on each reader | `Core/Audit/AuditChangeProbe.cs`, `SnapshotMemo.cs`, `OversizedValue.cs` |
| Catalogue trust | `CatalogTrust` (`Trust` on every panel with a list), `ConfigDiskSignature` | `ViewModels/Govern/CatalogTrust.cs`, `Core/Config/ConfigDiskSignature.cs`, `DiscoverActionReview.RunGuard`, `CliRunner.RecordRefusal` |

## 1. Settings store - `AppSettingsStore`

**For:** every persisted app setting, in one file, `%LOCALAPPDATA%\DefenseClaw.App\settings.json`. Typed sections: `Appearance` (style, mode;
still owned by `AppearanceService`), `Monitoring` (`HealthIntervalSeconds` 2-60, default 5; `Paused`), `Notifications` (`Critical`, `High`,
`Gateway`, `HighWaterUnixNano`), `Startup` (`GatewayAutoStart`, `CloseToTray`, `RememberLastPanel`, `LastPanelId`), `Connection`
(`CliPathOverride`), `Updates` (`LastCheckUnix`, `DismissedVersion`, `NotifiedVersion`; written by `UpdateWatcher`).

```csharp
var interval = Services.Settings.Current.Monitoring.HealthInterval;                  // read: cached, any thread
Services.Settings.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } });   // change: atomic, persisted
Services.Settings.Changed += (_, e) => { if (e.Affects(AppSettingsSections.Monitoring)) Apply(e.Current.Monitoring); };
```

- One store per file per process (`AppSettingsStore.ForPath`), so its lock is the only writer; `FileAppearanceSettingsStore` is a thin client of it.
- Only the sections an update changes are written, into the file as it is on disk: unknown sections/members and a newer `schemaVersion` survive; the write is temp-file + flush + move.
- Never throws for what is on disk: missing/empty/corrupt/wrongly-typed content reads as defaults (per field); a failed write returns `false` from `Update`, keeps the change in memory and retries it with the next write.
- `Update` writes synchronously (a few ms: the flush is what makes it crash-safe), so do not call it per keystroke or per frame - set a value when the operator commits it, or coalesce (e.g. remember `LastPanelId` when a panel activates, not while it scrolls).
- `Changed` is raised on the thread that called `Update`, after the lock is released; marshal to the UI thread if you touch controls. The `change` function must only build a value (no I/O, no call back into the store).
- Adding a section needs no migration: a section the file lacks is its defaults. Extend `AppSettings`, `AppSettingsCodec` (one read, one write) and `AppSettingsSections`.

## 2. Navigation requests - `ShellNavigation` / `IAcceptsNavigation`

**For:** "show this panel, and tell it this" (the Mac's `openAlerts / openAudit / openLogs`). A request is `{ PanelId, Payload }`; payloads today:
`AlertsFilter(SeverityFloor?, Kind?)`, `AuditPreset(Name)`, `LogsPreset(Name)`. A target panel ignores payloads it does not understand.

```csharp
RequestNavigation("alerts", new AlertsFilter(AuditSeverity.Critical));              // from any panel view-model (or Services.Navigation.Request(...))
public sealed partial class AlertsPanelViewModel : PanelViewModelBase, IAcceptsNavigation
{ public void Accept(object payload) { if (payload is AlertsFilter f) { SeverityFloor = f.SeverityFloor; _ = ApplyFilterAsync(); } } }
```

- Exactly one request is outstanding; a second replaces one nobody has taken. It is **consumed once**: the catalog hands it to the panel when that panel *activates* (or at once if it is already on screen) and never again.
- `Accept` runs on the UI thread **right after `OnActivated`** (subscribed, active), including on the very first visit where `InitializeAsync` may still be running: apply the payload to state the load will honour. An exception from `Accept` is traced and swallowed.
- A request for a panel that is not built, or is in a hidden/minimized window, waits in the inbox. The shell shows the dashboard (building it if the session is tray-only) and selects the panel; a new window opens on `PanelCatalog.InitialPanel`. An unknown panel id is dropped with a trace.
- The status strip / tray can raise one without a panel: `MainWindowViewModel.RequestNavigation`, `OpenPanelCommand` (`CommandParameter="alerts"`), `ShellActions.OpenPanel`.
- A panel can also ask the shell for its command palette (the Overview's Diagnostics menu): `Services.Navigation.RequestPalette()` raises `PaletteRequested`, which the dashboard window answers by opening the palette. It is not a panel request, so nothing waits in the inbox.

## 3. Alert counts - `AlertQueueReader` / `AlertCounts` / `AlertCountsService`

**For:** the Mac's single "unacknowledged findings" number, so the sidebar badge, status chip, tray and Alerts panel agree. The definition
(`AuditStore.alertQueueEvents`): the newest 500 `audit_events` with severity CRITICAL/HIGH/MEDIUM/LOW, `bucket IS NULL` *or* `security.finding` + `finding.observed`,
`action NOT LIKE 'dismiss%'`, and not in `alert_acknowledgement_projection` (table may be missing). So the count caps at 500 (`HasMore` says "500+").

```csharp
Services.AlertCounts.Changed += (_, e) => Badge = e.Counts.TallyFor(Services.ConnectorScope.Allows).Total;  // subscribing starts it; leaving stops it
await Services.AlertCounts.RefreshAsync();                                          // after an acknowledge: badge drops now, not in 30 s
var newest = (await Services.AlertQueue.ReadAsync(newestLimit: 500)).Counts.Newest; // raw read: Id, Severity, Action, Target, Connector, Timestamp
```

- `AlertCounts`: `Tally` (per severity), `Total`, `HasMore`, `Newest` (first N rows), `ByConnector`, `TallyFor(allows)`, `SameAs`. `Source` is `Database`, or `Gateway` when the database predates schema v8 (no `bucket`): then the counts come from the monitor's already-fetched `/alerts` list (25 rows, so `HasMore` is usually set) - no extra request.
- The service refreshes on `GatewayMonitor.AlertCadenceElapsed` (the 30 s alert cadence, raised after the poll is published, independent of the gateway being up), once when the first subscriber attaches, and on `RefreshAsync`. **No subscriber, no work**: it is detached from the monitor and runs no query. `Changed` (UI thread) is raised only when what a badge shows differs, on first data, or when the counts turn unavailable/recover. A failed read keeps the last good counts and sets `Unavailable`; it never zeroes the badge.
- Read-only (`Mode=ReadOnly`), off the caller's thread (`ReaderOffload`), with a timeout (default 10 s) and cancellation that interrupt the running statement. Query plan is pinned by tests: two index searches (`idx_audit_event_name_timestamp` / `idx_audit_bucket_timestamp`) merged, no table scan. On the live 6.7 GB database: ~4 ms warm (the Mac's single-statement form is ~55 ms).

## 4. Connector scope - `ConnectorScope`

**For:** the one "All / one connector" filter every screen shares (the Mac's `connectorFilter`). Every connector-tagged panel follows it (override `PanelViewModelBase.OnConnectorScopeChanged`, filter with `Allows`); each page toolbar's ConnectorSlot holds `ctl:DcConnectorScopeChip Model="{Binding ConnectorChip}"` (shown with more than one connector); Ctrl+Shift+M cycles it. Audit and the Govern catalogs keep their combos as views of the same scope.

```csharp
var rows = allRows.Where(r => Services.ConnectorScope.Allows(r.Connector));          // the predicate; null scope allows everything
Services.ConnectorScope.Cycle();   // All -> first -> second -> ... -> All     (Set("codex") for a menu pick; Connectors is the roster)
Services.ConnectorScope.Changed += (_, _) => Refilter();                              // scope or roster changed
```

- Roster = `GatewaySnapshot.ActiveConnectors` (configured first, then live). The scope **resets to All by itself** when its connector leaves the roster or the roster shrinks to one or none; `Set` refuses a connector not on the roster; `CanScope` (roster > 1) is what shows the chip.
- An explicit scope is an exact match (ignoring case/padding) and **hides rows with no connector**, like every other Mac screen.

## 5. Runtime detection and gating - `Services.Runtime` / `RuntimeGate`

**For:** showing something only newer DefenseClaw runtimes have (verified against source commit 95159fd, which self-reports 1.0.0) and nothing else. 0.8.10 is the baseline: a feature it already has is never gated.

```csharp
if (Services.Runtime.Check(RuntimeCapability.AcpGuard).IsAvailable) { /* offer it */ }       // GateDecision: IsAvailable, Reason
var why = Services.Runtime.Check(RuntimeCapability.Sandbox).Reason;                            // "Requires a compatible DefenseClaw runtime (verified against source commit 95159fd)"
Services.Runtime.Changed += (_, _) => Rebuild();                                               // UI thread; first answer, upgrade
new PanelDescriptor(..., Requires: RuntimeCapability.AcpGuard)                                 // palette "Go to" row is disabled with the sentence until it is true
RuntimeGate.CheckSetupCommand(Services.Runtime.Capabilities, "kiro")                           // setup --help names it
```

- **Capabilities** (`PolicyModel`, `AcpGuard`, `RedactionAdvanced`, `Sandbox`, `CanonicalSchema8`, `TuiRegistry`, `AiRuntime`) are decided by `RuntimeProbe` from `--version-json` plus the `Commands:` lists of `--help` screens (the Mac's test). Markers are listed by `RuntimeCapabilityCatalog.Marker` and shown in About; each is absent on 0.8.10 and present at the pin. `CanonicalSchema8` also needs version 1.0.0 or later. `TuiRegistry` is the command markers (`setup` lists amp, devin, kiro), not a count of entries. `AiRuntime` (the Runtime panel, CUST-309) needs two screens: `agent discovery --help` lists `runtime` (0.8.10's lists disable, enable, scan, setup, status) and `agent discovery runtime --help`, probed only then, lists `status`, `scan`, `findings` and `permissions`. The subcommands that second screen lists are kept too, one by one (`Capabilities.HasAiRuntimeCommand("scan")`, like the Mac's `runtimeDiscoveryCommands`), so a runtime that lists `scan` but not `enable` offers Poll now and not Enable.
- **A gated panel is not just a disabled palette row.** `PanelCatalog.IsOffered(panel)` is the one test: the sidebar entry is hidden (and appears when the probe answers), the palette row is disabled with the sentence and shows no chord, `MainWindow.NavigateTo` and `PanelCatalog.InitialPanel` refuse it, and its number chord does nothing and is left out of the shortcuts list. The chords count in `PanelCatalog.ChordOrder` - the panels that need nothing in sidebar order, then the gated ones - so a panel that appears in the middle of the sidebar moves no existing chord (Runtime is Ctrl+Shift+5; `ShellShortcuts.NumberedPanels` is 15).
- **Fail closed.** Nothing probed yet, a timeout, a missing CLI, garbage output: `Capabilities.Unknown`, every flag false, features hidden. A failure is never read as "empty".
- **A panel with two surfaces** (Policies, CUST-293; see `RUNTIME-COMPAT-95159fd.md`): when what a panel *is* depends on the runtime, it asks `Services.Runtime.WhenProbedAsync()` on its first visit (the app's own first probe is awaited when the service is running; a service nothing started answers at once, so a tool or a test never waits for a probe that will not come), picks its surface with `PolicyBackends.For(Capabilities)`, and re-decides on `Services.Runtime.Changed` while it is on screen. Unknown is the 0.8.10 surface, and that surface's view tree is exactly what it was (the newer one is built only when the runtime has it).
- **A panel whose newer fields are in the payload** (AI Discovery models, CUST-310; see `RUNTIME-COMPAT-95159fd.md`): no help screen marks it, so there is no `RuntimeCapability`. A column, a filter or a chip exists only when some model carries what it shows (the Mac has no probe for it either), and a runtime that sends none of it - 0.8.10 - gets the panel it always had. The gateway's own report (`GET /api/v1/ai-usage`) is read through the bounded read of the gateway client and only adds those fields to the signals the files list.
- **Cache** (`RuntimeDetector`): keyed by the CLI file's path, size and modified time (a container: its name, for a short window). Re-probed when the key moves (an upgrade is noticed by the 30 s background check) or on "Check the runtime again". An unknown answer is retried after 15 s. Probes run `--version-json` and `--help` only (the production runner refuses anything else), each with a 20 s limit and 45 s for the round, and never appear in Activity.
- **Developer runtime selector** (Settings -> Advanced, `developer.*` in settings.json, off by default; off is byte-for-byte the installed runtime): `Installed`, `Cli` (explicit defenseclaw.exe + `DEFENSECLAW_HOME` + optional loopback gateway address), or `Container` (`docker exec` + published loopback gateway + a read-only host copy of the data folder). It is read once at startup (`AppServices.CreateStartupPaths` -> `RuntimeEnvironment.CreatePaths`), so a change applies on the next launch; an incomplete or invalid choice starts the installed runtime. Container mode runs `defenseclaw` and `defenseclaw-gateway` through `CliRunner.RunNamedAsync` as `docker exec [-i] [-e NAME] CONTAINER TOOL ARGS`: environment values stay in docker's own environment (names only on argv), and the config editor refuses to write the copy (`DefenseClawPaths.DataDirectoryReadOnly`).

## 6. TUI command catalogue - `TuiRegistryCatalogues` / `CuratedCommandCatalog`

**For:** the command palette's CLI rows (the Mac sheet's "all 253 current TUI entries"): one row per entry of the connected runtime's own TUI command registry (`defenseclaw/tui/registry_data.py`: name, binary, argv, description, category, needs-argument flag, hint), with the TUI's names, so `scan skill --all`, `skills` and `skill list` are three rows for the commands operators already know.

```csharp
var catalogue = actions.CliCatalogue;                                  // ShellActions: 0.8.10's registry, or the pinned source's once the runtime shows RuntimeCapability.TuiRegistry
var offered = catalogue.Commands; var note = catalogue.HiddenNote;     // what Windows runs / "21 hidden on Windows"
var all = TuiRegistryCatalogues.For(Services.Runtime.Capabilities).Entries;   // every entry, each with its WindowsUnavailable reason or null
```

- **Generated, checked in.** `tools/gen-tui-registry.py` reads both registries (the installed 0.8.10 and the pinned source commit) as data with `ast` - nothing of DefenseClaw is imported or run - and writes `Core/Cli/TuiRegistryData.Generated.cs`: the entries plus, in its header, which runtime and file each came from (version or commit, path in the package, SHA-256 with LF endings, entry counts). `--check` compares without writing. The counts are 231 (0.8.10) and 253 (pin); a refresh that adds an entry, an option or a read-only command fails a test until someone has read what it does (`TuiRegistryCatalogueTests`).
- **Which registry.** Follows `Services.Runtime`: `TuiRegistry` present gives the pin's, everything else (0.8.10, a runtime that has not answered, one that failed to) gives 0.8.10's, which is never gated. The palette rebuilds in place when the answer changes.
- **Hidden on Windows.** Each runtime's own platform table decides (`setup <connector>` unless supported or preview; the sandbox group; the local stack controller): 232 of the pin's 253 are offered (its own `build_registry("windows")`), 210 of 0.8.10's 231 (the 0.8.10 CLI refuses the same things there). The palette says how many it hid and, in the note's tooltip, why, in the runtime's words.
- **Tiers and review.** Every row has a tier from `CommandTiers` (unknown is a change). Only a bare read on the allow-list (`CommandTiers.UnreviewedReadPaths`, plus the gateway's `status` and `provenance show`) runs without a review; a read with an option or a typed value on it, and everything else, is shown in `CommandReview` first. Options on an argv are limited to `TuiRegistryCatalogues.ReviewedFlags`; the gateway is never run bare.
- **Arguments.** A hint that is one word (`<skill-name>`) or one choice (`<observe|action>`) opens a box in the detail pane: Enter asks for the value, a second Enter reviews `... -- <value>` (a name the CLI would rewrite, such as `~`, is refused first). A hint with more in it, and anything that asks questions at a prompt (`keys set`, bare `setup`, the registry's "interactive" commands), is copied for a terminal instead. `start`, `stop` and `restart` of the gateway go the tray's way.
- **Ids** are `cli.` plus the TUI name with its spaces as dots (`cli.skill.list`), stable across catalogues, so a remembered "last command" can find its row again.

## 7. Docker look for the local observability stack - `Services.LocalStack`

**For:** offering `setup local-observability` (the Setup hub card, and the palette's registry rows for `up | down | reset | status | logs` and the bare group) only while Docker can run it - Compose v2 present and an engine that answers - and saying the probe's reason otherwise. 0.8.10 runs the stack natively on Windows (`platform_support.local_observability_stack_supported`, see the comment on `WizardWindowsPolicy.LocalObservabilityTarget`), which is also why both TUI registries list it there (section 6: `tools/gen-tui-registry.py` reads that function), so nothing about it is hidden by name; the CLI's own preflight stays the authority and the probe's warnings (WSL 2, per-user install, edition, ...) ride along on the review.

```csharp
var gate = Services.LocalStack.Decision;               // GateDecision: IsAvailable, Reason ("Checking for Docker…" before the first answer)
_ = Services.LocalStack.EnsureFreshAsync();            // looks only if there is no answer yet or it is old (60 s for a yes, 15 s for a no)
_ = Services.LocalStack.RefreshAsync();                // looks now (the Setup page's Refresh)
Services.LocalStack.Changed += (_, _) => OnUiThread(Reapply);   // raised on the probe's thread, and only when the answer changed
WizardWindowsPolicy.NeedsDocker(target) / .CommandNeedsDocker(argv)   // which card / palette row is gated (url and env are not: they print constants)
```

- **One look, shared.** `DockerProbe` runs `docker compose version`, then `docker info` (the CLI's preflight order) - read-only, never starts, pulls or runs anything. The Setup card and the palette read the same answer, so they cannot disagree, and Docker is looked at once rather than once per surface.
- **Not a poll.** No timer. The hub asks when it comes on screen and on Refresh; the palette when it opens (`ShellActions.CheckLocalStack`, which asks only if the palette lists the stack's rows), and `MainWindow` rebuilds an open palette's rows in place when the answer changes. Concurrent callers join the look already running. Once there is an answer it keeps being given while a newer one is fetched, so a card does not flicker into "Checking..." on every visit.
- **Fail open on "could not look".** A probe that throws or cannot run is `DockerState.Unknown`, which does not close the feature (the CLI checks again); only a definite no (not installed, engine down, no Compose) does.
- **Rows and tiers.** The registry's `url` row never reaches Docker and is always enabled (`env` has no registry row). The bare group row is the CLI's `up` (its group callback invokes it, whatever the registry's one-line description says), so it is gated and reviewed as `up`. `CommandTiers.IsReadOnlyLeaf` names `status | logs | url | env` by exact path - `setup` would make each a change - and they are on the allow-list (section 6): the three registry reads run without a review, `up`, `down` and the bare group change state, and `reset` is destructive (the registry's argv carries the `--yes` that stands in for the CLI's "Continue?", and the review says so).
- **Reviews say what the verb does.** `LocalStackReview` is shared by the palette's confirmation and the wizard's last page: the files `up` refreshes, the data `reset` deletes, the destination `down` leaves enabled, the Docker look's cautions, and a gateway restart only for the verbs that rewrite config.yaml (`up` unless `--no-config` or `--no-wait`; `down --disable-config`), from the `setup` group's result callback in the CLI's source.

## 8. Change probe and oversized rows - `AuditChangeProbe` / `OversizedValue` (CUST-284)

**For:** "has `audit.db` changed since I last looked?" answered by one trivial statement, so a refresh that finds nothing new costs a probe and not a query, a decode and a diff; and "a value too big to load is *unavailable*, not cut off and not an empty list" (the Mac 1.1.26's unchanged-snapshot skip and `lastQuerySucceeded = !rows.isEmpty`).

```csharp
var before = await Services.AuditChanges.SampleAsync(ct);                    // an opaque AuditStamp, taken BEFORE the read
var rows = await Services.Audit.QueryAsync(query, ct);
if (!(await Services.AuditChanges.SampleAsync(ct)).Matches(before)) { /* something was committed since: read again */ }
```

- **What shares it.** `AppServices` builds one probe over the live file and hands it to `Audit`, `AlertQueue`, and the readers the Mutations tab, the Logs streams and the Alerts egress feed build (`new MutationReader(path, Services.AuditChanges)`). Each reader remembers its last answers by query/limit/stream under the stamp it took *before* reading (`SnapshotMemo`); a call that finds the same stamp returns the remembered **object** (no connection, no statement, no row decoded; `UnchangedReads` counts them, `RowsDecoded` the rows that were), so a view-model can tell "nothing changed" by reference. A commit that lands between the stamp and the statement makes the next sample differ, so the worst case is one read too many, never a stale one. Unknown stamps (no probe, missing file, a database that could not be sampled) match nothing; failed and cancelled reads store nothing; a remembered answer is not trusted past 5 minutes (backstop only). The Audit live refresh (CUST-262) polls `SampleAsync` and fetches only when the stamp moved: it needs no timer of its own beyond its poll.
- **Why `PRAGMA data_version` on a kept read-only connection** (the alternative was the size / modified time of `audit.db` and its `-wal`): it is exact in rollback-journal and WAL mode, never reads a table page (the same cost on 0.6 GB and 10.4 GB: 0.08-0.13 ms for a whole unchanged read, probe included), holds no transaction between statements (a `wal_checkpoint(TRUNCATE)` still completes, pinned by a test), and a writer committing as fast as it could was never missed (0 of 241). File metadata is not exact on this platform: the WAL is *reused* after a checkpoint, so its size stops moving while the content keeps changing (73 of 241 commits left the length unchanged), and the modified time is a coarse clock tick (2 of 214 commits 20 ms apart looked like none). A wrong "unchanged" leaves a screen stale; a wrong "changed" only costs a read. The kept connection adds no pin that was not there: the readers' pooled connections hold the file for the life of the process too (measured: still held 200 s after `Close()`, released only by `ClearAllPools`). `Release()` closes it for a caller that wants the file back; the next sample opens a new connection with a new epoch, and every older stamp is then stale. No timer, no thread: it does something only when asked.
- **The archive (CUST-299) is a file stat.** An `immutable=1` reader gets a probe of its own: the stamp is the file's size, modified time and creation time (a replaced archive moves them); nothing is opened, locked, created or written beside it.
- **Oversized rows.** Wherever a reader limits a payload, a value over the limit is left in the database - decided by `octet_length`, which reads the record header and not the value (100 rows of which 12 held 2 MB: 0.2 ms, against 26 ms for `length(CAST(x AS BLOB))` and 28 ms for `substr`) - and the row is returned with its column empty and `Oversized` naming the column, its size and the limit (`structured_json is 3.2 MB, over the 256 KB limit`); its other columns are intact. Limits: `AuditReader` `details` / `structured_json` 256 KiB each (`AuditQuery.PayloadLimitBytes` lifts it for an export, which must not drop text), `MutationReader` 256 KiB per before / after / diff / reason / record, `EventStreamReader` 64 KiB of `payload_json` (the existing `PayloadOmitted`, now with the reason), `NetworkEgressReader` 64 KiB. Mutation and egress values used to be `substr`-cut: a document cut mid-way reads as a different one (an egress decision was read as "not allowed"). A page of rows that are all oversized is a page of rows (`AuditPage.AllOversized`), never "no events": the Audit panel lists them, says why in the Details cell and the inspector (in a code box as `(not shown: ...)`, broken into lines that fit it by `OversizedValue.Placeholder(width)`: the boxes scroll sideways, and one long line is cut off under the scroll bar), and the filter strip reads `1 event too large to display`; Alerts and Mutations say the same in their notes; the Logs raw JSON carries `payload_unavailable`.
- **What the panels do with an unchanged answer.** Audit: a fresh load that reads the page and total already shown (same ids, same order) leaves the rows, the selection and the scroll position alone; "Load more" extended the list, so a refresh still starts over from the newest page; the time window starts on a whole minute so two refreshes inside it ask the same question. Alerts: a queue read that finds the same finding and egress ids rebuilds nothing (no hydration, no filter pass). Mutations: the same changes leave the list alone. Logs: the reader hands back its last list, and a row already projected is reused by id, so a read after the database moved (it does all day) parses and masks only the rows that arrived.
- **Measured** (synthetic database from the real DDL, 1.25 M audit rows, 10.4 GB; ms per read, wall / CPU; the unchanged column includes the probe): alert queue 11.1 / 12.5 -> 0.13 / 0.16; mutation history 16.9 / 16.3 -> 0.12 / 0.10; Verdicts stream 171 / 247 -> 0.12 / 0.10 (a read after the database moved: 125 / 117, the id reuse); Events stream 82 / 84 -> 0.08 / 0.10; Audit page of 100 + 24 h total 20.7 / 23.1 -> 0.09 / 0.16; egress feed 1.6 / 1.9 -> 0.08 / 0.10. A read after a *change* costs what it did (the probe adds ~0.04-0.07 ms). The probe: 3 ms to open and first sample, then ~40-70 us per sample at either size.

## 9. Catalogue trust and the config disk signature - `CatalogTrust` / `ConfigDiskSignature` (CUST-283, CUST-312)

**For:** "may the rows on screen authorize a change?" Skills, MCPs, Plugins and Tools (`GovernPanelViewModelBase`), Registries, and Policies (the 0.8.10 table and the model surface, `PolicyModelViewModel`) each own a `CatalogTrust`; the Runtime panel's STALE snapshot follows the same rule with its own flag. The Mac's Policies counts as current only while the installation and the `config.yaml` / `.env` disk signature are the ones the read saw (`PoliciesView.swift:25-29`); here that is the config half (the installation is fixed at launch).

```csharp
Trust = CatalogTrust.Watching(services.Paths);        // in the panel's constructor: the list goes stale when config.yaml or .env change
Trust.BeginRead();                                     // where a read really starts: notes the files' signature for the rows it produces
Trust.MarkComplete();  // or MarkPartial(diagnostics) / MarkFailed(message)
Services.ConfigReloaded += OnConfigReloaded;           // OnActivated (and -= in OnDeactivated): if (Trust.CheckConfig()) NotifyTrust();
if (Trust.ReasonNow() is { } why) { /* refuse */ }     // at the moment a change is requested or confirmed
review.RunGuard = () => /* the same, at Confirm */;    // DiscoverActionReview: nothing runs when it answers
```

- **What turns changes off** (`Trust.Reason`, one sentence for the tooltip; the rows stay for every one, and Info, Copy name and Refresh stay on): the first read still running, a partial read (the CLI named a source it could not read), a failed refresh (the last good rows stay, labelled), a read older than 10 minutes, and - CUST-312 - a read made before `config.yaml` or `.env` changed. A fresh complete read restores trust.
- **The signature is a stat.** `ConfigDiskSignature` is the path, whether it exists, the length and the last-write time (UTC) of each file - the Mac's rule (`InstallationContext.diskSignature`: path, modified time, size) - taken with `FileInfo`. Nothing opens, reads or hashes either file: `.env` holds the gateway token and every key `keys set` stored, so the type has no member that could carry content (a test pins its shape and takes one of an exclusively locked file). The config watcher's own `FileSignature` hashes small files so it can tell two same-length saves apart; the trust deliberately does not use it. A same-length save inside one file-system clock tick is not seen by a stat - the Mac has the same limit - and only matters for an edit made within a few milliseconds of the read on screen.
- **When the files are looked at** - only when something happens, never on a timer and never from a bound property (`IsTrusted` and `Reason` are pure; rows ask them for every cell): a read starts (`BeginRead`) and ends (`MarkComplete` / `MarkPartial`; files that moved while it ran make the rows stale at once, because nothing says which side of the edit it saw); `AppServices.ConfigReloaded` is raised while the panel is on screen; the panel comes back on screen (a change made while it was away); and a change is requested or confirmed (`ReasonNow`), which is what covers the gap before the watcher has spoken. Two stats each.
- **No new watcher, timer or poll.** `ConfigChangeToken` already watches both files (a directory watcher with a 2 s poll as backstop) and `AppServices` raises `ConfigReloaded` for either, on the UI thread, so `.env` needs nothing of its own. Panels subscribe only while active (`OnActivated` / `OnDeactivated`), like every other subscription. The event is *compared*, not believed: a panel that re-reads straight after its own change (the CLI rewrote `config.yaml`; Registries also calls `ReloadConfig()`) is not made stale by the watcher's notification, which arrives a few hundred milliseconds after the read began. A test fixes that no type of the rule owns a timer, a watcher or a thread.
- **What stale does.** The rows stay; every action is off with `CatalogTrust.ConfigChangedReason()` - *Changes are off: config.yaml or .env changed after this list was read. Refresh to act on current data.* (the Policies model surface says "these settings"; the Runtime panel's STALE banner says the snapshot was read before the file changed) - as the buttons' and menu items' tooltip, the same place as every other reason. Govern and Registries do not read on the event (Refresh does) but read once on the next visit if they are stale, as they do for an old list; the Policies model surface reads by itself when idle (CUST-293).
- **Refused at execution.** A change started after the edit is refused when it is requested (`RefuseUntrustedChange` / `RefuseChange`, result bar or notice) and again when it is confirmed: the Govern panels in `ConfirmYes`, every review through `DiscoverActionReview.RunGuard` (Registries, Policies, the model surface, Runtime). Nothing starts; the dialog finishes with *Not run. <reason>*, and `CliRunner.RecordRefusal` writes the command that was refused into Activity - no exit code, `refused - <reason>`, badge "refused", its argv checked for secrets like any run's (an argv that holds one is refused and not recorded). The follow-up of the review (`onFinished`) is not called: nothing ran.
- **Runtime panel.** It keeps its own stale snapshot (`MarkStale`, the STALE banner, the note under the buttons, `BlockedReason`) and records the signature per read the same way; Poll now, Enable and Disable are off while it is stale for any reason, and a review confirmed on a snapshot that went stale is refused.
- **For the next panel with a list that actions are taken on:** construct the trust with `CatalogTrust.Watching`, call `BeginRead` where the read starts, subscribe while active, ask `ReasonNow` before a change and set `RunGuard` on its `DiscoverActionReview`. `CatalogTrust` needs a signature source for the config rule; `new CatalogTrust()` has none, which is what the existing age tests use.
