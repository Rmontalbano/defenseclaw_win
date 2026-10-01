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

**For:** the one "All / one connector" filter every screen shares (the Mac's `connectorFilter`). No UI yet; the chip is a later issue.

```csharp
var rows = allRows.Where(r => Services.ConnectorScope.Allows(r.Connector));          // the predicate; null scope allows everything
Services.ConnectorScope.Cycle();   // All -> first -> second -> ... -> All     (Set("codex") for a menu pick; Connectors is the roster)
Services.ConnectorScope.Changed += (_, _) => Refilter();                              // scope or roster changed
```

- Roster = `GatewaySnapshot.ActiveConnectors` (configured first, then live). The scope **resets to All by itself** when its connector leaves the roster or the roster shrinks to one or none; `Set` refuses a connector not on the roster; `CanScope` (roster > 1) is what shows the chip.
- An explicit scope is an exact match (ignoring case/padding) and **hides rows with no connector**, like every other Mac screen.
