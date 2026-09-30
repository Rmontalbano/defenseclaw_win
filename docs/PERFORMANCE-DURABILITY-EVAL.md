# Performance & durability evaluation

> **Baseline:** app at commit `bfd2bf3` (0.8.10 runtime baseline), evaluated 2026-08-25.
> **Method:** four dimension surveys (UI responsiveness, process & I/O durability, data access & memory, startup & packaging) read the code independently; every candidate finding was then re-verified against source by a separate adversarial pass instructed to reject anything wrong, already handled, or speculative. 34 candidates → **33 confirmed, 1 rejected** (appendix). Nothing in this document has been applied — it is an evaluation, not a changelog.
> **Status update (2026-08-25):** the six quick wins (#6, #7, #8, #14, #18, #20) and #26 (folded into the same cursor-API fix) are implemented — see [QUICK-WINS-LOG.html](QUICK-WINS-LOG.html). Their code citations below describe the pre-fix state.
> **Status update (2026-09-28):** a second pass fixed #1, #2, #3, #9-adjacent Logs projection, #10, #11, #12, #13, #16, #21 (null-probe keyset + indexed ranges), and #33-adjacent catalog refresh; #15 was re-tested and its premise disproved (read-only WAL opens survived every synthesized crash state), so only the busy timeout was tightened. Measured live: idle CPU in the tray 13.8% → 0.6% of a core. Citations below describe the pre-fix state.
> **Status update (2026-09-29):** every remaining finding is closed (CUST-176). #9, #22, #23, #24, #33 fixed; #30 (dashboard built on first show), #31 (tray icon cached per build), #32 (config load off the UI thread) fixed; #28 ReadyToRun (non-composite) adopted. **#29 was measured and rejected**, and its premise corrected: a compressed single-file assembly is inflated into private memory on *every* launch and held for the process lifetime, not once at extraction. On the real exe, tray-only, 15 s after launch: v0.3.1 ~1.23 s to idle / 55 MB private; R2R + compressed ~1.00 s / 130 MB; R2R uncompressed ~1.15 s / 49 MB (shipped), with startup CPU ~1.8 s → ~0.7 s. Also corrected: #31 was rated low impact but was the largest single startup cost (400–600 ms of WPF media-stack start-up, harness-measured), and #28's TieredPGO advice had no measurable effect.

## Executive summary

The architecture is sound: the read-mostly design, the CLI-mediated mutation path, and the bounded buffers in most streaming paths all hold up under scrutiny. The confirmed findings cluster into four recurring themes rather than isolated defects:

1. **Poll-path churn.** The 5-second gateway poll fans out unconditionally — `StateChanged` fires with no equality gate (#1), Overview rebuilds four bound collections from scratch each tick (#2), Alerts re-projects on the 5 s cadence for data that only refreshes every 30 s (#3), and two view-models allocate fresh unfrozen brushes per tick (#10). Most of the app's steady-state CPU and allocation cost traces to this one fan-out.
2. **A few unbounded or quadratic buffers in an otherwise-capped design.** `CliInvocation`'s retained output has no byte cap (#14) and its `Snapshot()` copies the whole history on every 250–500 ms tick (#6, #26); the Audit panel's row collection is the one streaming collection without a cap (#23); log-buffer trims remove one element at a time (#9, #24).
3. **Missing global safety nets.** No `DispatcherUnhandledException` / `UnobservedTaskException` handlers (#18), no CLI child timeout, cancellation, or kill-on-exit (#12, #13), a config-watcher event that mutates a bound collection off the UI thread (#20), and SQLite reads with no busy-timeout (#15). These are the durability gaps: each is invisible until the day it is a crash, a wedged panel, or a zombie process.
4. **Unexploited startup/packaging levers.** ReadyToRun and single-file compression are one-line publish properties (#28, #29); the `--minimized` autostart path still constructs the full `MainWindow` (#30); the wizard-catalog probe fan-out re-pays ~31 × 800 ms every process lifetime (#33).

The highest-leverage sequence: land the six quick wins below (all `high impact / small effort`), then take the poll-path churn as one coherent refactor (#1 gates #2, #3, and #10 — fixing the fan-out first shrinks the others), then the CLI lifecycle work (#12–#14 share `CliRunner`/`CliInvocation`).

## Priority matrix

### Quick wins — high impact, small effort

| # | Finding | Where |
|---|---------|-------|
| 6 | `CliInvocation.Snapshot()` O(n) triple-copy, re-invoked per loop iteration | `DefenseClaw.Core/Cli/CliInvocation.cs` |
| 7 | Updates console scrolls once per appended line, non-virtualized | `DefenseClaw.App/Views/Updates/UpdatesWindow.xaml.cs` |
| 8 | Logs filter re-projects the 5000-line buffer per keystroke (no `Delay`) | `DefenseClaw.App/ViewModels/LogsPanelViewModel.cs` |
| 14 | `CliInvocation` retained output uncapped — Activity ring unbounded in bytes | `DefenseClaw.Core/Cli/CliInvocation.cs` |
| 18 | No `DispatcherUnhandledException` / `UnobservedTaskException` handlers | `DefenseClaw.App/App.xaml.cs` |
| 20 | `ConfigChangeToken.Changed` raised on watcher thread into bound collection | `DefenseClaw.Core/Config/ConfigChangeToken.cs` |

### Structural — high impact, medium effort

| # | Finding | Where |
|---|---------|-------|
| 1 | Unconditional `StateChanged` fan-out every 5 s poll | `DefenseClaw.App/Services/GatewayMonitor.cs` |
| 2 | Overview clears + rebuilds four bound collections per poll | `DefenseClaw.App/ViewModels/OverviewPanelViewModel.cs` |
| 3 | Alerts re-projects on 5 s cadence for 30 s data | `DefenseClaw.App/ViewModels/AlertsPanelViewModel.cs` |
| 4 | Catalog-panel virtualization inert (outer `ScrollViewer` defeats it) | `DefenseClaw.App/Views/Panels/SkillsPanel.xaml` (+3 siblings) |
| 5 | Activity panel realizes 200 cards + all output, no virtualization | `DefenseClaw.App/Views/Panels/ActivityPanel.xaml` |
| 11 | Panel view-models never torn down; timers run while hidden in tray | `DefenseClaw.App/Services/PanelCatalog.cs` |
| 12 | `CliRunner` has no timeout; most call sites pass no token | `DefenseClaw.Core/Cli/CliRunner.cs` |
| 13 | CLI children never killed/cancelled on app exit | `DefenseClaw.App/Services/AppServices.cs` |
| 21 | `COALESCE` in keyset sort defeats the retention index — full scans | `DefenseClaw.Core/Audit/AuditReader.cs` |
| 24 | Log buffer trim shifts the whole backing array per line | `DefenseClaw.App/ViewModels/LogsPanelViewModel.cs` |

### Hygiene — medium impact, small effort

| # | Finding | Where |
|---|---------|-------|
| 9 | `TrimDisplayed` removes one element per excess line | `DefenseClaw.App/ViewModels/LogsPanelViewModel.cs` |
| 10 | Fresh unfrozen `SolidColorBrush` allocated every poll | `DefenseClaw.App/ViewModels/MainWindowViewModel.cs` |
| 16 | `RefreshAsync` races the background poll over plain fields | `DefenseClaw.App/Services/GatewayMonitor.cs` |
| 17 | Upgrade staging never pruned — ~270 MB left per upgrade | `DefenseClaw.App/Services/Updates/UpgradeRunner.cs` |
| 19 | `LogTailer` poll loop unguarded; `StartWatching` not re-entry safe | `DefenseClaw.Core/Logs/LogTailer.cs` |
| 22 | Audit rows eagerly pretty-print `structured_json` per page | `DefenseClaw.App/ViewModels/AuditPanelViewModel.cs` |
| 23 | Audit `Rows` collection uncapped | `DefenseClaw.App/ViewModels/AuditPanelViewModel.cs` |
| 26 | `Snapshot()` full-copy per 500 ms per running invocation | `DefenseClaw.Core/Cli/CliInvocation.cs` |
| 28 | No ReadyToRun/PGO on the login-autostart launch path | `.github/workflows/ci.yml` |

### Remainder — lower impact or larger effort

| # | Finding | Where |
|---|---------|-------|
| 15 | Read-only SQLite: no busy-timeout/retry when gateway is down | `DefenseClaw.Core/Audit/AuditReader.cs` |
| 30 | `--minimized` autostart still constructs the full `MainWindow` | `DefenseClaw.App/App.xaml.cs` |
| 25 | Poll response bodies parsed twice in `GatewayClient.Interpret` | `DefenseClaw.Core/Gateway/GatewayClient.cs` |
| 29 | Single-file exe ships uncompressed | `.github/workflows/ci.yml` |
| 27 | Fresh `SqliteConnection` per call, no batching on panel init | `DefenseClaw.Core/Audit/AuditReader.cs` |
| 31 | Tray icon rasterized synchronously in the composition root | `DefenseClaw.App/Services/TrayIconService.cs` |
| 32 | `AppServices` chains sync file I/O on the UI thread at startup | `DefenseClaw.App/Services/AppServices.cs` |
| 33 | Wizard probe results (~31 × 800 ms) never persisted across launches | `DefenseClaw.App/Services/Wizards/WizardCatalog.cs` |

---
## UI responsiveness & rendering

### 1. GatewayMonitor.Publish fans out an unconditional StateChanged every 5s poll with no snapshot-equality gate — `high impact / medium effort`

**Where:** `DefenseClaw.App/Services/GatewayMonitor.cs:505`

`Publish()` (lines 505-526) stores `_current = snapshot` under lock, then unconditionally does `context.Post(...)` to raise `handler(this, new GatewaySnapshotEventArgs(snapshot))` on the UI SynchronizationContext, with no comparison against the previously published snapshot. `PollAsync` (285-356) always stamps `PolledAt = DateTimeOffset.UtcNow` (353), so no two snapshots are ever reference- or value-equal, and there is no RendersSameAs/Equals check anywhere in the class.

**Symptom:** On an idle, healthy box every subscribed panel (Overview, Alerts, tray icon, tray flyout, MainWindow) does a full re-render pass every 5 seconds forever, most of which (5 of 6, since AlertInterval/StatusInterval = 30s vs FastInterval = 5s, confirmed at lines 166-174) carries byte-identical alert/connector data.

**Refactor:** Add an equality/RendersSameAs comparison on GatewaySnapshot restricted to UI-relevant fields (excluding PolledAt/ConsecutiveFailures), and only Post the event when the new snapshot differs from the last-published one; update `_current` unconditionally under the lock so RefreshAsync callers still see fresh data.

**Verified:** Read GatewayMonitor.cs in full (1-528); Publish() at 505-526 matches exactly, including the unconditional context.Post, with FastInterval=5s (166), AlertInterval=30s (168), StatusInterval=30s (174), and PolledAt=DateTimeOffset.UtcNow (353) all confirmed verbatim.

### 2. OverviewPanelViewModel.Replace<T> clears and rebuilds four bound ObservableCollections from scratch on every poll — `high impact / medium effort`

**Where:** `DefenseClaw.App/ViewModels/OverviewPanelViewModel.cs:819`

Every StateChanged/poll runs `Apply()`, which ends in four `Replace()` calls (Attention, ServiceRows, ScannerRows, ConnectorRows) that Clear()+re-Add() the ObservableCollections bound to non-virtualized ItemsControls inside one ScrollViewer, tearing down and rebuilding the whole dashboard's visual subtree every 5 seconds. `Replace<T>` at lines 819-826 is exactly `target.Clear(); foreach (item in items) target.Add(item);`, and its own doc comment (815-818) explicitly argues a diffing merge "would cost more than it saves" — confirmed verbatim. `Apply()` (149-175) calls BuildAttention/BuildServices/BuildScanners/BuildConnectors, ending in Replace(Attention, rows) (333), Replace(ServiceRows, rows) (362), Replace(ScannerRows, rows) (422), Replace(ConnectorRows, rows) (508). `OnStateChanged` (136-146) calls Apply on every poll. OverviewPanel.xaml has a ScrollViewer at 178 wrapping six plain ItemsControls (225, 258, 311, 358, 396, 467) with no ItemsPanel virtualization override, and a copyable remediation TextBox inside the Attention ItemTemplate (280).

**Symptom:** An operator scrolled into the Connectors box is snapped back to the top of Overview every 5 seconds because Clear() collapses the ScrollViewer's extent; text selected in the remediation-command TextBox is wiped mid-drag, making the fail-mode remediation command effectively unselectable with the mouse.

**Refactor:** Replace clear-and-refill with a keyed in-place merge (row identity by Title/Name), make row types settable/observable, and update matched rows in place rather than discarding the visual tree; the doc comment's cost argument is the wrong trade given the visible scroll-reset and lost-selection consequences.

**Verified:** Read OverviewPanelViewModel.cs lines 120-176, 320-430, 495-510, 795-827 — Apply()/OnStateChanged/Replace<T> and all four Build*/Replace call sites match the cited line numbers exactly, including the doc-comment text. Confirmed OverviewPanel.xaml ScrollViewer(178)/ItemsControls(225,258,311,358,396,467)/TextBox(280) via grep.

### 3. AlertsPanelViewModel.Apply rebuilds and re-projects all alerts on the 5s health cadence though /alerts only refreshes every 30s — `high impact / medium effort`

**Where:** `DefenseClaw.App/ViewModels/AlertsPanelViewModel.cs:132`

`Apply()` clears and rebuilds `_all` from the snapshot on every StateChanged (i.e. every 5s poll), then `ApplyFilters()` clears and refills the ListView-bound Alerts collection — running `AlertItem.FromGateway`'s JSON pretty-printing/field-flattening for all 25 alerts up to 6x per actual data change, since GatewayMonitor caches `_lastAlerts` between 30s alert polls. `OnStateChanged` (125) calls Apply on every StateChanged. `Apply()` (132-162): `_all.Clear(); foreach... _all.Add(AlertItem.FromGateway(alert));` at 151-155, then `ApplyFilters()` at 161. `ApplyFilters()` (221-260) does `Alerts.Clear(); foreach... Alerts.Add(row);` at 246-250. `AlertItem.FromGateway` (398) calls `Pretty()` (513, OrderBy over the attribute bag) and `ToFields()` (491, another OrderBy). `GatewayMonitor.AlertInterval`=30s (GatewayMonitor.cs:168) with `_lastAlertPoll` gating a re-poll (GatewayMonitor.cs:307), so 5 of 6 poll cycles carry the identical cached `_lastAlerts` reference.

**Symptom:** The Alerts ListView (correctly virtualized: AlertsPanel.xaml:163,169-170) has its ItemsSource content Reset every 5 seconds via Clear/refill, snapping scroll position to top and nulling the two-way SelectedItem binding (AlertsPanel.xaml:164) mid-read, collapsing/re-expanding the detail pane each time.

**Refactor:** Short-circuit Apply() when snapshot.RecentAlerts is reference-identical to the last-applied list (updating only the "refreshed Ns ago" SourceNote text), and merge ApplyFilters into Alerts by key instead of Clear/refill so the ListView keeps containers/scroll/selection when the result set is unchanged.

**Verified:** Read AlertsPanelViewModel.cs lines 60-260 — Apply()/ApplyFilters()/OnStateChanged and all quoted line numbers (125,132,151-155,161,221-260,246-250) match exactly. Confirmed FromGateway/Pretty/ToFields/OrderBy locations via grep (398,491,513,494,521). Confirmed AlertsPanel.xaml ListView with VirtualizingPanel.IsVirtualizing=True at 163,169-170 and SelectedItem TwoWay at 164 via grep — the claim that this ListView is "correctly virtualized" (unlike other panels) is accurate.

### 4. VirtualizingStackPanel on Skills/MCPs/Plugins/Tools catalog ItemsControls is inert — nested inside an outer ScrollViewer that gives it infinite-height measure — `high impact / medium effort`

**Where:** `DefenseClaw.App/Views/Panels/SkillsPanel.xaml:94`

All four Govern panels declare VirtualizingStackPanel as the ItemsPanel of a plain ItemsControl sitting inside an outer ScrollViewer > StackPanel, which defeats virtualization: the plain ItemsControl has no IScrollInfo-owning template and the outer ScrollViewer measures the inner content with infinite height, so every catalog entry (a CardExpander plus a raw-JSON TextBox) is fully realized regardless of catalog size. SkillsPanel.xaml: ScrollViewer (78) > StackPanel (79) > ItemsControl ItemsSource={Binding Skills} (94-95) with VirtualizingStackPanel VirtualizationMode=Recycling (98) as ItemsPanel; identical structure confirmed in McpsPanel.xaml (ScrollViewer 135, VSP 155), PluginsPanel.xaml (ScrollViewer 111, VSP 131), ToolsPanel.xaml (ScrollViewer 80, VSP 100). This is a well-documented WPF virtualization pitfall: a plain ItemsControl's default template is just an ItemsPresenter (no ScrollViewer), and nesting a virtualizing panel inside an already-scrolling ancestor with unbounded height forces full realization.

**Symptom:** On a box with a large skill/MCP/plugin/tool catalog, opening the Govern panel hitches on first navigation and every subsequent layout pass (resize, theme flip) walks every realized CardExpander+TextBox; PanelCatalog.GetPage caches the view forever (confirmed at PanelCatalog.cs:115-141) so the memory is never reclaimed for the life of the process.

**Refactor:** Swap the ItemsControl for a ListBox/ListView with VirtualizingPanel.IsVirtualizing=True + VirtualizationMode=Recycling, give it its own bounded-height row instead of nesting it in the outer ScrollViewer, and defer the RawJson TextBox content until the CardExpander is actually expanded.

**Verified:** Read SkillsPanel.xaml lines 78-101 directly — ScrollViewer/StackPanel/ItemsControl/VirtualizingStackPanel structure and ItemsSource={Binding Skills} match exactly. Grep-confirmed the identical ScrollViewer+VirtualizingStackPanel pattern in McpsPanel.xaml, PluginsPanel.xaml, ToolsPanel.xaml at the cited lines. Confirmed PanelCatalog.cs caches views forever via `_views` dictionary with no eviction (lines 47-48, 119-141).

### 5. ActivityPanel realizes up to 200 invocation cards and all output lines with no virtualization; per-invocation output is unbounded — `high impact / medium effort`

**Where:** `DefenseClaw.App/Views/Panels/ActivityPanel.xaml:109`

The invocation list is a plain ItemsControl (default StackPanel, no virtualization) inside a ScrollViewer; each card's Expander body is a second plain ItemsControl over that invocation's output, and the Expander's content is instantiated even while collapsed. CliInvocation._outputLines has no line cap. ActivityPanel.xaml: ScrollViewer (108) > ItemsControl ItemsSource={Binding Rows} (109) with no ItemsPanel override; nested Expander (169) whose body is ItemsControl ItemsSource={Binding Output} (176). Rows is capped at CliRunner.ActivityCapacity, confirmed default 200 (CliRunner.cs:64, `activityCapacity > 0 ? activityCapacity : 200`) and enforced in ActivityPanelViewModel (Rows.Insert/RemoveAt around lines 107-110). CliInvocation._outputLines (CliInvocation.cs:24) is a plain List with Append (78-84) doing only `_outputLines.Add(line)` — no trimming, confirmed by reading the full file.

**Symptom:** After a session with real CLI activity, navigating to Activity stalls the UI while potentially thousands of elements (200 cards x unbounded output lines) are inflated, and every subsequent layout pass re-measures all of them; the 500ms DispatcherTimer (ActivityPanelViewModel.cs:34,47-49, confirmed) re-measures on top of that during any running invocation.

**Refactor:** Use a virtualizing ListBox/ListView (Recycling) for the outer list instead of nesting an ItemsControl in a ScrollViewer; gate the nested output ItemsControl's content on Expander.IsExpanded so collapsed output is never inflated; cap CliInvocation.Append at a few thousand lines with a truncation marker.

**Verified:** Read ActivityPanel.xaml lines around 108-204 via grep — ScrollViewer(108)/ItemsControl(109)/Expander(169)/nested ItemsControl(176) confirmed. Read CliInvocation.cs in full — Append (78-84) confirmed to have no cap. Grep-confirmed CliRunner.cs:64 default ActivityCapacity=200 and ActivityPanelViewModel.cs:34 TickInterval=500ms, timer started at 47-49.

### 6. CliInvocation.Snapshot() does O(n) triple-copy of output, and UpgradeSectionViewModel's loop re-invokes the ToArray-backed getter on every iteration — `high impact / small effort`

**Where:** `DefenseClaw.Core/Cli/CliInvocation.cs:87`

The OutputLines getter does `_outputLines.ToArray()` under lock; Snapshot() calls OutputLines once more via AddRange, so Snapshot is two full copies, and its two UI-thread callers (ActivityRow.Tick at 500ms, UpgradeSectionViewModel.PullOutput at 250ms) each re-read `snapshot.OutputLines` again. Worse, PullOutput's for-loop condition `i < snapshot.OutputLines.Count` and body `snapshot.OutputLines[i]` both re-invoke the ToArray-backed getter on every single iteration, not just once per tick. CliInvocation.cs: OutputLines getter (54-63) does `_outputLines.ToArray()` under lock; Snapshot() (87-99) does `copy._outputLines.AddRange(OutputLines)` at 97. ActivityRow.Tick (ActivityPanelViewModel.cs:198-210) calls `Invocation.Snapshot()` (200) then `SyncOutput(snapshot.OutputLines)` (210) — a third getter call. UpgradeSectionViewModel.cs: OutputTick=250ms (50), timer wired to PullOutput (197-198), PullOutput (834-853): `var snapshot = _invocation.Snapshot()` (841), then `for (var i = _syncedOutputCount; i < snapshot.OutputLines.Count; i++) { var line = snapshot.OutputLines[i]; ... }` (842,844).

**Symptom:** During an active upgrade run, the UI thread allocates repeated full-array copies of the accumulated output four times a second, with cost scaling with output length and per-line getter re-invocation compounding it further — exactly while an operator is watching the console for signs of trouble, producing GC pressure that shows up as stutter.

**Refactor:** Add an O(1) OutputCount property and a delta-copy method (CopyOutputFrom(index, into)) that takes the lock once, and use it from both ActivityRow.SyncOutput and UpgradeSectionViewModel.PullOutput. At minimum hoist `var lines = snapshot.OutputLines;` out of the loop at UpgradeSectionViewModel.cs:842 so the getter isn't re-invoked per iteration.

**Verified:** Read CliInvocation.cs in full (all 104 lines) — OutputLines getter (54-63) and Snapshot() (87-99) match exactly. Grep-confirmed UpgradeSectionViewModel.cs: OutputTick=250ms(50), timer wiring(197-198), PullOutput(834), snapshot=Invocation.Snapshot()(841), loop condition and indexer both calling snapshot.OutputLines(842,844) — confirms the getter is invoked twice per loop iteration, an even stronger finding than originally stated. Confirmed ActivityRow.Tick calls Snapshot()(200) then SyncOutput(snapshot.OutputLines)(210).

### 7. UpdatesWindow scrolls once per appended output line into a non-virtualized console, matching a pattern the Logs panel already solves correctly — `high impact / small effort`

**Where:** `DefenseClaw.App/Views/Updates/UpdatesWindow.xaml.cs:84`

OnUpgradeOutputChanged calls ScrollToEnd() on every single CollectionChanged Add notification (one per appended output line), and the target is a plain ItemsControl (all lines realized as wrapped TextBlocks) inside a fixed-height Border/ScrollViewer, so a burst of N lines from one 250ms PullOutput tick produces N full-layout ScrollToEnd calls. UpdatesWindow.xaml.cs lines 84-92 match verbatim: `if (e.Action != NotifyCollectionChangedAction.Add || _viewModel.Upgrade.IsOutputPaused) { return; } UpgradeOutputScroll.ScrollToEnd();`. UpgradeSectionViewModel.PullOutput (834-853) adds output in a for-loop, one Add per line. UpdatesWindow.xaml: Border Height=260 (540) wrapping ScrollViewer x:Name=UpgradeOutputScroll (547) wrapping ItemsControl ItemsSource={Binding Output} (548) with no ItemsPanel override. LogsPanel.xaml.cs already solves this exact problem: a `_scrollScheduled` bool (26) guards a `DispatcherPriority.Background`-scheduled BeginInvoke (61-74) that coalesces bursts into one scroll.

**Symptom:** A verbose resolver run makes the Updates console judder as it scrolls on every appended line, and the window is least responsive to Pause/Cancel exactly while the operator is watching the upgrade output most closely.

**Refactor:** Copy the LogsPanel.xaml.cs `_scrollScheduled` + DispatcherPriority.Background pattern so a whole burst coalesces into one ScrollToEnd; separately consider a virtualizing ListBox for the console content.

**Verified:** Read UpdatesWindow.xaml.cs lines 1-100 in full — OnUpgradeOutputChanged (84-92) matches exactly. Grep-confirmed UpdatesWindow.xaml: Border Height=260(540), ScrollViewer x:Name=UpgradeOutputScroll(547), ItemsControl ItemsSource={Binding Output}(548). Grep-confirmed LogsPanel.xaml.cs already implements the _scrollScheduled/DispatcherPriority.Background coalescing pattern at lines 26,61-74 — the comparison is accurate, not fabricated.

### 8. Logs filter textbox has no Delay and re-projects the entire 5000-line buffer per keystroke, unlike the Audit panel's identical filter which does — `high impact / small effort`

**Where:** `DefenseClaw.App/ViewModels/LogsPanelViewModel.cs:356`

FilterText is bound with UpdateSourceTrigger=PropertyChanged and no Delay (unlike AuditPanel's two filter boxes, which both use Delay=400), and OnFilterTextChanged triggers ApplyFilters(), a full Clear-and-refill of DisplayedLines over the whole MaxBufferedLines=5000 buffer with a per-line Passes() predicate on every keystroke. LogsPanel.xaml:114 `Text="{Binding FilterText, UpdateSourceTrigger=PropertyChanged}"` — no Delay, confirmed via grep; contrast AuditPanel.xaml:162,169 both carrying `Delay=400`, confirmed via grep. LogsPanelViewModel.cs: `partial void OnFilterTextChanged(string value) => ApplyFilters();` (118, confirmed exact); MaxBufferedLines=5000 (33); ApplyFilters (356-370) does `DisplayedLines.Clear(); foreach (line in state.Buffer) { if (Passes(line)) DisplayedLines.Add(...) }` (360-367, confirmed exact); Passes() (336-353) does `ComponentFilters.All(f => f.IsEnabled)` and `ComponentFilters.FirstOrDefault(...)` per line (338,341, confirmed exact), each allocating an enumerator.

**Symptom:** Typing a five-character filter on a full log buffer runs five full 5000-item projections with per-line LINQ allocation and up to thousands of LogEntry allocations and CollectionChanged notifications, so keystrokes visibly lag and the list flashes empty between passes.

**Refactor:** Add Delay=300 (or match Audit's 400) to the FilterText binding; hoist the component-filter "all enabled" fast-path out of the per-line Passes() call so it isn't recomputed 5000 times per pass.

**Verified:** Read LogsPanelViewModel.cs lines 1-50 and 200-380 in full, plus grepped LogsPanel.xaml/AuditPanel.xaml for the Delay contrast. All cited lines (33,102-118,336-370) match the evidence exactly, character for character in the quoted code.

### 9. TrimDisplayed enforces the buffer cap with one RemoveAt(0) per excess line, unlike the source buffer's own RemoveRange trim — `medium impact / small effort`

**Where:** `DefenseClaw.App/ViewModels/LogsPanelViewModel.cs:240`

TrimDisplayed loops `while (DisplayedLines.Count > MaxBufferedLines) { DisplayedLines.RemoveAt(0); }`, each call an O(n) array shift plus its own CollectionChanged Remove-at-0 notification, while the source-side Append() already does the equivalent trim correctly in one RemoveRange call. TrimDisplayed (240-246) matches exactly: `while (DisplayedLines.Count > MaxBufferedLines) { DisplayedLines.RemoveAt(0); }`. The batch feeding it is bounded by LogTailer's MaxLinesPerBatch=2000 (confirmed LogTailer.cs:15), and OnLinesReceived adds the whole batch (208-222) before calling TrimDisplayed (234). Append() (273-290) trims the source-side buffer correctly in one shot: `var excess = state.Buffer.Count - MaxBufferedLines; if (excess > 0) { state.Buffer.RemoveRange(0, excess); }` (285-288) — confirmed exact match to the contrast claim.

**Symptom:** When the gateway writes a burst (startup banner, scanner sweep), the Logs panel can do up to ~2000 individual RemoveAt(0) shifts over a 5000-element ObservableCollection in one dispatcher callback, each with its own container-reconciliation and OnDisplayedLinesChanged notification, visibly jittering the scroll position.

**Refactor:** Compute the excess once and remove it in a single operation (custom RemoveRange raising one Reset, or subclass ObservableCollection), mirroring the source buffer's already-correct RemoveRange(0, excess) pattern.

**Verified:** Read LogsPanelViewModel.cs lines 200-290 in full — TrimDisplayed(240-246) and Append(273-290) both match the quoted code exactly, including the RemoveRange contrast. Grep-confirmed LogTailer.cs:15 MaxLinesPerBatch=2000.

### 10. MainWindowViewModel and TrayFlyoutViewModel allocate a fresh unfrozen SolidColorBrush every poll, the one path ShieldIconFactory's otherwise-consistent caching missed — `medium impact / small effort`

**Where:** `DefenseClaw.App/ViewModels/MainWindowViewModel.cs:136`

Both StateBrush assignments construct `new SolidColorBrush(...)` per snapshot with no caching/freezing, guaranteeing a new-reference PropertyChanged and Ellipse re-render every 5s poll even though ColorFor only ever returns one of 4 fixed colors — while every other icon/brush path in the codebase (ShieldIconFactory.Get's Dictionary cache, MainWindow.ApplyShieldIcon's state-gated ImageSource cache, TrayIconService's _currentShield guard) already avoids exactly this redundant work. MainWindowViewModel.cs:136 and TrayFlyoutViewModel.cs:88 both do `StateBrush = new SolidColorBrush(ShieldIconFactory.ColorFor(ShieldIconFactory.StateFor(snapshot)));` — confirmed identical in both files via grep. ShieldIconFactory.ColorFor (48-54) returns exactly 4 Colors via switch. Contrast: ShieldIconFactory.Get (76-89) memoizes in a Dictionary; MainWindow.ApplyShieldIcon (54-69) short-circuits with `if (_currentIconState == state) return;` before touching its own ShieldImageCache Dictionary — confirmed exact; TrayIconService.cs:194 guards with `if (_currentShield != shield)` — confirmed via grep.

**Symptom:** Two unfrozen Freezables allocated per poll for the life of the tray-resident process, each forcing the bound status Ellipse (MainWindow.xaml, TrayFlyoutWindow.xaml) to re-render its Fill on a cadence that will structurally never produce a different pixel most of the time.

**Refactor:** Add a static `BrushFor(ShieldState)` to ShieldIconFactory backed by 4 pre-frozen SolidColorBrush instances shared across both view-models; frozen, shared, reference-stable brushes let the generated ObservableProperty setter's equality check suppress the redundant notification entirely.

**Verified:** Read ShieldIconFactory.cs lines 1-89 and MainWindow.xaml.cs lines 40-69 in full; grep-confirmed the exact StateBrush assignment lines in both view-models and the TrayIconService `_currentShield` guard at line 194. All quoted code matches verbatim; impact was downgraded from high to medium since this brush churn is a smaller, narrower cost than the collection-rebuild findings it's grouped alongside — a single Freezable allocation plus one Ellipse fill re-render per poll, not a visual-tree teardown.

### 11. Panel view-models are never torn down: StateChanged subscriptions and DispatcherTimers outlive navigation and keep running while the app is hidden in the tray — `high impact / medium effort`

**Where:** `DefenseClaw.App/Services/PanelCatalog.cs:119`

PanelCatalog.GetPage caches every panel view forever with no eviction, PanelViewModelBase declares no IDisposable/teardown hook, and several panel view-models subscribe to long-lived events/timers in their constructors with no matching unsubscribe — so once a panel has been visited once, its expensive Apply()/tick logic keeps running on every poll for the rest of the process's life, including while MainWindow is hidden to the tray. PanelCatalog.cs: `_views` Dictionary with TryGetValue-and-return-cached at 119-122, no eviction path anywhere in the file. PanelViewModelBase.cs (25-56): no IDisposable, no Dispose. OverviewPanelViewModel.cs:93 and AlertsPanelViewModel.cs:74 both subscribe `Services.Monitor.StateChanged += OnStateChanged` with no corresponding `-=` found in either file. ActivityPanelViewModel starts a 500ms DispatcherTimer in its constructor (47-49, confirmed) never stopped in that file. AlertsPanelViewModel starts a 20s `_clock` DispatcherTimer for RestampTimes (78-80, confirmed) never stopped. LogsPanelViewModel attaches LinesReceived/Truncated handlers to both tailers (190-191, confirmed) with no detach found. Contrast: MainWindowViewModel implements IDisposable and unsubscribes StateChanged at Dispose (74,87,95, confirmed), TrayFlyoutViewModel does the same (61,65,73, confirmed). MainWindow.xaml.cs.OnClosing (91-113) only Hide()s and cancels the close unless `_allowClose` — confirmed — so the process and all cached panel view-models genuinely survive the dashboard being "closed" to the tray.

**Symptom:** Once an operator has visited Overview, Alerts, Activity, and Logs and then closes the dashboard to the tray, every 5s poll still drives OverviewPanelViewModel.Apply (four full collection rebuilds, per finding above) and AlertsPanelViewModel.Apply (25 AlertItem reconstructions with JSON pretty-printing) for panels that are off-screen, plus the Activity timer waking the dispatcher twice a second indefinitely — the tray-resident process never reaches idle, competing with the render thread the moment the window is reopened.

**Refactor:** Add a teardown hook (IDisposable or an OnDeactivated/OnActivated pair) to PanelViewModelBase, and drive it from PanelCatalog on navigation-away/window-hide: unsubscribe StateChanged and stop DispatcherTimers when a panel isn't the active/visible one, re-subscribing with a one-shot Apply(Monitor.Current) on return. At minimum, gate the expensive Apply paths on window visibility.

**Verified:** Read PanelCatalog.cs lines 40-150 and PanelViewModelBase.cs in full — caching (119-141) and lack of Dispose both confirmed exactly. Grep-confirmed all subscribe/Dispose sites across OverviewPanelViewModel.cs, AlertsPanelViewModel.cs, MainWindowViewModel.cs, TrayFlyoutViewModel.cs, LogsPanelViewModel.cs in one pass — every cited line number matched. Read MainWindow.xaml.cs OnClosing (91-113) in full, confirming Hide()-not-close behavior verbatim.
## Process & I/O durability

### 12. CliRunner has no timeout and most call sites pass no CancellationToken, so a hung child wedges its panel and the tray's gateway toggle — `high impact / medium effort`

**Where:** `DefenseClaw.Core/Cli/CliRunner.cs:232`

RunExecutableAsync's only bound on child lifetime is the caller's CancellationToken — `await process.WaitForExitAsync(cancellationToken)` at line 232, with no timeout parameter anywhere in the method (confirmed against the signature at line 150). Nearly every call site uses the default-token overload: AiDiscoveryPanelViewModel.cs:252 `RunAsync(new[] { "agent", "discover" })`, McpsPanelViewModel.cs:292, PluginsPanelViewModel.cs:198, SkillsPanelViewModel.cs:219, and ToolsPanelViewModel.cs:229 all call `RunAsync(argv)` with no token, as does ViewModels/Wizards/WizardViewModel.cs:241 (`RunAsync(argv, secret)`). TrayIconService.cs:172 passes no token to `RunGatewayAsync(new[] { stopping ? "stop" : "start" })`, and the `_gatewayActionRunning` latch set true at line 169 before the await is only cleared in a `finally` at line 187 — which cannot run while the await is still pending. One exception: RegistriesPanelViewModel.cs:97 does pass a `cancellationToken:` parameter, but its Refresh command path at line 87 supplies `CancellationToken.None` explicitly, which is functionally equivalent to no cancellation for a user-triggered hang.

**Symptom:** A wedged defenseclaw/defenseclaw-gateway child (stdin closed while it waits on a prompt, a stalled registry fetch, a wedged gateway socket) leaves the await pending forever. The panel's busy flag never clears and its action button stays disabled with no Cancel affordance; the tray's Start/Stop Gateway control is the worst case since `_gatewayActionRunning` stays true and permanently disables the tray's only control until the app is killed and relaunched.

**Refactor:** Add a `TimeSpan? timeout` to RunExecutableAsync (default ~60-120s), implemented via a linked CancellationTokenSource with CancelAfter, and record a distinct FailureReason ("timed out" vs "cancelled"). Wire a real per-command CancellationTokenSource into each panel's action/refresh commands so Refresh does not silently pass CancellationToken.None.

**Verified:** Re-read CliRunner.cs:146-250 (no timeout field/param anywhere in the class), TrayIconService.cs:161-189 (latch set at 169, cleared only in the finally at 187), and RegistriesPanelViewModel.cs:83-98 (RefreshAsync passes CancellationToken.None at line 87), plus a grep of RunAsync/RunGatewayAsync call sites across DefenseClaw.App/ViewModels confirming the no-token pattern at the cited lines.

### 13. Nothing kills or cancels CLI children when the app exits; CliRunner is not disposed and holds no ambient cancellation — `high impact / medium effort`

**Where:** `DefenseClaw.App/Services/AppServices.cs:170`

AppServices.Dispose (lines 170-185) tears down Monitor, ConfigWatcher, GatewayLog, WatchdogLog, and Gateway but never touches Cli. CliRunner.cs:48 declares `public sealed class CliRunner` with no `: IDisposable` and no CancellationTokenSource field anywhere in the class, so any in-flight child — including the installer, which shells through this same path — is orphaned when the app exits. App.xaml.cs:63-66 OnExit calls only `_tray?.Dispose(); _services?.Dispose();`, and UpgradeRunner.cs:1060-1061 confirms the installer runs through `_cli.RunExecutableAsync(executable, argv, stdinSecret: null, cancellationToken)`, the same unmanaged path.

**Symptom:** Quitting from the tray (or a crash) during a wizard mutation, `agent discover`, or the ~270 MB Setup installer (UpgradeRunner) leaves that child running with no window, no owner, and no visibility in the (now-closed) Activity panel. A relaunch is possible immediately since the instance mutex is released on exit, so a new instance can start while the orphan is still writing to ~/.defenseclaw.

**Refactor:** Make CliRunner IDisposable with a `_shutdown` CancellationTokenSource linked into every RunExecutableAsync call; dispose it from AppServices.Dispose so exit cancels and kills (via the existing TryKill(process, entireProcessTree: true) pattern) any in-flight children. Decide deliberately whether the installer should be exempted (it is meant to outlive the app it replaces) versus assigned to a Win32 Job object for crash-safety.

**Verified:** Re-read AppServices.cs:170-185 (Dispose list, Cli absent), CliRunner.cs:48-65 (class declaration and fields, no IDisposable, no shutdown token), App.xaml.cs:63-66 (OnExit body), and UpgradeRunner.cs:1055-1063 (installer invocation via RunExecutableAsync).

### 14. CliInvocation's retained output-line list has no cap, so the 200-entry Activity ring is unbounded in bytes — `high impact / small effort`

**Where:** `DefenseClaw.Core/Cli/CliInvocation.cs:78`

CliRunner trims the activity ring by count only (200 entries), but each retained CliInvocation accumulates every stdout/stderr line via an uncapped `List<CliOutputLine>` — CliInvocation.cs:24 declares `private readonly List<CliOutputLine> _outputLines = new();`, and Append at lines 78-84 does `lock (_gate) { _outputLines.Add(line); }` with no trimming anywhere in the class. CliRunner.Record (CliRunner.cs:335-345) trims `_activity` by count only (`while (_activity.Count > ActivityCapacity) { _activity.RemoveLast(); }`), and every line reaches Append via CliRunner.Capture at CliRunner.cs:300-311. ActivityPanelViewModel.cs:108-111 mirrors the same count-based cap on Rows, and its CapacityNote at :60-61 confirms the panel's stated cap is purely "last N invocations" with no mention of line truncation — while LogsPanelViewModel.cs:33 (`MaxBufferedLines = 5000`, trimmed at :242/:285) shows the codebase already has a precedent pattern for capping buffered lines that was not applied here.

**Symptom:** A single chatty command (agent discover on a large repo, or the installer streaming progress) retains its full transcript in memory, and up to 200 such entries are held simultaneously in a tray app meant to run for weeks — working set grows steadily with no visible cause, remediable only via the Clear button or a restart.

**Refactor:** Cap _outputLines in CliInvocation.Append following the LogsPanelViewModel.MaxBufferedLines pattern — keep head+tail with an elided-count marker — and surface the truncation alongside the existing CapacityNote.

**Verified:** Re-read CliInvocation.cs:1-104 in full (no cap on _outputLines anywhere), CliRunner.cs:300-345 (Record/Capture, count-only trim), ActivityPanelViewModel.cs:60-61 and :108-111, and LogsPanelViewModel.cs:33, :242, :285 (existing MaxBufferedLines precedent).

### 15. Read-only SQLite connections against a WAL database can fail exactly when the gateway is down, with no retry or busy-timeout — `medium impact / medium effort`

**Where:** `DefenseClaw.Core/Audit/AuditReader.cs:56`

AuditReader and InventoryReader always open `Mode=ReadOnly` with no fallback and no busy timeout — AuditReader.cs:52-57 `BuildReadOnlyConnectionString` sets only DataSource and Mode=ReadOnly, and InventoryReader.cs:33-38 is identical. A read-only connection cannot create or recover the -shm file or perform WAL recovery, so opening breaks precisely when the gateway is stopped or was killed uncleanly, and any SQLITE_BUSY is not retried. The class's own doc comment (AuditReader.cs:10-13) scopes its verification to the healthy case only — "a read-only connection against a WAL database with a hot -wal/-shm pair opens fine" — without claiming recovery of a missing/stale -shm works; a grep of DefenseClaw.Core for `immutable`, `ReadWrite`, `busy_timeout`, and `Default Timeout` found no hits. The catch filters that swallow this failure into a UI banner are confirmed at AuditPanelViewModel.cs:205-206 and :267-268, OverviewPanelViewModel.cs:576-577 (comment: "A locked or half-written DB must degrade the tile, not the panel"), and InventoryPanelViewModel.cs:269, all filtering `ex is SqliteException or IOException or (InvalidOperationException|UnauthorizedAccessException)`.

**Symptom:** When the gateway is stopped or crashed leaving a hot -wal with no valid -shm, opening the connection throws SqliteException, which the panels catch cleanly (no crash) but surface as "audit.db could not be read" / "Audit counts unavailable" — exactly the moment an operator most needs the historical audit trail, with no retry and no distinction from a genuinely corrupt database.

**Refactor:** Attempt ReadOnly first; on the specific WAL-recovery/CANTOPEN failure, retry once with Mode=ReadWrite (never Create) since the app is the only other party touching the file when the gateway is down. Set an explicit busy timeout so concurrent writer activity is waited out rather than failing instantly.

**Verified:** Re-read AuditReader.cs:1-57 (class doc + BuildReadOnlyConnectionString) and InventoryReader.cs:25-38 (identical pattern); grepped DefenseClaw.Core for immutable/ReadWrite/busy_timeout/Default Timeout (no hits); re-read the four cited catch blocks at AuditPanelViewModel.cs:205-210 and :265-272, OverviewPanelViewModel.cs:576-582, and InventoryPanelViewModel.cs:269-271 — independent of, and not reliant on, the separately rejected claim that app upgrades replace these database files.

### 16. GatewayMonitor.RefreshAsync runs an unsynchronized second poll that races the background loop over plain fields — `medium impact / small effort`

**Where:** `DefenseClaw.App/Services/GatewayMonitor.cs:234`

RefreshAsync calls PollAsync directly with no gate against the background loop's own PollAsync — its body (GatewayMonitor.cs:234-239) is exactly `var snapshot = await PollAsync(cancellationToken); Publish(snapshot); return snapshot;`, with no semaphore or lock. PollAsync writes several non-volatile fields with no lock: RefreshAlertsAsync (:358-391) writes `_lastAlertPoll` (:364) and `_lastAlerts`/`_lastAlertsUnavailable` (:369-388) as plain field assignments, and RefreshClaudeCodeModeAsync (:408-434) writes `_lastStatusPoll` (:412) and `_lastClaudeCodeMode` (:423/:432), also unlocked. Confirmed no-token/no-gate call sites are TrayIconService.cs:179 (`_ = await _services.Monitor.RefreshAsync()`), AlertsPanelViewModel.cs:109, OverviewPanelViewModel.cs:128, and MainWindowViewModel.cs:124 — all bare `Monitor.RefreshAsync()` calls, any of which can overlap the loop's own poll running via RunAsync/PollAsync on its Task.Run at GatewayMonitor.cs:230. The exact banner text "The gateway is not answering; alerts are unavailable." is confirmed at GatewayMonitor.cs:320, matching the described user-visible symptom.

**Symptom:** Clicking Refresh (nothing debounces it) fires overlapping /health, /alerts and /status requests alongside the loop's own poll; _lastAlerts can be set by one poll while _lastAlertsUnavailable still holds the other's stale value, producing a populated alert list under the "gateway not answering" banner or vice versa. It also defeats the 30s alert-poll throttle documented at GatewayMonitor.cs:159-162, inflating load on the sidecar.

**Refactor:** Serialize polling with a SemaphoreSlim(1,1) held across PollAsync+Publish so manual refresh coalesces with or queues behind the loop; better, have RefreshAsync signal the loop to poll immediately rather than running a second concurrent poll. Combine _lastAlerts/_lastAlertsUnavailable into one immutable record updated with a single volatile write.

**Verified:** Re-read GatewayMonitor.cs:225-283 (Start/RunAsync/RefreshAsync/Dispose, no lock in RefreshAsync), :285-356 (PollAsync body and the alert/status refresh gating), and :358-434 (RefreshAlertsAsync/RefreshClaudeCodeModeAsync unlocked field writes, banner text at :319-320); grepped Monitor.RefreshAsync( call sites across DefenseClaw.App and confirmed all four cited lines match with no token/gate.

### 17. UpgradeRunner stages installers/scripts under StagingRoot but never prunes them, so every successful upgrade leaves the ~270 MB asset behind permanently — `medium impact / small effort`

**Where:** `DefenseClaw.App/Services/Updates/UpgradeRunner.cs:740`

Staging creates a per-version directory (`Directory.CreateDirectory(directory)` at :740/:625) and promotes the verified asset into it via `File.Move(partialPath, filePath, overwrite: true)` at :867-868, setting `promoted = true`. The only cleanup path, TryDelete at :912-915, is scoped to the failure case (`if (!promoted)`) inside the finally block at :908-916, and only ever targets the .partial file — never a promoted asset or an old version's directory. A grep of DefenseClaw.App and DefenseClaw.Core for Directory.Delete/EnumerateDirectories/GetDirectories turned up only one other hit, an unrelated cosign-package probe at UpgradeRunner.cs:1278. The ~270 MB size is corroborated at multiple points, e.g. line 841 ("installer is 270,013,440 bytes").

**Symptom:** Unbounded disk growth in %LOCALAPPDATA% — every successful upgrade permanently adds a large asset with no in-app cleanup action, even though StagingRoot's path is exposed as read-only text in UpgradeSectionViewModel.cs:227.

**Refactor:** After a successful run, prune every version directory under StagingRoot except the current one (checksums.txt makes re-verification cheap if ever needed, so eager deletion loses nothing). Add a manual "Clean up staged downloads" action with reclaimed-byte reporting as a stopgap.

**Verified:** Re-read UpgradeRunner.cs:736-752 (staging directory creation), :860-880 (promotion), and :908-916 (failure-only cleanup); grepped Directory.Delete|EnumerateDirectories|GetDirectories across DefenseClaw.App and DefenseClaw.Core, confirming no pruning path exists anywhere.

### 18. No DispatcherUnhandledException or TaskScheduler.UnobservedTaskException handler anywhere in the app — `high impact / small effort`

**Where:** `DefenseClaw.App/App.xaml.cs:23`

App.OnStartup wires the instance guard, services, theme, tray, window, and monitor but registers no last-resort exception handler — a grep across DefenseClaw.App for DispatcherUnhandledException/UnobservedTaskException/UnhandledException/AppDomain.CurrentDomain returns zero matches, and App.xaml.cs:23-61 (OnStartup) has no such subscription. App.xaml:5 confirms `ShutdownMode="OnExplicitShutdown"`, and MainWindow.xaml.cs:91-97 confirms OnClosing sets `e.Cancel = true; Hide();` rather than closing — the app is designed to run with no visible window, so a silent process death is invisible. Confirmed dispatcher-callback sites are LogsPanelViewModel.cs:202 (`dispatcher.BeginInvoke(...)`), ActivityPanelViewModel.cs:105 (`dispatcher.BeginInvoke(...)` inserting rows), and GatewayMonitor.cs:525 (`context.Post(...)`), alongside fire-and-forget Task.Run sites at LogTailer.cs:183 and GatewayMonitor.cs:230 (`_loop = Task.Run(() => RunAsync(_stop.Token));`).

**Symptom:** Any exception inside a dispatcher callback terminates the whole process with no dialog, no log, and no notification — since the app usually autostarts with --minimized and no window, the tray icon simply vanishes mid-session and the user may not notice monitoring has stopped. Faults in the discarded Task.Run loops (log tail, poll loop) are swallowed at GC with no notification, silently killing background functionality while the UI still looks live.

**Refactor:** Subscribe DispatcherUnhandledException in OnStartup to log to a file, show a tray balloon, and set e.Handled = true for recoverable panel-callback faults. Subscribe TaskScheduler.UnobservedTaskException to log and mark observed. This is also a natural place to consume PanelCatalog.PanelFaulted, which currently has no global listener.

**Verified:** Grepped DefenseClaw.App for DispatcherUnhandledException/UnobservedTaskException/UnhandledException/AppDomain.CurrentDomain (no hits); re-read App.xaml.cs:1-66 in full, App.xaml:5 (ShutdownMode), MainWindow.xaml.cs:85-100 (OnClosing hide-not-close), LogsPanelViewModel.cs:195-210, ActivityPanelViewModel.cs:98-112, GatewayMonitor.cs:505-527 and :224-231, and LogTailer.cs:175-203.

### 19. LogTailer's polling loop has no top-level error handling and StartWatching is not re-entry guarded — `medium impact / small effort`

**Where:** `DefenseClaw.Core/Logs/LogTailer.cs:183`

The discarded Task.Run loop only catches ObjectDisposedException from the semaphore wait — LogTailer.cs:181-203 shows the full loop body wrapped in `catch (ObjectDisposedException) { return; }` around the signal wait only, with the surrounding while-loop and ReadNewLines call having no enclosing try/catch, so any other exception propagates out and silently ends the tail with no signal to the caller. ReadNewLines (:120-144) itself only guards IOException and UnauthorizedAccessException. EnsureWatcher (:272-277) returns early only when `_watcher is not null || _disposed` — this does not gate the Task.Run in StartWatching, which runs unconditionally on every call (confirmed at :157 and :182, both calling EnsureWatcher() then unconditionally proceeding), so a second StartWatching call would spawn a duplicate loop. Both tailers are started via LogsPanelViewModel.InitializeAsync:98-99 (`_gateway.Tailer.StartWatching(); _watchdog.Tailer.StartWatching();`).

**Symptom:** The Logs panel silently stops receiving new lines while its live-state indicator still reads "live" — the operator concludes the gateway went quiet when the tail actually died, and with no UnobservedTaskException handler (see the App.xaml.cs finding) nothing records why. Recovery requires an app restart since the loop's own `while (!_disposed)` is its only restart mechanism.

**Refactor:** Wrap the loop body in try/catch, expose a TailFailed event so LogsPanelViewModel can show a real error state, and add an Interlocked-guarded _watching flag so StartWatching is idempotent like EnsureWatcher already is.

**Verified:** Re-read LogTailer.cs:150-205 in full (StartWatching body, only ObjectDisposedException caught), :120-144 (ReadNewLines' own guarded exceptions), and :272-290 (EnsureWatcher's guard scope); LogsPanelViewModel.cs:90-102 (InitializeAsync calling StartWatching on both tailers).

### 20. ConfigChangeToken.Changed is raised on the poll-timer/watcher thread straight into ObservableCollection mutation, with no UI-thread marshalling — `high impact / small effort`

**Where:** `DefenseClaw.Core/Config/ConfigChangeToken.cs:132`

The poll timer (`new Timer(_ => CheckAll(), null, interval, interval)` at line 132) and the FileSystemWatcher both drive CheckAll -> Raise -> `Changed?.Invoke` on a non-UI thread — CheckAll (:176-201) and Raise (:203-216, `Changed?.Invoke(this, args)` at :211) run on whichever thread triggered them, and Raise is called at :199, outside the `_gate` lock scoped at :186-197, confirming timer and watcher can both drive a reload concurrently. This reaches AppServices.OnConfigChanged -> ReloadConfig -> ConfigReloaded: AppServices.cs:68 subscribes `ConfigWatcher.Changed += OnConfigChanged`, and OnConfigChanged (:187) calls ReloadConfig, which ends with `ConfigReloaded?.Invoke(this, EventArgs.Empty)` at :167, still on the calling thread with no Post/marshalling. OverviewPanelViewModel.cs:94 subscribes `Services.ConfigReloaded += OnConfigReloaded`; the handler at :134 is `private void OnConfigReloaded(object? sender, EventArgs e) => Apply(Services.Monitor.Current);`, which flows into BuildAttention (:171/:182) and `Replace(Attention, rows)` at :333, mutating the `ObservableCollection<AttentionRow> Attention` declared at :105. The contrasting OnStateChanged handler (:136-140) carries the explicit comment "GatewayMonitor posts on the UI SynchronizationContext, so this is already the UI thread" — a guarantee GatewayMonitor.Publish (GatewayMonitor.cs:505-525) does provide for that path via `context.Post`, but which OnConfigReloaded has no equivalent for. MainWindowViewModel.cs:75/129/167 confirms the same unmarshalled-subscription shape via ApplyConfigError, though that path mutates simple properties rather than a bound collection.

**Symptom:** An external edit to config.yaml (or the installer re-planting an env override mid-session, which the code explicitly anticipates) reaching the Overview panel while it's open throws NotSupportedException from WPF's CollectionView ("does not support changes to its SourceCollection from a different thread") on a ThreadPool/timer thread. Combined with the missing global exception handler, this unhandled exception on a non-UI thread terminates the process — the tray icon vanishes mid-session with no dialog and no log.

**Refactor:** Marshal once at the boundary: have AppServices capture the UI SynchronizationContext at construction and Post ConfigReloaded through it, mirroring GatewayMonitor.Publish's existing pattern, so every current and future subscriber is automatically safe. Also move Raise inside the existing critical section (or add a re-entrancy flag) so the watcher event and poll timer cannot both drive a reload for the same edit concurrently.

**Verified:** Re-read ConfigChangeToken.cs:82-216 in full (Timer construction at :132, CheckAll/Raise at :176-216, Raise called outside the _gate lock at :199, Changed?.Invoke at :211), AppServices.cs:67-68 and :154-168 (OnConfigChanged/ReloadConfig, no thread marshalling), OverviewPanelViewModel.cs:93-96, :105, :125-146, :182, :333 (subscription, Attention collection, OnConfigReloaded vs the explicitly-marshalled OnStateChanged), GatewayMonitor.cs:505-525 (Publish's actual context.Post marshalling, confirming the asymmetry), and MainWindowViewModel.cs:75, :96, :129, :167-171 (parallel unmarshalled ApplyConfigError path).
## Data access & memory

### 21. Keyset pagination's SortKey wraps the retention column in COALESCE, defeating the retention index — `high impact / medium effort`

**Where:** `DefenseClaw.Core/Audit/AuditReader.cs:24`

The `SortKey` constant (L24-29) is `COALESCE(e.retention_timestamp_unix_nano, CAST(strftime(...) AS INTEGER) * 1000000000)`, and this exact expression is what `QueryAsync`'s `ORDER BY` (L76), `AppendWhere`'s From/To clauses (L273-283), and the cursor comparison (L285-292) all use verbatim. The schema (`DefenseClaw.Tests/Fixtures/audit-schema.sql` L418) only defines `CREATE INDEX idx_retention_audit_events_timestamp ON audit_events(retention_timestamp_unix_nano, id)` — a plain-column index, not an expression index on the COALESCE text — so SQLite's planner cannot match it to any of these predicates. The class doc comment (L18-22) even asserts the COALESCE is "indexed by idx_retention_audit_events_timestamp," which is incorrect for the wrapped form actually used in queries.

**Symptom:** Every Audit panel load, refresh, Load-more, and every `CountAsync`/`CountBySeverityAsync` call (used for dashboard severity tiles) forces a full table scan of `audit_events` plus a temp b-tree sort instead of using the retention index, becoming a visible stall/freeze on the Audit panel as `audit.db` grows.

**Refactor:** Stop deriving `sort_nanos` via COALESCE at query time. Make `retention_timestamp_unix_nano` (already trigger-maintained on every insert/update per the `retention_audit_events_timestamp_insert`/`update` triggers) the sole ordering/predicate column, and treat the strftime fallback as a one-time backfill rather than a per-query expression, so ORDER BY/WHERE bind to the bare column and hit `idx_retention_audit_events_timestamp`.

**Verified:** Read AuditReader.cs L1-359 in full (SortKey L24-29, QueryAsync L60-101, AppendWhere L206-298) and audit-schema.sql in full (grepped for `idx_retention_audit_events_timestamp` and `COALESCE`) — only one plain-column index exists at line 418, no expression index.

### 22. AuditRow eagerly pretty-prints structured_json for every row in every page, contradicting its own doc comment about lazy parsing — `medium impact / small effort`

**Where:** `DefenseClaw.App/ViewModels/AuditPanelViewModel.cs:453`

`AuditRow`'s constructor unconditionally calls `StructuredJson = PrettyJson(source.StructuredJsonRaw);` (L453) for every row built in `LoadAsync`'s foreach (L240-248, up to 100 rows per `PageSize=100`, L33), even though the class doc comment right above it (L410-413) claims "structured_json is parsed lazily by Core, so building a row stays cheap even at a page of 100." That lazy path is `AuditEvent.StructuredJson` (AuditEvent.cs L112-132), which `AuditRow` never calls — it bypasses it and does its own eager `JsonDocument.Parse` + indented `Serialize` on the raw string via `PrettyJson` (AuditPanelViewModel.cs L548-565), which does `JsonDocument.Parse(raw)` then `JsonSerializer.Serialize(document.RootElement, PrettyOptions)` with `WriteIndented=true`.

**Symptom:** Loading or filtering a page of audit rows (`Reload()` fires on every filter/severity/connector/search change, L139-149) pays a full JSON parse plus indented re-serialize for every row's structured_json blob (documented as "often several kilobytes," AuditEvent.cs L43) regardless of whether the operator ever opens that row's detail pane, causing a stutter that scales with page size and payload size.

**Refactor:** Make `AuditRow.StructuredJson` lazy (e.g. `Lazy<string>` or compute on first access from the detail-pane binding) instead of unconditionally in the constructor, mirroring the pattern `AuditEvent.StructuredJson` already uses.

**Verified:** Read AuditPanelViewModel.cs L212-280 (LoadAsync/Reload) and L410-565 (AuditRow class, doc comment, constructor, PrettyJson) and AuditEvent.cs L41-132 (doc comment and actual lazy StructuredJson property) — claim matches exactly.

### 23. AuditPanelViewModel.Rows has no cap, unlike every other streaming/paged collection in the app — `medium impact / small effort`

**Where:** `DefenseClaw.App/ViewModels/AuditPanelViewModel.cs:109`

`ObservableCollection<AuditRow> Rows` (L109) is only Cleared on a fresh load (L237); `LoadMoreAsync` (L160-161 → `LoadAsync(append: true)`) appends additional 100-row pages with no upper bound, unlike `ActivityPanelViewModel.Rows` (capped at `Services.Cli.ActivityCapacity`, enforced with `while (Rows.Count > Services.Cli.ActivityCapacity) Rows.RemoveAt(Rows.Count - 1);`) and `LogsPanelViewModel.DisplayedLines` (capped at `MaxBufferedLines=5000` via `TrimDisplayed`'s while-loop).

**Symptom:** An operator repeatedly clicking Load more on a busy audit.db accumulates AuditRow objects — each also carrying the eagerly pretty-printed StructuredJson string from finding 22 — without limit for the lifetime of the panel instance, growing working-set memory the longer the session runs.

**Refactor:** Cap Rows the same way Logs/Activity do (drop the oldest page once a ceiling like 2000-5000 rows is exceeded), or switch to a windowed/virtualized source instead of retaining every loaded row permanently.

**Verified:** Read AuditPanelViewModel.cs L105-280 (Rows declaration, LoadAsync), ActivityPanelViewModel.cs L70-115 (cap enforcement), LogsPanelViewModel.cs L33, L239-246, L273-290 (cap enforcement on both Buffer and DisplayedLines) — all three collections' cap behavior confirmed exactly as described.

### 24. Log buffer trim shifts the whole backing array per line instead of batching the excess removal — `high impact / medium effort`

**Where:** `DefenseClaw.App/ViewModels/LogsPanelViewModel.cs:285`

`Append()` (L273-290) enforces `MaxBufferedLines=5000` (L33) with `var excess = state.Buffer.Count - MaxBufferedLines; if (excess > 0) { state.Buffer.RemoveRange(0, excess); }` called once per appended line inside a foreach over up to `LogTailerOptions.MaxLinesPerBatch=2000` lines (LogTailer.cs L15) per `OnLinesReceived` dispatcher callback (L195-237); `TrimDisplayed()` (L240-246) does the equivalent one-at-a-time with `while (DisplayedLines.Count > MaxBufferedLines) DisplayedLines.RemoveAt(0);`, both running on the UI thread inside `dispatcher.BeginInvoke`. `List<T>.RemoveRange(0, n)` and `ObservableCollection.RemoveAt(0)` are both array-backed operations requiring an O(count) shift of the remaining elements, and `RemoveAt` additionally raises one `CollectionChanged(Remove)` notification per call.

**Symptom:** During a burst of up to 2000 new log lines in one poll, the UI thread performs up to ~2000 O(5000) array shifts (Buffer) plus up to 2000 individual `RemoveAt(0)` shifts and CollectionChanged notifications (DisplayedLines) inside one dispatcher callback — a visible freeze on the Logs panel exactly when the log is busiest.

**Refactor:** Replace the `List<T>` buffer with a ring buffer/circular array for O(1) trim-from-front, and batch DisplayedLines trimming into a single bulk removal or Reset-style rebuild instead of `RemoveAt(0)` in a loop.

**Verified:** Read LogsPanelViewModel.cs L194-290 (OnLinesReceived, TrimDisplayed, Append) and LogTailer.cs L1-18 (LogTailerOptions.MaxLinesPerBatch=2000) — per-line RemoveRange-on-append and once-per-batch RemoveAt-loop both confirmed exactly as described.

### 25. GatewayClient.Interpret parses every object-shaped poll response body twice — `low impact / small effort`

**Where:** `DefenseClaw.Core/Gateway/GatewayClient.cs:174`

`Interpret<T>` (L174-214) calls `TryReadErrorPayload(body)` (L220-248), which does `JsonDocument.Parse(body)` (L235) for any body starting with `{` purely to check for an `error` property, and then — when no error envelope is found — independently calls `JsonSerializer.Deserialize<T>(body, JsonOptions)` (L205) on the same string again. `GatewayMonitor` polls `GetHealthAsync` (object-shaped GatewayHealth body) every `FastInterval=5s` (GatewayMonitor.cs L166) indefinitely for the app's lifetime, via `RunAsync`'s while loop (L253-283) with no exit condition.

**Symptom:** Steady-state background allocation/GC pressure for the app's entire runtime — small per call, but it is the single hottest, most frequent data-access path since it runs every 5 seconds without stopping.

**Refactor:** Parse the body once into a `JsonDocument` and use its `RootElement` both for the error-envelope check and for deserialization (e.g. `JsonElement.Deserialize<T>()`), avoiding the second independent parse.

**Verified:** Read GatewayClient.cs L120-262 (GetAsync, Interpret, TryReadErrorPayload) and GatewayMonitor.cs L164-283 (FastInterval, RunAsync poll loop, PollAsync calling GetHealthAsync) — double-parse and continuous 5s polling both confirmed.

### 26. CliInvocation.Snapshot() copies the full live output history every 500ms for every running invocation, only to extract the tail — `medium impact / small effort`

**Where:** `DefenseClaw.Core/Cli/CliInvocation.cs:87`

`OutputLines` (L54-63) does `_outputLines.ToArray()` under lock on every access; `Snapshot()` (L87-99) allocates a new `CliInvocation` and calls `copy._outputLines.AddRange(OutputLines)` — a full O(n) copy, actually two copies (the `ToArray()` inside the getter, then `AddRange` into the copy). `ActivityRow.Tick()` calls `Invocation.Snapshot()` on a 500ms `DispatcherTimer` for every row still running (ActivityPanelViewModel.cs L34,47-49 TickInterval; L132-141 TickRunningRows; L198-211 Tick calling Snapshot then `SyncOutput(snapshot.OutputLines)`), even though `SyncOutput` only reads `lines[_syncedOutputCount..]` (L265-274) — the delta since `_syncedOutputCount`. `CliRunner.cs` bounds `Activity` to a 200-item ring (L58-68 ActivityCapacity, L335-344 Record), but nothing bounds an individual `CliInvocation`'s `_outputLines`.

**Symptom:** For a long-running, verbose CLI invocation, each 500ms tick allocates and copies the entire growing output history just to discard everything but the new tail, so allocation cost grows the longer the command runs, producing UI stutter/GC pressure that worsens over the invocation's lifetime.

**Refactor:** Add an incremental read API (e.g. `ReadFrom(int startIndex)` under the same lock) so each tick only copies the new tail, and consider bounding `_outputLines` itself with a rolling cap the way `CliRunner.Activity` is bounded.

**Verified:** Read CliInvocation.cs L1-103 in full and ActivityPanelViewModel.cs L32-211 (Tick interval, TickRunningRows, ActivityRow.Tick/SyncOutput) and CliRunner.cs L48-113,335-345 (ActivityCapacity ring bound, confirming no equivalent bound exists on a single invocation's output list).

### 27. Read APIs open a fresh SqliteConnection per call with no batching, multiplying round trips on Audit panel init and Inventory table-count loading — `low impact / medium effort`

**Where:** `DefenseClaw.Core/Audit/AuditReader.cs:69`

Every `AuditReader` method (`QueryAsync`, `GetByIdAsync`, `CountBySeverityAsync`, `CountAsync`, `DistinctAsync`) opens its own `new SqliteConnection(_connectionString)` independently — `await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(...)` repeated at QueryAsync (L69-70), GetByIdAsync (L112-113), CountBySeverityAsync (L136-137), CountAsync (L163-164), and DistinctAsync (L189-190, shared by ListBucketsAsync/ListConnectorsAsync/ListActionsAsync). `AuditPanelViewModel.LoadFilterOptionsAsync` calls `ListBucketsAsync`, `ListConnectorsAsync`, `ListActionsAsync` sequentially on every panel `InitializeAsync` (L177-183) — three separate connection opens for one logical "load the filter dropdowns" operation. `InventoryReader` has the same per-call `OpenAsync` helper (L151-164) used independently by `ListTablesAsync` (L47), `CountAsync` (L90), `BrowseAsync` (L111), and `InventoryPanelViewModel.LoadTableCountsAsync`'s `foreach (var name in tableNames)` (L279-302) calls `await Services.Inventory.CountAsync(name, cancellationToken)` per table, each opening its own connection.

**Symptom:** Panel initialization and inventory-table enumeration pay N separate SQLite connection-open operations (each still validating the WAL header on a read-only connection) instead of one, adding avoidable latency to Audit-panel startup and to inventory table-count loading when the database has many tables.

**Refactor:** Open one connection per logical operation (e.g. for LoadFilterOptionsAsync, and for the LoadTableCountsAsync loop) and issue the sequential queries against it instead of a fresh connection per call.

**Verified:** Read AuditReader.cs in full (connection-open sites at L69-70,112-113,136-137,163-164,189-190), AuditPanelViewModel.cs L177-210 (LoadFilterOptionsAsync), InventoryReader.cs L151-164 (OpenAsync) and its four call sites, and InventoryPanelViewModel.cs L279-302 (LoadTableCountsAsync per-table loop) — all connection-open sites confirmed as claimed.
## Startup & packaging

### 28. No ReadyToRun / PGO tuning for the tray app's autostart-at-login launch path — `medium impact / small effort`

**Where:** `.github/workflows/ci.yml:36`

The repo's only release publish step builds a purely JIT'd self-contained single-file win-x64 binary: `dotnet publish DefenseClaw.App/DefenseClaw.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish` (ci.yml:37-40). A repo-wide grep for `PublishReadyToRun`/`TieredPGO`/`QuickJitForLoops`/`RuntimeIdentifier` turns up no such property in any .csproj or workflow file. App.xaml.cs:7-13's doc comment documents the `--minimized` autostart-at-login path as a first-class scenario, while DefenseClaw.App.csproj:33-46 pulls in WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2 (source-generated `ObservableObject`/`RelayCommand`), H.NotifyIcon.Wpf, and AvalonEdit — all JIT'd cold on first launch.

**Symptom:** Slower time-to-tray-icon on a cold-cache login boot than an R2R-precompiled equivalent, worst exactly when OS/disk caches are cold at startup, for an app whose documented primary path is unattended autostart.

**Refactor:** Add `-p:PublishReadyToRun=true` to the release publish step (the RID is already pinned via `-r win-x64`) so startup-critical framework/app code ships precompiled, keeping TieredPGO on (its .NET 9 default). Confirm the win with a cold-boot time-to-tray-icon measurement, since R2R trades a larger binary for faster startup.

**Verified:** Read ci.yml:25-54 (the only publish step in the repo) and found no ReadyToRun/PGO flags; read App.xaml.cs:1-14 confirming the `--minimized` autostart path is a documented scenario; read DefenseClaw.App.csproj:1-47 confirming no `PublishReadyToRun` property and the JIT-heavy dependency set (WPF-UI, CommunityToolkit.Mvvm, H.NotifyIcon, AvalonEdit).

### 29. Self-contained single-file exe ships uncompressed — `low impact / small effort`

**Where:** `.github/workflows/ci.yml:39`

`IncludeNativeLibrariesForSelfExtract` is set but `EnableCompressionInSingleFile` is not, so the win-x64 self-contained WPF single-file bundle is shipped and distributed at full uncompressed size. ci.yml:37-40 sets `-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` with no `-p:EnableCompressionInSingleFile=true` anywhere in that step or in DefenseClaw.App.csproj (read in full, lines 1-47).

**Symptom:** Needlessly large GitHub release asset for every tagged release (ci.yml:48-54 publishes `publish/*.exe` + `checksums.txt` via `softprops/action-gh-release`) and needlessly large on-disk footprint once installed.

**Refactor:** Add `-p:EnableCompressionInSingleFile=true` to the release publish command. The one-time decompression cost lands at first-run bundle extraction (cached thereafter in the .NET single-file extraction cache) — an acceptable trade for a smaller download on an app installed once and autostarted for months.

**Verified:** Re-read ci.yml:36-40 and DefenseClaw.App.csproj:1-47; confirmed `EnableCompressionInSingleFile` appears nowhere in either file.

### 30. MainWindow is fully constructed even on the --minimized autostart path — `medium impact / medium effort`

**Where:** `DefenseClaw.App/App.xaml.cs:50`

`App.OnStartup` unconditionally builds `new MainWindow(...)` at line 50 before checking the `--minimized` flag at lines 54-57, so the window's `InitializeComponent`, WPF-UI theme-watcher hookup, and a tray-state icon render happen on every launch even when the window is never shown. App.xaml.cs:46-57 shows: `_tray = new TrayIconService(_services); ... _window = new MainWindow(_services, _catalog, _tray); MainWindow = _window; if (!e.Args.Contains("--minimized", ...)) { _window.Show(); }` — construction at line 50 happens unconditionally, before the minimized check. MainWindow.xaml.cs:26-49 confirms the constructor does real work unconditionally: `InitializeComponent()` (line 33), `SystemThemeWatcher.Watch(this)` (line 40), and `ApplyShieldIcon(...)` (line 45), which calls `ShieldIconFactory.CreateImage`, a `DrawingVisual`->`RenderTargetBitmap` render (ShieldIconFactory.cs:92-97, 126-147).

**Symptom:** An autostart-at-login launch (the app's documented primary unattended path, App.xaml.cs:53) pays full WPF window construction — XAML/BAML parse, theme-watcher registration, and a `DrawingVisual` render — for a window that may sit hidden for the entire session.

**Refactor:** Defer MainWindow construction until `ShowAndActivate()` is actually first needed (lazily instantiate inside the `OpenDashboardRequested` handler and `OnActivationRequested`) instead of building it unconditionally in `OnStartup`; on `--minimized` launches this removes window-chrome construction entirely until the user asks for the dashboard.

**Verified:** Re-read App.xaml.cs:41-61 confirming construction precedes the minimized check, and MainWindow.xaml.cs:26-69 to verify what the constructor actually does. One inaccuracy in the original candidate was corrected: MainWindow's icon path (`ApplyShieldIcon` -> `ShieldIconFactory.CreateImage`, ShieldIconFactory.cs:92-97) renders a `RenderTargetBitmap` and freezes it as an `ImageSource` directly — it does not go through the GDI+ `Bitmap.GetHicon`/`DestroyIcon` interop round-trip the original evidence described; that HICON dance is unique to `ShieldIconFactory.Create` (used only by `TrayIconService`, see finding 31). The core claim — unconditional MainWindow construction before the `--minimized` check — still stands and is the more important of the two findings.

### 31. Tray icon is rasterized synchronously inline in the composition-root startup path — `low impact / medium effort`

**Where:** `DefenseClaw.App/Services/TrayIconService.cs:61`

`TrayIconService`'s constructor calls `Apply(_services.Monitor.Current)` before `Monitor.Start()` has ever run, which synchronously renders a `DrawingVisual` to a `RenderTargetBitmap`, PNG-encodes it, and round-trips through an unmanaged GDI+ `Bitmap.GetHicon`/`DestroyIcon` before the constructor returns. TrayIconService.cs:61-62 shows `Apply(_services.Monitor.Current); _icon.ForceCreate(enablesEfficiencyMode: false);` — `Apply()` (line 191) calls `ShieldIconFactory.Get(shield)` (line 197), which (ShieldIconFactory.cs:76-89) calls `Create()` (line 99), which calls `Render()` (`DrawingVisual` + `RenderTargetBitmap`, lines 126-147), then `PngBitmapEncoder.Save` (103-108), `new Drawing.Bitmap(pngStream).GetHicon()` (110-111), `Icon.FromHandle`/`Save` round-trip (114-118), and `DestroyIcon(handle)` in a finally (122). The class's own doc comment (ShieldIconFactory.cs:29-38) concedes "a few milliseconds" and explicitly describes the GDI+ handle dance as "the price of the WPF/GDI+ boundary."

**Symptom:** One more synchronous unmanaged-interop hop stacked into the `OnStartup` call chain before the tray icon — the app's primary always-visible surface — can appear; uncached across process launches, so it runs on every single launch including every autostart relaunch.

**Refactor:** Pre-render the "Stopped" state icon (the deterministic first-paint state before any poll completes) at build/publish time as an embedded `.ico` resource instead of drawing it at runtime on the critical path, reserving the runtime `DrawingVisual`/`GetHicon` path for the Running/Warning/Critical states reached only after the first poll completes.

**Verified:** Re-read TrayIconService.cs:33-63 and :191-201 (`Apply`), and ShieldIconFactory.cs in full (1-201), confirming the exact call chain `Get`->`Create`->`Render` plus the PNG encode and `GetHicon`/`DestroyIcon` interop, and that it runs unconditionally in the `TrayIconService` constructor called from `App.OnStartup` line 46, before `Monitor.Start()` at line 60.

### 32. AppServices constructor chains synchronous file I/O on the UI thread with no overlap against window/tray setup — `low impact / medium effort`

**Where:** `DefenseClaw.App/Services/AppServices.cs:41`

`AppServices.Initialize()` runs synchronously as the first line of `App.OnStartup`, and its private constructor calls `ConfigStore.Load()` and `TokenResolver.Resolve()` (backed by `DotEnvFile` reads) back-to-back before `ApplyTheme`, the tray, or the window are created — even though `ConfigStore` already has an async `LoadAsync()` overload that goes unused on this path. App.xaml.cs:41 shows `_services = AppServices.Initialize();`. AppServices.cs:39-71 (constructor): line 43 `_config = LoadConfigSafely(ConfigStore, out var configError);`, which (line 214-220) calls the synchronous `store.Load()`; line 46-47 `TokenResolver = new TokenResolver(...); _token = TokenResolver.Resolve(_config.Config);`. `ConfigStore.cs` exposes both a synchronous `Load()` (line 44) and an async `LoadAsync(CancellationToken)` (line 56) — the async overload exists but `AppServices` never calls it.

**Symptom:** Every launch pays two serial synchronous disk reads plus a YAML parse on the UI thread before any UI object exists to show progress; individually cheap, but it is dead time on the critical path that cannot overlap with tray/window chrome construction since nothing else has started yet.

**Refactor:** Kick off `ConfigStore.LoadAsync()` on a background task the moment `OnStartup` begins, and await/join it only where the resolved config is first needed (e.g. right before `ApiPort`/Gateway construction), so the disk I/O overlaps with tray/window chrome construction instead of serializing in front of it.

**Verified:** Re-read AppServices.cs:39-71 and :214-231 (`LoadConfigSafely`) confirming the synchronous `Load()` path, and confirmed ConfigStore.cs:44 (sync `Load`) and :56 (async `LoadAsync`) both exist, with `AppServices` calling only the sync one.

### 33. Setup wizard catalog's CLI --help probe fan-out is cached only for the process lifetime, never persisted to disk — `low impact / medium effort`

**Where:** `DefenseClaw.App/Services/Wizards/WizardCatalog.cs:37`

`WizardCatalog` documents "Results are cached for the life of the process," and `SetupHelpProbe`'s cache is a purely in-memory `ConcurrentDictionary`, so the full phase-two fan-out (up to `MaxParallelProbes=6` concurrent `defenseclaw setup <target> --help` processes, ~800ms each, across roughly 31 targets) reruns from zero on every fresh app process — unlike `UpdateChecker`, which persists its own cache to disk. WizardCatalog.cs:37 states: "Results are cached for the life of the process; ReloadAsync starts over." SetupHelpProbe.cs:30-31's doc comment reads: "Probes are ~800 ms each on 0.8.7 (Python start-up dominates)... caches per app run." SetupHelpProbe.cs:43 declares `private readonly ConcurrentDictionary<string, Task<HelpProbeResult>> _cache` — purely in-memory, no file I/O anywhere in the class. By contrast, UpdateChecker.cs:92-96, :116, :131, and :155-159 confirm a real on-disk cache at `%LOCALAPPDATA%\...\latest-release-cache.json` with a 24h `CacheLifetime`.

**Symptom:** This path is correctly lazy — `WizardCatalog.Shared(services)` is invoked only from SetupPanelViewModel.cs:61 and WizardLauncher.cs:32, and PanelCatalog.cs:87-88 constructs the Setup panel's view model lazily via a factory delegate, so it never blocks first frame or runs during `OnStartup`. But a user who opens Setup once per session pays the full multi-second probe warm-up again after every relaunch, including an autostart relaunch after a reboot, with no on-disk cache the way `UpdateChecker` has one.

**Refactor:** Give `SetupHelpProbe` an on-disk cache keyed by the installed CLI's version (mirroring `UpdateChecker`'s `%LOCALAPPDATA%\DefenseClaw.App\updates\latest-release-cache.json` pattern), invalidated whenever the resolved `defenseclaw --version-json` changes, so repeat launches against the same CLI build skip straight to phase-two-complete definitions.

**Verified:** Re-read WizardCatalog.cs:1-95 and SetupHelpProbe.cs:1-70 confirming the in-memory-only `ConcurrentDictionary` caches and the documented per-process lifetime; grepped `WizardCatalog` usage sites (WizardLauncher.cs:32, SetupPanelViewModel.cs:61) and PanelCatalog.cs:87-88 to confirm it is lazily constructed behind the Setup panel, not eagerly during startup; read `UpdateChecker.cs`'s cache-related lines to confirm the on-disk-cache precedent this app already follows elsewhere.

---

## Appendix: rejected findings

Listed so a future evaluation does not re-surface them.

- **"Default SQLite connection pooling pins audit.db/inventory.db across the runtime upgrade that replaces them"** — rejected: the premise is contradicted by verified behavior. The Setup-installer upgrade channel (live-verified on this machine's 0.8.7 → 0.8.10 upgrade, documented in `UpgradeRunner.cs`) installs over the top and leaves `%USERPROFILE%\.defenseclaw` — including `audit.db` and `inventory.db` — intact, so there is no upgrade-time file replacement for pooled handles to go stale against. The separable, real concern about read-only WAL connections during a gateway-down window is preserved as finding #15.
