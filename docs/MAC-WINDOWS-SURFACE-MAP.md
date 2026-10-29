# Mac to Windows surface map

Part 1 of CUST-387. Mac source: DefenseClaw for macOS v1.1.34 (`DefenseClawMac`, commit 6ce825f). Windows source: this repo at `main` 45e6f8f.

**Nothing was moved.** Section 8 is a proposal for wave 2, to start only after CUST-383, 384, 385 and 386 have merged (they are restructuring Shell, Setup and Settings right now). Rows and sentences marked *(in progress, CUST-38x)* describe the **target state** of that work, not what is on `main` today.

## 1. Purpose and how to use

Goal: a Windows developer can find the counterpart of any Mac surface, and the Mac counterpart of any Windows file, without redoing the audit.

| You want | Go to |
|---|---|
| The Windows view, view-model, service, nav id and data source for a Mac file under `App/` or `Features/` | Section 4 (one row per Mac file or surface) |
| What replaces a Mac `DataLayer` file | Section 5 |
| What replaces `DesignSystem` | Section 6 |
| What exists on only one platform, or exists on both but is built differently | Section 7 |
| Where a Windows file will live after wave 2, and what the move can break | Section 8 |
| What a `CUST-nnn` in a row is | Section 9 |

Conventions used in every table:

- Windows paths are relative to `DefenseClaw.App/` unless prefixed `Core:` (`DefenseClaw.Core/`), `Admin:` (`DefenseClaw.GatewayAdmin/`) or `AdminContract:` (`DefenseClaw.GatewayAdmin.Contract/`). A path with no extension stands for every file with that stem (`Views/Panels/OverviewPanel` is the `.xaml` and the `.xaml.cs`); a trailing `*` stands for all partial-class files (`ViewModels/AlertsPanelViewModel*`).
- Mac paths are relative to `DefenseClawMac/`.
- Navigation ids are the `Id` values in `Services/PanelCatalog.cs`. Two Mac ids are spelled differently (`aiDiscovery` is `ai-discovery`, `aiRuntime` is `ai-runtime`). A deep link is a `NavigationRequest` to one of these ids (`Services/Shell/NavigationRequest.cs`).
- Markers: **[WIN]** exists on Windows only. **[MAC]** exists on the Mac only. **[IMPL]** both platforms have the concept, the implementation is platform-specific. No marker means a like-for-like counterpart.
- Data sources: *gateway* routes are relative to the sidecar root and read-only (`Core:Gateway/GatewayClient` only issues GET; every Windows runtime mutation is a reviewed CLI argv through `Services/Shell/CommandReview` and `Core:Cli/CliRunner`, recorded in Activity; the one direct write is the config editor's reviewed, backed-up save of `config.yaml`). *CLI* verbs are `defenseclaw` verbs. *SQLite* names the table in the runtime's `audit.db` (or `inventory.db`) opened read-only. 404/405 from the gateway and "No such command" from the CLI mean "not supported", never an error banner.
- Issue numbers come from `git log --grep CUST-` and the history of each file group (Section 9 gives each a one-line title). Early panels were created in the scaffold commits that carry no issue number; those rows list the issues that shaped the surface.

Every Windows path in this file was checked against `git ls-files` at 45e6f8f. After a rename wave, re-check a path with `git ls-files | grep -F <path>`.

## 2. Source-folder layout vs runtime on-disk folders

### 2.1 Source folders (what is in git)

| Mac | Windows today | Windows after wave 2 (proposal, Section 8) |
|---|---|---|
| `App/` (`DefenseClawApp.swift`, `AppState.swift`) | `App.xaml(.cs)`, `AssemblyInfo.cs`; composition root `Services/AppServices.cs` | `App/` holds `App.xaml(.cs)` and `AssemblyInfo.cs`; `Services/AppServices.cs` stays |
| `Features/` (one SwiftUI file per surface) | `Views/{Panels,Shell,Wizards,ConfigEditor,SetupResources,Redaction,Guardrail,Updates,FirstRun,JudgeHistory}`, `ViewModels/`, `MainWindow.xaml` | `Features/<Surface>/` with each surface's views and view-models together |
| `DataLayer/` | `DefenseClaw.Core/` (runtime and data boundary) plus `Services/` (app-level services) | unchanged: Core and Services do not move |
| `DesignSystem/` | `Themes/`, `Views/Controls/`, panel layout helpers in `Views/Panels/`, `Services/Appearance/` | `DesignSystem/{Themes,Controls,Layout}`; `Services/Appearance/` stays |
| `Assets.xcassets/` (app icon, four menu-bar shields) | `Assets/DefenseClaw.ico`; the tray shield is drawn at runtime by `Services/ShieldArtwork`, `ShieldIconFactory`, `TrayShieldPresenter` | unchanged |
| `Resources/policy_catalog_bridge.py` | none: the policy model is read from `policy` CLI output and data files (`Core:Policy/Model/*`) | none |
| `GatewayAdminHelper/` (privileged XPC helper) | `Admin:AdminHelper` and the `DefenseClaw.GatewayAdmin*` projects | unchanged |

### 2.2 Runtime on-disk folders (what the apps read and write)

**Nothing under the live DefenseClaw install or data directories moves. Wave 2 renames files inside `DefenseClaw.App/` only; it changes no path the app reads or writes at run time.** Every location below is a code constant (`Core:Paths/DefenseClawPaths`, `Services/Settings/AppSettingsStore`, ...), not derived from a source folder. The only things the move changes at run time are resource URIs inside the compiled assembly (Section 8.7).

| Location | Owner | Contents | Touched by wave 2 |
|---|---|---|---|
| `DEFENSECLAW_HOME`, else `%USERPROFILE%\.defenseclaw` (the data directory; the full resolution order is in `Core:Paths/InstallationContext`) | DefenseClaw runtime | `config.yaml`, `.env`, `audit.db`, `inventory.db`, `judge_bodies.db`, `gateway.log`, `gateway.jsonl`, `watchdog.log`, `*.pid`, `policies\`, `hooks\`, `plugins\`, `quarantine\`, `agent_discovery.json`, `ai_discovery_state.json`, `doctor_cache.json`, `active_connector.json`, `.venv\` | No |
| The runtime's install and `bin` directories (`DefenseClawPaths.BinDirectory`, managed layout) | DefenseClaw runtime / `install.ps1` | `defenseclaw.exe`, `defenseclaw-gateway.exe`, bundled Python | No |
| `%LOCALAPPDATA%\DefenseClaw.App\` | This companion app | `settings.json` (all app settings), `logs\` (crash logs), `cache\` (hook totals, `setup-help-cache.json`, `icons\`), `cwd\` (the CLI's neutral working directory), `updates\latest-release-cache.json`, `upgrades\<version>\`, `admin-gateway\` (administrator helper manifest and staging), self-update state | No |
| The app's install folder (`Program Files` or a portable folder) | This companion app | `DefenseClaw.App.exe`, `DefenseClaw.GatewayAdmin.exe` | No |
| `DefenseClaw.App/Assets/DefenseClaw.ico` in the repo | Build | `ApplicationIcon`, installer `SetupIconFile` | No (`Assets/` stays) |

Companion settings and cache are separate from the runtime's directories. The app changes the runtime's directories only through the CLI and the verified `install.ps1` (reviewed argv) and the config editor's reviewed, backed-up save of `config.yaml`; exports go where the operator points them.

## 3. Navigation: Mac target layout vs Windows `main` at 45e6f8f

Mac (`Features/MainWindow.swift`, `PanelID` in `App/AppState.swift`): four sidebar groups, Settings is a separate scene (Cmd-comma) with four tabs. Windows (`Services/PanelCatalog.cs`): the same four group names, plus a footer panel for Settings.

| Group | Mac target | Windows `main` @ 45e6f8f | Windows target (in flight) |
|---|---|---|---|
| Monitor | overview, alerts, logs, audit, activity | `overview`, `alerts`, `logs`, `audit`, `activity` | same |
| Govern | skills, mcps, plugins, tools, **policies**, **sandboxes** | `skills`, `mcps`, `plugins`, `tools` | `policies` moves here after `tools` and a new non-executing `sandboxes` page is added *(in progress, CUST-383)* |
| Discover | inventory, aiDiscovery, aiRuntime, registries | `inventory`, `ai-discovery`, `ai-runtime`, `agents` **[WIN]**, `registries` (`ai-runtime` and `agents` are capability-gated) | `agents` stays after Discover as a Windows extension *(in progress, CUST-383)*; today it sits between `ai-runtime` and `registries` |
| Configure | setup | `setup`, `policies` | `setup` only (policies left) *(in progress, CUST-383)* |
| Settings | scene, tabs General / Monitoring / Notifications / Connection | footer panel `settings` (Ctrl+,): one scrolling page of cards | tabs General / Monitoring / Notifications / Connection; file roles Config, Data, Environment, Audit DB, Virtual environment, Gateway log *(in progress, CUST-386)* |

Other differences between the Mac layout and `main`:

| Area | Mac | Windows `main` @ 45e6f8f | Change in flight |
|---|---|---|---|
| Application menu bar | Monitor, Commands, Go, Help menus with chords (`DefenseClawApp.swift`) | none; the same actions are in the command palette (`Views/Shell/CommandPaletteControl`), the shortcuts catalogue (`Services/Shell/ShortcutCatalog`) and the tray | new menu bar *(in progress, CUST-384)* |
| Setup page | Area grid first, then the Config Editor on the same page, one draft | Wizard tile grid plus cards (credentials, readiness, restart queue); the config editor is a separate window (`Views/ConfigEditor/ConfigEditorWindow`) | area grid first, inline config editor sharing one draft *(in progress, CUST-385)* |
| Sandboxes | `SandboxesView` driven by the daemon's `/api/v1/sandbox` and `sandbox` CLI verbs | no panel; Overview's Services list has a Sandbox card that says "not supported on Windows" (`ViewModels/OverviewPanelViewModel.Services.cs`); sandboxes stay unsupported (CUST-296 canceled) | explanatory page, no working actions *(in progress, CUST-383)* |
| Chord order | `PanelID.allCases` (Cmd-1..9, Cmd-0, Cmd-Shift-n) | `PanelCatalog.ChordOrder`: panels needing no capability first, so an upgrade never rebinds a chord | follows the new sidebar order (CUST-383, CUST-384) |
| Sidebar badges | Alerts count; degraded triangle on Overview | Alerts count; "new since last visit" capsules on Audit, Activity, AI Discovery **[WIN]** (`Services/UnreadCountsService`) | none |
| Status strip, appearance styles, shortcuts overlay | none | present **[WIN]** | none |


## 4. Surface table

Columns: Mac file(s) | Windows view(s) | Windows view-model(s) | Windows service(s) and `Core:` types | canonical nav id | runtime data source | owning issues. Rows are in sidebar order. *(in progress, CUST-38x)* marks target state.

### 4.1 App, shell and cross-cutting chrome

| Mac file(s) | Windows view(s) | View-model(s) | Services / Core types | Nav id | Data source | Issues |
|---|---|---|---|---|---|---|
| `App/DefenseClawApp.swift` (window scene, Monitor / Commands / Go / Help menus, Settings scene, menu-bar extra, `AppDelegate`) | `App.xaml`, `MainWindow.xaml`; application menu bar: none on `main`, new menu bar *(in progress, CUST-384)* | `ViewModels/MainWindowViewModel*` | `Services/DashboardHost`, `SingleInstanceGuard`, `SecondLaunchHandshake`, `AutostartManager`, `AppRestart`, `NotificationPolicy`, `Services/Shell/ShellShortcuts`, `ShortcutCatalog`, `ShellActions` | any; `settings` is the Settings scene | none (process and window shell) | 176, 195, 211, 224, 315, 384 *(in progress)* |
| `App/AppState.swift` (one observable app state: polling, config reload, alerts, scans, upgrades, palette, doctor) | none (state, no view) | `ViewModels/PanelViewModelBase`, `ViewModels/MainWindowViewModel*` | `Services/AppServices` (composition root), `GatewayMonitor`, `RuntimeService`, `AlertCountsService`, `RuntimeAlertsService`, `UnreadCountsService`, `ConnectorScope`, `RestartQueue`, `UpdateWatcher`, `InventoryBomStore`, `Services/Shell/StatusFacts`; `Core:Config/ConfigStore`, `Core:Runtime/RuntimeDetector`, `Core:Paths/InstallationContext` | `PanelID` is `Services/PanelCatalog` | gateway `health`, `status`, `alerts?limit=` (polled by `GatewayMonitor`); `config.yaml` (`Core:Config/ConfigChangeToken` watcher); `doctor_cache.json` | 200, 291, 308, 312 |
| `Features/MainWindow.swift` (grouped sidebar, token / update banners, first-run and palette sheets, toolbar) | `MainWindow.xaml`, `Views/Shell/AccessibleNavigation`, `Views/Shell/SidebarBadge`, `Views/Shell/UpdateBanner`, `Views/Shell/SelfUpdateBanner` | `ViewModels/MainWindowViewModel*` | `Services/PanelCatalog` (ids, groups, chords, capability gating), `Services/Shell/NavigationRequest`, `UnreadCountsService`, `AlertCountsService` | every sidebar id; group order *(in progress, CUST-383)* | badge: SQLite `audit_events` + `alert_acknowledgement_projection` (`Core:Audit/AlertQueueReader`); last panel in `settings.json` | 201, 202, 213, 265, 273, 383 *(in progress)* |
| `Features/MenuBarPopover.swift`, `MenuBarIcon` in `App/DefenseClawApp.swift`, `Assets.xcassets/MenuBarShield*` | `Views/TrayFlyoutWindow`, `Views/FlyoutPlacement` | `ViewModels/TrayFlyoutViewModel*` | `Services/TrayIconService`, `TrayShieldPresenter`, `ShieldIconFactory`, `ShieldArtwork`; `Core:Audit/ConnectorHookTotalsReader`, `Core:Audit/HookTotalsCacheStore` | opens `overview`, `alerts` and others through `OpenPanelCommand` | `GatewayMonitor` state; SQLite alert counts; hook totals from `audit_events`, cached in `%LOCALAPPDATA%\DefenseClaw.App\cache` | 201, 202, 204, 278, 279, 314 |
| `Features/CommandPaletteView.swift` | `Views/Shell/CommandPaletteControl` | none (control binds `Services/Shell/CommandPalette`) | `Services/Shell/CommandPalette`, `ShellCommandRegistry`, `CuratedCliCommands`, `PaletteRecents`, `CommandRerun`; `Core:Cli/TuiRegistry`, `Core:Cli/TuiRegistryData.Generated` | none (overlay, Ctrl+Shift+P); "Go to" rows use every panel id | runtime TUI command registry (catalogue generated by `tools/gen-tui-registry.py`); `--help` probes for gating | 224, 264, 282, 325 |
| `Features/FirstRunView.swift` | `Views/FirstRun/FirstRunWindow` | `ViewModels/FirstRun/FirstRunViewModel` | `Services/FirstRun/ConnectorOnboarding`, `InitPlanBuilder`; `Core:Setup/InitReportValidator` | none (window; opened by `MainWindowViewModel` and `SetupPanelViewModel`) | CLI `init`, `setup <connector>`; connector detection; presence of `config.yaml` | 210, 213, 373, 379 |
| **[IMPL]** `DataLayer/UpdateChecker.swift` (app self-update), `RuntimeInstaller.swift`, `RuntimeInstallFilesystem.swift` (runtime install), surfaced by `MainWindow` banners and Settings General | `Views/Updates/UpdatesWindow`, `InstallScriptUpgradeCard`, `InstallScriptReviewOverlay`, `SelfUpdateWindow`; banners above | `ViewModels/Updates/UpdatesWindowViewModel`, `UpgradeSectionViewModel`, `InstallScriptUpgradeViewModel`, `Services/Updates/SelfUpdate/SelfUpdateViewModel` | `Services/Updates/UpdateChecker`, `UpdateWatcher`, `UpgradeRunner`, `InstallScriptRunner`, `InstallScriptUpgrade`, `ProvenanceInspector`, `ChecksumsSignatureVerifier`, `Services/Updates/SelfUpdate/*`; `Core:InstallState/InstallStateDetector` | none (windows; opened from banners, Settings, Overview) | HTTPS GET of GitHub releases; verified `install.ps1`; checksum signature; runtime `.install.lock` and CAS receipts | 206, 315, 346, 359, 378 |
| **[IMPL]** `Features/CatalogViews.swift` (`CatalogCommandSheet`), `Features/SetupView.swift` (`WizardSheet` review phase): a review sheet per feature | `Views/Shell/CommandReviewControl`, `Views/Shell/GatewayActionDialog` (one shared review for every mutation) | none | `Services/Shell/CommandReview`, `CommandOutcome`, `CommandRerun`, `GatewayControl`, `GatewayVerbs`; `Core:Cli/CommandTier`, `Core:Cli/CommandNextAction` | none (overlay) | CLI only, argv shown before it runs; recorded in Activity | 173, 264, 382 |
| **[WIN]** none (the Mac has no status strip) | `Views/Shell/StatusStripControl`, `ChipStripPanel`, `ChipStripLayout` | `ViewModels/StatusStripViewModel`, `StripChip` | `Services/Shell/StatusFacts`, `StripPresentation`, `Services/PollFreshness` | chips deep-link to panels | what panels hand to `StatusFacts` (health, keys, policy, redaction) | 273 |
| **[WIN]** none (the Mac follows the system appearance) | `Views/Shell/AppearanceFlyout`, `Views/Shell/ShortcutsOverlay` | none | `Services/Appearance/*`, `Services/Shell/ShortcutCatalog`; styles in `Themes/Styles/*` | none | `appearance` section of `settings.json` | 194, 207, 224 |

### 4.2 Monitor

| Mac file(s) | Windows view(s) | View-model(s) | Services / Core types | Nav id | Data source | Issues |
|---|---|---|---|---|---|---|
| `Features/OverviewView.swift` | `Views/Panels/OverviewPanel`, `Views/Controls/HourlyBarChart`; its Runtime monitoring row opens the Setup hub's card (Section 4.5) | `ViewModels/OverviewPanelViewModel*` (22 partials), `OverviewAgents`, `OverviewDetectedConnectors`, `OverviewStatusReader`, `DoctorReconciliation`, `DurabilityAttention` | `Services/GatewayMonitor`, `Services/Observability/*` (Agent360), `Services/Durability/*`, `Services/GatewayAdmin/GatewayAdminService`, `InventoryBomStore`, `RestartQueue`; `Core:Audit/HourlyActivityReader`, `ConnectorHookTotalsReader`, `RecentAuditMetricsReader`; `Core:Observability/*`; `Core:Gateway/EffectivePolicy`; `Core:Config/OperatorAssetLists`, `AdmissionOverrides`; `Core:Gateway/Durability/*` | `overview` | gateway `health`, `status`, `alerts`, `enforce/blocked`, `enforce/allowed`, `api/v1/guardrail/profiles/resolve`; SQLite `audit_events`; `config.yaml`; `doctor_cache.json`; CLI `doctor` and reviewed buttons. The Scanners card (the Mac's `ScannerProbe`) has a Windows counterpart in the same place, fed by `ViewModels/OverviewPanelViewModel.cs` (`BuildScanners`) and `OverviewPanelViewModel.Scanners.cs`: skill-scanner and mcp-scanner by PATH and the installer's bin directory (no subprocess), aibom and codeguard built in, keys from `doctor_cache.json`, plus watcher, AI discovery and the CLI path, and a policy row when a connector is selected. The Mac's guardrail row is the Services card and the Guardrail tile, its coverage row is the AI BOM coverage card (which only reads the last AI BOM), and its symlink Fix button has no counterpart; the Sandbox card is a static "not supported on Windows" card | 205, 209, 213, 258, 272, 274, 313, 329, 348, 352, 354, 358, 360, 373, 375, 389 |
| `Features/AlertsView.swift` | `Views/Panels/AlertsPanel` | `ViewModels/AlertsPanelViewModel*` (Detail, Events, Runtime, Search), `ConnectorColumn` | `Services/AlertCountsService`, `RuntimeAlertsService`; `Core:Audit/AlertQueueReader`, `AlertDetailReader`, `AlertCounts`, `RuntimeAlertMerge`, `RuntimeAlertSnapshot`, `DetectionOnlyFindings`, `NetworkEgressReader`, `AuditCorrelationReader` | `alerts` (payload `AlertsFilter`) | SQLite `audit_events`, `alert_acknowledgement_projection`, `scan_results`, `scan_findings`, `network_egress_events`; gateway `alerts?limit=` when the database predates schema v8; CLI `alerts --json --limit 500` (runtime alert kinds); reviewed `alerts acknowledge` / `dismiss` | 201, 202, 218, 255, 261, 262, 374, 380 |
| `Features/LogsView.swift` | `Views/Panels/LogsPanel`; **[WIN]** `Views/JudgeHistory/JudgeHistoryWindow`, `JudgeHistoryView` (the Mac has no judge-response history) | `ViewModels/LogsPanelViewModel*`, `LogEntry`, `LogPresets`, `JudgeHistoryViewModel` | `Core:Logs/LogTailer`, `LogRingBuffer`, `LogLine`, `DisplayRedaction`; `Core:Audit/EventStreamReader`, `EventDetail`, `LogSignalRule`, `JudgeHistoryReader` | `logs` (payload `LogsPreset`) | files `gateway.jsonl`, `gateway.log`, `watchdog.log`; SQLite `audit_events` (Verdicts and Events streams); `judge_bodies.db` or `judge_responses` | 216, 260, 261, 262, 263, 295 |
| `Features/AuditView.swift` (`AuditView`, `AuditExportDocument`) | `Views/Panels/AuditPanel` | `ViewModels/AuditPanelViewModel*` (Actionable, Archive, Correlation, Live, Search) | `Core:Audit/AuditReader`, `AuditQuery`, `AuditQuerySearch`, `SearchQuery`, `AuditExport`, `AuditArchive`, `AuditCorrelationReader`, `AuditHeadReader`, `AuditChangeProbe`, `StructuredDetailParser` | `audit` (payload `AuditPreset`) | SQLite `audit_events`, `scan_results`; read-only archive of pre-upgrade history; CSV / JSON export to a file | 217, 261, 262, 284, 299, 378 |
| `Features/ActivityView.swift` (Commands and Mutations tabs) | `Views/Panels/ActivityPanel`, `Views/Panels/ActivityMutationsView`, `Views/Controls/DcCommandOutput` | `ViewModels/ActivityPanelViewModel`, `ActivityMutations`, `ActivityMutationsViewModel`, `ActivityTranscript` | `Services/CommandActivity`, `Services/Shell/CommandRerun`, `CommandOutcome`; `Core:Cli/CliRunner` (activity list), `Core:Audit/MutationReader` | `activity` | in-process history of CLI runs (Commands); SQLite `activity_events` (Mutations, before / after diff) plus `compliance.activity` and `enforcement.action` rows of `audit_events` | 175, 178, 193, 219, 221, 264 |

### 4.3 Govern

| Mac file(s) | Windows view(s) | View-model(s) | Services / Core types | Nav id | Data source | Issues |
|---|---|---|---|---|---|---|
| `Features/CatalogViews.swift`: `SkillsView` | `Views/Panels/SkillsPanel` | `ViewModels/SkillsPanelViewModel` | shared base below | `skills` | CLI `skill list --json`; reviewed `skill scan`, `install`, `block`, `allow`, `quarantine`, `restore`, `disable`, `enable`, `info` | 212, 214, 276, 283 |
| `CatalogViews.swift`: `MCPsView`, `MCPSetSheet` | `Views/Panels/McpsPanel` | `ViewModels/McpsPanelViewModel` | shared base below | `mcps` | CLI `mcp list --json`; reviewed `mcp set` and the verbs above | 212, 214, 276, 283, 378 |
| `CatalogViews.swift`: `PluginsView`, `CatalogInstallSheet` | `Views/Panels/PluginsPanel` | `ViewModels/PluginsPanelViewModel` | shared base below | `plugins` | CLI `plugin list --json`; reviewed `plugin install` and the verbs above | 212, 214, 276, 283 |
| `CatalogViews.swift`: `ToolsView` | `Views/Panels/ToolsPanel` | `ViewModels/ToolsPanelViewModel`, `ToolsPanelViewModel.OperatorLists` | shared base below; `Core:Config/OperatorAssetLists` | `tools` | CLI `tool list --json`, `tool status`; operator allow / block lists read from the text of `config.yaml` | 214, 283, 375 |
| `CatalogViews.swift`: `CatalogListScaffold`, `CatalogContainer`, `CatalogActionMenu`, `CatalogCommandSheet`, status and scan labels, `RefreshButton` | `Views/Panels/Govern/GovernTable`, `GovernConfirmOverlay`, `GovernOutputCard`, `GovernPanelKeys`, `ChangeTipConverter`, `FillHeightConverter`; `Views/Controls/DcRowMenu`, `DcInspector`, `DcPageToolbar` | `ViewModels/Govern/GovernPanelViewModelBase`, `GovernRow`, `GovernJson`, `CatalogTrust` | `Core:Cli/CatalogPartialRead`, `Core:Gateway/Models/GovernModels`, `Core:Config/RegistryAttribution` | (shared by the four rows above) | the Mac reads gateway catalogs then falls back to the filesystem (`SkillScanner`); Windows reads the CLI only and refuses changes on stale or untrusted data | 173, 214, 283, 312 |
| `Features/PoliciesView.swift` | `Views/Panels/PoliciesPanel`, `Views/Panels/PolicyModelView` | `ViewModels/PoliciesPanelViewModel*`, `PolicyFormViewModel`, `PolicyModelViewModel*`, `PolicyModelItems`, `PolicyRow` | `Core:Policy/PolicyBackend`, `PolicyModels`, `PolicyCreate`, `PolicyEdit`, `PolicyDiff`, `PolicyTextParser`; `Core:Policy/Model/*` (`PolicyModelReader`, `PolicyCatalog`, `PolicyCatalogJson`, `PolicyDataFiles`, `PolicyIntents`, `PolicyPostureComposer`, `PackRepin`) | `policies`; group Configure today, **Govern after `tools`** *(in progress, CUST-383)* | CLI `policy list`, `show`, `validate`, `test`, `activate`, `create`, `edit`, `delete` (0.8.10 surface); with `RuntimeCapability.PolicyModel` the runtime's policy catalog JSON plus `policies\` data files; reviewed `guardrail use-pack --repin` | 281, 293, 312, 378, 383 *(in progress)* |
| `Features/SandboxesView.swift` (`SandboxesView`, `SandboxUnblockMenu`, `SandboxAskRow`, `SandboxMenuSection`, `SandboxOverviewCard`) | none on `main`; **new non-executing Sandboxes page** *(in progress, CUST-383)*; until then the Overview Sandbox card | *(new, CUST-383)*; today `ViewModels/OverviewPanelViewModel.Services.cs` | `Core:Runtime/RuntimeCapability` (`Sandbox`, never offered on Windows), `Services/Wizards/WizardWindowsPolicy` (hides sandbox commands) | `sandboxes` *(proposed id; confirm in CUST-383's `PanelCatalog.cs`)*, Govern group | Mac: daemon `/api/v1/sandbox` and `sandbox` CLI verbs. Windows: none, an honest "OpenShell sandboxes run on Linux and macOS only" | 296 (canceled), 313, 383 *(in progress)* |

### 4.4 Discover

| Mac file(s) | Windows view(s) | View-model(s) | Services / Core types | Nav id | Data source | Issues |
|---|---|---|---|---|---|---|
| `Features/DiscoverViews.swift`: `InventoryView` | `Views/Panels/InventoryPanel` | `ViewModels/InventoryPanelViewModel*` (Bom, IdeInventory), `InventoryBomBrowser*` | `Services/InventoryBomStore`; `Core:Inventory/InventoryReader`, `InventoryBomSnapshot`, `InventoryScan`, `IdeInventory` | `inventory` | SQLite `ai_signals`, `ai_scans`, `ai_confidence_snapshots` in `inventory.db`; CLI `aibom scan --json` (BOM tabs); gateway `api/v1/ai-usage/ide-plugins`, `api/v1/agents/identities`. No automatic scan on page open (deliberate) | 175, 177, 178, 275, 376, 381 |
| `DiscoverViews.swift`: `AIDiscoveryView` | `Views/Panels/AiDiscoveryPanel`, `Views/Panels/AiDiscoveryTuningDialog` (**[IMPL]** Mac uses a wizard sheet) | `ViewModels/AiDiscoveryPanelViewModel*` (AddConnector, Coverage, Models, Navigation, Tuning), `AiDiscoveryTuningViewModel`, `AiDiscoveryModelRows`, `AiDiscoveryNovelty`, `AiDiscoveryProvenance`, `AiDiscoverySignals`, `AiDiscoveryUsage` | `Services/AiDiscoveryHead`, `UnreadCountsService`; `Core:Inventory/InventoryReader`, `Core:Setup/AiDiscoveryTuning` | `ai-discovery` | gateway `api/v1/ai-usage` and `api/v1/ai-usage/runtime`; files `ai_discovery_state.json`, `agent_discovery.json`; SQLite `ai_signals`; CLI `agent discovery status --json`, `enable`, `disable`, `scan` | 177, 265, 271, 277, 292, 310, 329 |
| `Features/LocalModelsView.swift` (a tab of AI Discovery, not a panel) | the Models mode of `Views/Panels/AiDiscoveryPanel` | `ViewModels/AiDiscoveryPanelViewModel.Models`, `AiDiscoveryModelRows` | as AI Discovery | `ai-discovery` | as AI Discovery (model owner, relevance, confidence, lineage from `api/v1/ai-usage`) | 277, 310 |
| `Features/AIRuntimeView.swift` | `Views/Panels/AiRuntimePanel` | `ViewModels/AiRuntimePanelViewModel*` (Actions, PlaneC, Prerequisites), `AiRuntimeRows` | `Services/WindowsPlaneCProbes`; `Core:AiRuntime/AiRuntimeReader`, `AiRuntimeSnapshot`, `AiRuntimeJson`, `AiRuntimeCommands`, `AiRuntimePermissions`, `PlaneCReadiness`, `RuntimeMonitoring` | `ai-runtime` (gated on `RuntimeCapability.AiRuntime`) | gateway `api/v1/ai-usage/runtime`; CLI `agent discovery runtime status`, `scan`, `findings`, `permissions` | 309, 312, 324 |
| `DiscoverViews.swift`: `RegistriesView`, `AddRegistrySourceSheet` | `Views/Panels/RegistriesPanel` | `ViewModels/RegistriesPanelViewModel*` (Actions, Entries), `RegistryIndexInfo` | `Core:Config/RegistryAttribution` | `registries` (payload `RegistryFocus`) | CLI `registry list --json`; reviewed `registry add`, `edit`, `remove`, `sync`, `approve`, `reject`, `require`; registry sources in `config.yaml` | 193, 276, 283, 312 |
| **[WIN]** none | `Views/Panels/AgentsPanel` | `ViewModels/AgentsPanelViewModel*` (Focus), `AgentsRows` | `Core:Correlation/CorrelationService`, `CorrelationReader`, `CorrelationRoutes`, `AgentTree`, `AgentTimeline`, `CorrelationModels` | `agents` (gated on `RuntimeCapability.CorrelationApi`; payload `AgentFocus`); stays after Discover as a Windows extension *(CUST-383)*, today between `ai-runtime` and `registries` | gateway `api/v1/correlation/graph`, `explain`, `timeline`, `conflicts`, `api/v1/agents/identities` | 354, 355, 381 |

### 4.5 Configure and Settings

| Mac file(s) | Windows view(s) | View-model(s) | Services / Core types | Nav id | Data source | Issues |
|---|---|---|---|---|---|---|
| `Features/SetupView.swift`: `SetupView` (area grid, Redaction button, Config Editor section) | `Views/Panels/SetupPanel`; **[IMPL]** dialogs for what the Mac does as wizard sheets: `Views/Panels/NotificationRoutingDialog`, `ConnectorBatchDialog`; the Runtime monitoring card `Views/Panels/RuntimeMonitoringDialog` (opened from Overview); `Views/Panels/CredentialEntryBox`. Target: area grid first, inline config editor sharing one draft *(in progress, CUST-385)* | `ViewModels/SetupPanelViewModel*` (Restart), `WizardCardStates`, `WizardTileIcons`, `SetupPlan`, `SetupDialogViewModel`, `ConnectorBatchViewModel`, `NotificationRoutingViewModel`, `RuntimeMonitoringViewModel`, `ReadinessViewModel`, `SetupReadiness`, `CredentialsViewModel*`, `CredentialRowViewModel.Entry`, `CredentialTerminal`, `RestartQueueReview` | `Services/RestartQueue`, `Services/Wizards/WizardCatalog`, `Services/Shell/SplunkDashboardsCommands`; `Core:Setup/*` (`NotificationRouting`, `ConnectorBatch`, `SetupStateLines`, `SetupJson`) | `setup` | CLI `setup --help` probe builds the tiles; reviewed `setup <target> ...`, `setup notifications-set` | 225, 266, 267, 269, 270, 271, 311, 317, 328, 385 *(in progress)* |
| `SetupView.swift`: `WizardSheet`; `Features/SetupDefinitions.swift` (static `TUIWizards`, `SetupSecretTransportPolicy`) | `Views/Wizards/WizardWindow`, `ModelPickerBox`, `SecretEntryBox` | `ViewModels/Wizards/WizardViewModel*`, `WizardFieldViewModel*`, `WizardGoalsViewModel`, `WizardGuideViewModel` | **[IMPL]** Windows derives the definitions from the runtime, the Mac hard-codes them: `Services/Wizards/WizardCatalog`, `SetupHelpProbe`, `SetupHelpParser`, `SetupHelpDiskCache`, `WizardDefinition*`, `WizardStepFactory*`, `WizardGoals*`, `WizardWindowsPolicy`, `SecretRoutes`, `WizardLauncher` | none (window) | CLI `setup --help` and `setup <target> --help` (cached on disk with an integrity check); reviewed `setup <target>` with secrets typed at a pseudo-console, never on argv | 179, 269, 271, 292, 311, 317, 328, 379 |
| `SetupView.swift`: `ConfigEditorView`, `ConfigDiffSheet`; `Features/ConfigEditorDefinitions.swift` | `Views/ConfigEditor/ConfigEditorWindow`, `UnsavedChangesDialog`, `FormFieldTemplateSelector`, `ConfigEditorConverters`, `YamlHighlighting`. Target: inline in Setup *(in progress, CUST-385)* | `ViewModels/ConfigEditor/*` (`ConfigEditorWindowViewModel*`, `ConfigFormBuilder`, `ConfigFieldCatalog`, `ConfigFieldValidator`, `ConfigDiffReview`, `ConfigDraftRebase`, `ConfigSaveService`, `YamlSectionEditor`) | `Core:Config/ConfigDocument`, `ConfigStore`, `ConfigYamlReader`, `ConfigYamlGuard`, `ConfigReloadRules`; **[IMPL]** the field catalogue is static plus runtime notes, not a Python dump (`DynamicConfigCatalog`) | none (window; palette "Open config editor") | `config.yaml` (raw and typed form), `.env`; backed-up atomic save, then a reviewed gateway restart | 174, 220, 268, 291, 377, 385 *(in progress)* |
| `Features/RedactionPolicyView.swift` (`RedactionPolicySheet`, `RedactionAdvancedEditor`) | `Views/Redaction/RedactionWindow`, `RedactionView` | `ViewModels/Redaction/*` (`RedactionViewModel*`, `RedactionFormViewModel`) | `Core:Redaction/RedactionArgv`, `RedactionOperation`, `RedactionReadings`, `RedactionStatus`, `RedactionVocabulary` | none (window; Setup tile and a Logs link; gated on `RuntimeCapability.RedactionAdvanced`) | CLI `setup redaction ...` (read and reviewed apply) | 295 |
| *(Mac: guardrail and trusted-path wizards in `SetupDefinitions.swift`)* **[IMPL]** | `Views/Guardrail/GuardrailControlsWindow`, `GuardrailControlsView`; `Views/SetupResources/SetupResourceWindow`, `SetupResourceView` (observability destinations, webhooks, trusted paths) | `ViewModels/GuardrailControlsViewModel`; `ViewModels/SetupResources/*` | `Services/Guardrail/GuardrailControlArgv`, `GuardrailReadings`; `Core:Setup/SetupResourceArgv`, `ObservabilityDestinations`, `NotifierWebhooks`, `TrustedPaths` | none (windows opened from Setup tiles) | CLI `guardrail hilt`, `block-message`, `judge`; `setup <resource> list --json`, `add`, `remove` | 222, 225, 270 |
| `Features/AppSettingsView.swift` (Settings scene: General, Monitoring, Notifications, Connection; `RuntimeSourceSwitchButton`) | `Views/Panels/SettingsPanel`, `Views/Panels/SettingsRow`. Target: General / Monitoring / Notifications / Connection, file roles Config, Data, Environment, Audit DB, Virtual environment, Gateway log *(in progress, CUST-386)* | `ViewModels/SettingsPanelViewModel*` (Advanced, Archive, Installation), `SettingsPathRow`, `SettingsPlatform`, `GatewayAdminToggleViewModel` | `Services/Settings/AppSettings`, `AppSettingsStore`, `AppSettingsCodec`; `Services/AutostartManager`; `Core:Runtime/RuntimeSelection` | `settings` (footer panel, Ctrl+,) | `%LOCALAPPDATA%\DefenseClaw.App\settings.json` only; read-only facts from `Core:Paths/DefenseClawPaths`; update state from `Services/Updates` | 203, 206, 211, 291, 299, 315, 348, 386 *(in progress)* |

### 4.6 Mac files with no Windows counterpart, and Mac assets

| Mac file(s) | Why there is no Windows file | Nearest Windows behaviour |
|---|---|---|
| **[MAC]** `Features/SandboxInspectorView.swift`, `DataLayer/SandboxInspection.swift`, `SandboxInspectionStore.swift`, `SandboxModels.swift`, `SandboxNotifications.swift` | OpenShell sandboxes run on Linux and macOS only; CUST-296 (Windows sandboxes) was canceled | The explanatory Sandboxes page *(in progress, CUST-383)*; Overview Sandbox card; `RuntimeCapability.Sandbox` is never offered on Windows |
| **[MAC]** `DataLayer/SkillScanner.swift` (filesystem skill and MCP catalogs when the gateway catalogs are unavailable) | Windows reads `skill list --json`, `mcp list --json` from the CLI, which already does the per-connector discovery | `ViewModels/Govern/GovernPanelViewModelBase` |
| `Resources/policy_catalog_bridge.py` (Python bridge that projects the runtime's policy model) | Windows runs no embedded Python; it parses the `policy` CLI JSON and the data files | `Core:Policy/Model/PolicyCatalogJson`, `PolicyModelReader` |
| `Assets.xcassets/AppIcon.appiconset`, `MenuBarShield*.imageset` | Windows draws the shield from code and generates the one file icon | `Assets/DefenseClaw.ico`, `Services/ShieldArtwork`, `ShieldIconFactory` |


## 5. DataLayer to DefenseClaw.Core mapping

`DefenseClaw.Core` is the data and runtime boundary (the Mac's `DataLayer`); app-level services that need the UI process live in `DefenseClaw.App/Services`. **Map, do not duplicate:** a Windows change reuses the type named here, it does not add a second reader, a second settings store, a second config-draft session or a second file watcher.

| Mac `DataLayer/` file(s) | Windows counterpart | Difference to know |
|---|---|---|
| `GatewayClient.swift` | `Core:Gateway/GatewayClient`, `GatewayPeerVerifier`, `GatewayResult`, `Core:Gateway/Models/*`; `Services/GatewayMonitor`, `GatewaySnapshotSource` | Windows is GET-only: the Mac's POSTs (enforce block / allow, scan, policy reload, sandbox) are reviewed CLI argv here. The bearer token goes only to a peer verified as the gateway (CUST-247, CUST-249) |
| `GatewayAdminProtocol.swift`, `GatewayAdministratorClient.swift` (XPC privileged helper: start / stop / restart) | **[IMPL]** `Admin:AdminHelper`, `Admin:OwnershipRepair`, `AdminContract:AdminVerbs`, `AdminLayout`, `AdminAcl`, `AdminCopyInspector`; `Services/GatewayAdmin/*`, `Services/GatewayElevationStatus`, `ViewModels/GatewayAdminToggleViewModel` | Windows runs an elevated helper process with fixed verbs, an ACL policy, a copy inspector and an NTFS ownership repair (CUST-348); the Mac helper is an XPC service that authorizes each start / stop / restart |
| `GatewayAutoStartCoordinator.swift` | `Services/GatewayAutoStart`, `Services/Shell/GatewayLifecycle`, `GatewayControl`, `GatewayVerbs` | Setting is `startup.gatewayAutoStart` in `settings.json` |
| `CLIRunner.swift` | `Core:Cli/CliRunner`, `CliRunner.Pty`, `CliInvocation`, `CliWorkingDirectory`, `ChildEnvironment`, `BoundedLineReader`, `ArgvHazards`, `PowerShellQuoting`, `CommandTier` | `WindowsJob` replaces the Mac's process-group launcher; secrets are typed at a pseudo-console (`PseudoConsole`, `PtyConversation`, `SecretPtyRunner`, `VtScreen`), never on argv |
| `CommandRegistry.swift` | `Core:Cli/TuiRegistry`, `TuiRegistryData.Generated` (from `tools/gen-tui-registry.py`); `Services/Shell/CuratedCliCommands` | Catalogue is generated at build time from the runtime's own registry |
| `CommandActivityStore.swift` | `Services/CommandActivity`; the activity list in `Core:Cli/CliRunner`; `Core:Audit/MutationReader` | In-process, capped list; mutations come from the database |
| `CatalogCLI.swift` | `ViewModels/Govern/GovernPanelViewModelBase`, `GovernJson`, `CatalogTrust`; `Core:Cli/CatalogPartialRead`; `Core:Gateway/Models/GovernModels` | Partial reads show rows with a warning; stale or failed data never authorizes a change (CUST-283, CUST-312) |
| `ConfigStore.swift` | `Core:Config/ConfigStore`, `ConfigDocument`, `ConfigYamlReader`, `ConfigYamlFlatten`, `ConfigYamlGuard`, `DefenseClawConfig`, `ConfigChangeToken`, `ConfigDiskSignature`, `ConfigReloadRules`, `DotEnvFile`, `TokenResolver`, `SecretValue` | Every YamlDotNet entry goes through `ConfigYamlGuard` (CUST-341); size and depth are capped (CUST-250) |
| `DynamicConfigCatalog.swift` | **[IMPL]** `ViewModels/ConfigEditor/ConfigFieldCatalog`, `ConfigRuntimeNotes`, `ConfigFormBuilder` | Static catalogue with runtime gating; no Python dump of the runtime's catalog |
| `ConnectorOnboarding.swift` | `Services/FirstRun/ConnectorOnboarding`, `InitPlanBuilder`; `Core:Setup/ConnectorBatch` | |
| `DoctorCache.swift` | `ViewModels/DoctorReconciliation`; path `Core:Paths/DefenseClawPaths` (`DoctorCachePath`) | Read-only; a STALE doctor result is reconciled against the config (CUST-213) |
| `EventStreamReader.swift` | `Core:Audit/EventStreamReader`, `EventDetail`, `LogSignalRule`; `Core:Logs/LogTailer`, `LogRingBuffer`, `LogLine`, `DisplayRedaction` | |
| `AuditStore.swift` | `Core:Audit/AuditReader`, `AuditQuery`, `AuditQuerySearch`, `SearchQuery`, `AuditEvent`, `AuditSeverity`, `AuditTargetType`, `AlertQueueReader`, `AlertDetailReader`, `AlertCounts`, `AuditExport`, `AuditArchive`, `ReadOnlyQuery`, `ReaderOffload`, `ReaderTimeouts`, `SnapshotMemo`, `AuditChangeProbe`, `HourlyActivityReader`, `ConnectorHookTotalsReader` | All reads are read-only, off the UI thread, with timeouts; an unchanged snapshot is not read twice (CUST-284) |
| `InstallationContext.swift` | `Core:Paths/InstallationContext`, `DefenseClawPaths`; `Core:Cli/InstallationGate`; `Services/InstallationGuard` | A managed or invalid installation is read-only (CUST-308) |
| `Models.swift` (shared model types) | split by owner: `Core:Audit/AuditEvent`, `Core:Gateway/Models/*`, `Core:Config/DefenseClawConfig`, `Core:Inventory/*`, row types beside each view-model | There is no single model file on Windows |
| `InventoryOutputParser.swift`, `RuntimeV1Inventory.swift` | `Core:Inventory/InventoryBomSnapshot`, `IdeInventory`, `InventoryScan`, `InventoryReader` | |
| `RuntimeV1Alerts.swift` | `Core:Audit/RuntimeAlertMerge`, `RuntimeAlertSnapshot`; `Services/RuntimeAlertsService` | |
| `OperatorAssetLists.swift`, `OperatorAssetListsConfig.swift` | `Core:Config/OperatorAssetLists`, `AdmissionOverrides` | |
| `PolicyCatalog.swift` (+ `Resources/policy_catalog_bridge.py`) | `Core:Policy/PolicyBackend`, `PolicyModels`; `Core:Policy/Model/*` | No Python bridge; the model is parsed from CLI JSON and policy data files |
| `RedactionCommands.swift` | `Core:Redaction/RedactionArgv`, `RedactionOperation`, `RedactionReadings`, `RedactionResult`, `RedactionStatus`, `RedactionVocabulary` | |
| `RegistryStore.swift` | **[IMPL]** `Core:Config/RegistryAttribution`; reads through `registry ... --json` in `ViewModels/RegistriesPanelViewModel*` | Windows does not read registry index files itself, so it has no per-index byte and entry limits to keep |
| `RuntimeInstaller.swift`, `RuntimeInstallFilesystem.swift` | **[IMPL]** `Services/Updates/UpgradeRunner`, `InstallScriptRunner`, `InstallScriptUpgrade`, `InstallState`, `ProvenanceInspector`, `ChecksumsSignatureVerifier`; `Core:InstallState/InstallStateDetector`, `Core:Install/InterruptedInstallLock`, `Core:Gateway/Durability/*` | Windows delegates the install to the verified `install.ps1` |
| `UpdateChecker.swift` | `Services/Updates/UpdateChecker`, `UpdateWatcher` (runtime releases); `Services/Updates/SelfUpdate/*` (this app's releases) | |
| `StructuredDetailParser.swift` | `Core:Audit/StructuredDetailParser` | |
| `AIRuntimeModels.swift` | `Core:AiRuntime/AiRuntimeSnapshot`, `AiRuntimeJson`, `AiRuntimeReader` | |
| `AIDiscoveryActionPolicy.swift` | no standalone type; Enable and Scan are separate reviewed commands in `ViewModels/AiDiscoveryPanelViewModel*` | |
| `AlertDispositionCommand.swift` | argv built in `ViewModels/AlertsPanelViewModel*` (`alerts acknowledge` / `dismiss`) | Windows previews a bulk dismiss with `--dry-run` before applying |
| `ScannerProbe.swift` | `ViewModels/OverviewPanelViewModel` (`BuildScanners`, `ExecutableRow`), `OverviewPanelViewModel.Scanners.cs` (aibom, keys); the PATH lookup is `Core:Paths/DefenseClawPaths.FindExecutableAsync` | Reads files and environment only, as the Mac's does. Windows searches PATH and the installer's bin directory and has no symlink Fix; the keys row follows the doctor cache the Doctor card holds; scanner mode also shows as a Configuration row |
| `SkillScanner.swift`, `Sandbox*.swift` | none (**[MAC]**, Section 4.6) | |

## 6. DesignSystem to Themes and Controls

| Mac `DesignSystem/` | Windows | Notes |
|---|---|---|
| `CiscoTheme.swift` (`Cisco` colours, `Color` extension) | `Themes/Styles/Default.xaml`, `Cisco.Dark.xaml`, `Cisco.Light.xaml`, `Linear.*.xaml`, `Tui.*.xaml` (token values); `Themes/DefenseClaw.xaml` (the `Dc*` styles that consume tokens); `Services/Appearance/AppearanceModel`, `AppearanceService`, `AppearanceSettingsStore`, `WpfUiBridge` | **[WIN]** four switchable styles; the Cisco style is the Mac's look (CUST-207). Tokens are consumed with `DynamicResource` so a style change applies live |
| `Components.swift`: `SeverityBadge`, `StatePill`, `StaleBadge` | `Views/Controls/DcBadges` (`DcSeverityBadge`, `DcStatePill`, `DcStatusLabel`), `DcGlyphs` (`DcStatusGlyph`, `DcSeverityGlyph`) | |
| `Components.swift`: `StatCard`, `DCCard`, `InteractiveCardButtonStyle`, `KeyValueGrid`, `DCEmptyState` | `Themes/DefenseClaw.xaml` (`DcCard*`, `DcValue`, `DcTileLabel`, `DcField*`, `DcEmptyState`), `Views/Controls/DcCardHeader` | |
| `Components.swift`: `ConnectorFilterChip`, `FilterChipRow` | `Views/Controls/DcConnectorScopeChip`, `DcSegmented`; `Services/ConnectorScope` | One connector scope shared by every panel (CUST-223) |
| `Components.swift`: `DiffView` | `Core:Text/LineDiff`; diff blocks in `Views/ConfigEditor/ConfigEditorWindow`, `Views/Panels/ActivityMutationsView` | |
| `Components.swift`: `ConfidenceGauge` | no counterpart: confidence is a text column and an accessible label in `Views/Panels/AiDiscoveryPanel` | |
| `InspectorLayoutPolicy.swift` (`dcInspector`) | `Views/Controls/DcInspector` | |
| `QuickHoverHelp.swift` | tooltips and `AutomationProperties` set in each view; `DcCellToolTip` in `Views/Controls/DcRowMenu` | The Mac needs a monitor to re-add toolbar help; WPF tooltips do not |
| *(none)* | **[WIN]** `Views/Controls/DcPageToolbar`, `DcRowMenu` (`DcClipboard`), `DcGridColumns`, `DcSpinner`, `DcSymbolIcon`, `DcIcon`, `DcCommandOutput`, `HourlyBarChart`; `Themes/Controls.xaml`; panel layout helpers `Views/Panels/ColumnSizing`, `CompactLayout`, `NestedScroll`, `TranscriptList` | Fluent / WPF-UI parts the SwiftUI toolkit supplies natively |

## 7. Who has what

| Class | Surfaces |
|---|---|
| **[WIN]** only on Windows | Agents panel and its correlation reader; status strip; appearance styles and flyout; shortcuts overlay; "new since last visit" capsules; judge-response history window; Agent360 deep links (`Services/Observability/Agent360Link`, `Agent360Launcher`, `Agent360Availability`) |
| **[IMPL]** both platforms, built differently | Privileged gateway helper (Mac XPC, Windows elevated process with ownership repair); runtime install (Mac installer, Windows `install.ps1`); app self-update (Mac `UpdateChecker`, Windows `Services/Updates/SelfUpdate/*`); wizard definitions (Mac static, Windows parsed from the runtime); config-field catalogue; registry reads; setup dialogs for notifications, connector batch, AI Discovery tuning, guardrail and list editors (Mac: wizard sheets); the review step (Mac: a sheet per feature, Windows: one shared control) |
| **[MAC]** only on the Mac | Sandbox inspector and the sandbox data layer; skill / MCP filesystem fallback; `policy_catalog_bridge.py`; the Scanners card's symlink Fix button (the card itself exists on both) |
| Windows target state, in flight | Sandboxes page, Policies in Govern (CUST-383); application menu bar (CUST-384); Setup area grid and inline config editor (CUST-385); Settings tabs and file roles (CUST-386) |


## 8. Proposed wave-2 folder move (not executed)

**Status: proposal only.** No file has been moved. Wave 2 starts after CUST-383, 384, 385 and 386 merge, because they add, edit and re-home files in Shell, Setup and Settings. At 45e6f8f, **324 of the 469 tracked files under `DefenseClaw.App/` move**; the other 145 stay (`Services/` 143, `Assets/` 1, `DefenseClaw.App.csproj` 1). No `Core`, test, installer or CI file moves.

### 8.1 Principles

- Pure `git mv` commits. File names, class names, `x:Class` and **namespaces do not change** (Section 8.6 says why). Only paths change.
- Re-derive the file list from the rule table (8.4) against merged `main` before starting: every tracked file under `DefenseClaw.App/` must match exactly one rule (no unmapped, no two files with the same destination). Files that CUST-383..386 add (`Sandboxes*`, `Settings*`, `Setup*`, anything under `Views/Shell/`) are caught by prefix rules.
- Each commit builds warning-clean and passes both suites with `--filter "Category!=Live"` before the next starts. Subject form: `Layout: <what> (CUST-387)`.
- Feature folders hold a surface's views and view-models together. `Services/` stays where it is: it is the Windows counterpart of the Mac `DataLayer`, shared by many surfaces and rooted at `AppServices`.

### 8.2 Target tree (file counts at 45e6f8f)

| Folder | Files | Holds |
|---|---|---|
| `DefenseClaw.App/App/` | 3 | `App.xaml`, `App.xaml.cs`, `AssemblyInfo.cs` |
| `DefenseClaw.App/DesignSystem/` | 27 | `Themes/` 9 (with `Styles/`), `Controls/` 14, `Layout/` 4 (`ColumnSizing`, `CompactLayout`, `NestedScroll`, `TranscriptList`) |
| `Features/Overview/` | 29 | Overview panel and its 21 view-model partials |
| `Features/Alerts/` | 7 | |
| `Features/Logs/` | 13 | Logs and the judge-history window |
| `Features/Audit/` | 8 | |
| `Features/Activity/` | 8 | Commands and Mutations |
| `Features/Catalogs/` | 25 | Skills, MCPs, Plugins, Tools and the shared Govern table, overlay and base view-model |
| `Features/Policies/` | 11 | |
| `Features/Sandboxes/` | 0 | created by CUST-383 |
| `Features/Discover/` | 39 | `Inventory/` 7, `AiDiscovery/` 16, `Runtime/` 7, `Registries/` 6, shared review overlay 3 |
| `Features/Agents/` | 5 | Windows extension |
| `Features/Setup/` | 92 | root 26, `Wizards/` 16, `ConfigEditor/` 21, `Redaction/` 11, `Guardrail/` 5, `SetupResources/` 10, `FirstRun/` 3 |
| `Features/Settings/` | 10 | |
| `Features/Shell/` | 47 | root 31 (main window, palette, strip, review, appearance, banners, shell view-models), `Tray/` 5, `Updates/` 11 |
| `Services/`, `Assets/`, `DefenseClaw.App.csproj` | 145 | stay |

### 8.3 Commit sequence

| # | Commit | Files moved | Other edits in the same commit | Gate |
|---|---|---|---|---|
| C1 | `Tests: find DefenseClaw.App sources by name, not by folder (CUST-387)` | 0 | New `DefenseClaw.App.Tests/TestSupport/AppSource.cs` (anchor: `DefenseClaw.App/DefenseClaw.App.csproj`; `Find(fileName)` throws on zero or several matches). The 12 test files in 8.7 stop hard-coding `Views\...`, `Themes\...` and `MainWindow.xaml` | Both suites green; no production file touched |
| C2 | `Layout: DesignSystem folder (CUST-387)` | 27 | `App.xaml:32`, `Services/Appearance/AppearanceModel.cs:132`, `TestSupport/UiThread.cs:121`, `AppearanceCiscoTests.cs:38-39` (8.7) | Appearance and PageChrome tests and every offscreen render test (they merge the theme by pack URI) |
| C3 | `Layout: Overview feature folder (CUST-387)` | 29 | none | Overview tests |
| C4 | `Layout: Alerts, Logs, Audit and Activity feature folders (CUST-387)` | 36 | none | Alerts, Logs, Audit, Activity tests |
| C5 | `Layout: Catalogs, Policies and Sandboxes feature folders (CUST-387)` | 36 | 4 `GovernTable.xaml` pack URIs (8.7) | Govern and Policies tests; a render of each of the four catalog panels |
| C6 | `Layout: Discover and Agents feature folders (CUST-387)` | 44 | none (`PlaneCReadinessPanelTests` already resolved by name in C1) | Discovery, Inventory, Runtime, Agents tests |
| C7 | `Layout: Shell feature folder, tray included (CUST-387)` | 36 | none | Shell, Monitoring, Icons tests |
| C8 | `Layout: Updates windows into Shell (CUST-387)` | 11 | `DefenseClaw.Tests/UpdateClaimsTests.cs:32,34,35,76` | UpdateClaims, Updates tests |
| C9 | `Layout: Setup feature folder, root files (CUST-387)` | 26 | none | Setup tests |
| C10 | `Layout: Setup wizards (CUST-387)` | 16 | none | Wizard tests |
| C11 | `Layout: Setup config editor (CUST-387)` | 21 | none | ConfigEditor tests |
| C12 | `Layout: Setup redaction, guardrail, list editors and first run (CUST-387)` | 29 | `DefenseClaw.Tests/UpdateClaimsTests.cs:33,84` | Setup, SetupResources, Redaction tests |
| C13 | `Layout: Settings feature folder (CUST-387)` | 10 | none | Settings tests |
| C14 | `Layout: App folder (CUST-387)` | 3 | none (`App.xaml` already uses the absolute pack URI from C2) | Build only: it compiles `App.xaml`; no test instantiates `App` |
| C15 | `Docs: refresh path references after the layout move (CUST-387)` | 0 | `docs/PARITY-FOUNDATIONS.md` path lists, about a dozen comments that name `Themes\`, `Views\` or `ViewModels\`, and Sections 2.1 and 8 of this file | Build only |

Order rationale: C1 first so no later commit edits a test for a path. C2 next because it holds the only global URIs. Surfaces that the in-flight issues do not touch go first; Shell, Setup and Settings go last; `App/` goes last because `App.xaml.cs` is the file everyone edits.

### 8.4 Rule table (first match wins; paths relative to `DefenseClaw.App/`)

| Rule (source) | Destination | Commit |
|---|---|---|
| `Themes/**` | `DesignSystem/Themes/**` (keeps `Styles/`) | C2 |
| `Views/Controls/**` | `DesignSystem/Controls/` | C2 |
| `Views/Panels/{ColumnSizing,CompactLayout,NestedScroll,TranscriptList}.cs` | `DesignSystem/Layout/` | C2 |
| `Views/Panels/OverviewPanel.*`; `ViewModels/Overview*.cs`, `DoctorReconciliation.cs`, `DurabilityAttention.cs` | `Features/Overview/` | C3 |
| `Views/Panels/AlertsPanel.*`; `ViewModels/AlertsPanelViewModel*.cs` | `Features/Alerts/` | C4 |
| `Views/Panels/LogsPanel.*`; `ViewModels/{LogEntry,LogPresets,LogsPanelViewModel*,JudgeHistoryViewModel}.cs`; `Views/JudgeHistory/**` | `Features/Logs/` | C4 |
| `Views/Panels/AuditPanel.*`; `ViewModels/AuditPanelViewModel*.cs` | `Features/Audit/` | C4 |
| `Views/Panels/Activity{Panel,MutationsView}.*`; `ViewModels/Activity*.cs` | `Features/Activity/` | C4 |
| `Views/Panels/{Skills,Mcps,Plugins,Tools}Panel.*`; `Views/Panels/Govern/**`; `ViewModels/{Skills,Mcps,Plugins,Tools}PanelViewModel*.cs`; `ViewModels/Govern/**` | `Features/Catalogs/` (Govern sub-folders flattened) | C5 |
| `Views/Panels/Polic*.*`; `ViewModels/Polic*.cs` | `Features/Policies/` | C5 |
| `Views/Panels/Sandbox*.*`; `ViewModels/Sandbox*.cs` (none at 45e6f8f; CUST-383 adds them) | `Features/Sandboxes/` | C5 |
| `Views/Panels/InventoryPanel.*`; `ViewModels/Inventory*.cs` | `Features/Discover/Inventory/` | C6 |
| `Views/Panels/AiDiscovery*.*`; `ViewModels/AiDiscovery*.cs` | `Features/Discover/AiDiscovery/` | C6 |
| `Views/Panels/AiRuntimePanel.*`; `ViewModels/AiRuntime*.cs` | `Features/Discover/Runtime/` | C6 |
| `Views/Panels/RegistriesPanel.*`; `ViewModels/Registr*.cs` | `Features/Discover/Registries/` | C6 |
| `Views/Panels/DiscoverReviewOverlay.*`; `ViewModels/DiscoverActionReview.cs` | `Features/Discover/` (shared; also used by Setup and FirstRun view-models) | C6 |
| `Views/Panels/AgentsPanel.*`; `ViewModels/Agents*.cs` | `Features/Agents/` | C6 |
| `MainWindow.xaml(.cs)`; `Views/Shell/**`; `ViewModels/{MainWindowViewModel*,StatusStripViewModel,StripChip,PanelViewModelBase,BatchObservableCollection,ConnectorColumn,ConnectorScopeViewModel}.cs` | `Features/Shell/` (`BatchObservableCollection`, `ConnectorColumn`, `ConnectorScopeViewModel` are shared by several surfaces) | C7 |
| `Views/TrayFlyoutWindow.*`, `Views/FlyoutPlacement.cs`; `ViewModels/TrayFlyoutViewModel*.cs` | `Features/Shell/Tray/` | C7 |
| `Views/Updates/**`; `ViewModels/Updates/**` | `Features/Shell/Updates/` | C8 |
| `Views/Panels/{Setup*,NotificationRoutingDialog,ConnectorBatchDialog,RuntimeMonitoringDialog}.*`, `Views/Panels/CredentialEntryBox.cs`; `ViewModels/{SetupPanelViewModel*,SetupPlan,SetupReadiness,SetupDialogViewModel,ReadinessViewModel,ConnectorBatchViewModel,NotificationRoutingViewModel,NotificationSwitch,RuntimeMonitoringViewModel,CredentialRowViewModel*,CredentialTerminal,CredentialsViewModel*,WizardCardStates,WizardTileIcons,RestartQueueReview}.cs` | `Features/Setup/` | C9 |
| `Views/Wizards/**`; `ViewModels/Wizards/**` | `Features/Setup/Wizards/` | C10 |
| `Views/ConfigEditor/**`; `ViewModels/ConfigEditor/**` | `Features/Setup/ConfigEditor/` | C11 |
| `Views/Redaction/**`; `ViewModels/Redaction/**` | `Features/Setup/Redaction/` | C12 |
| `Views/Guardrail/**`; `ViewModels/GuardrailControlsViewModel.cs` | `Features/Setup/Guardrail/` | C12 |
| `Views/SetupResources/**`; `ViewModels/SetupResources/**` | `Features/Setup/SetupResources/` | C12 |
| `Views/FirstRun/**`; `ViewModels/FirstRun/**` | `Features/Setup/FirstRun/` | C12 |
| `Views/Panels/Settings*.*`; `ViewModels/{Settings*,GatewayAdminToggleViewModel}.cs` | `Features/Settings/` | C13 |
| `App.xaml`, `App.xaml.cs`, `AssemblyInfo.cs` | `App/` | C14 |
| `Services/**`, `Assets/**`, `DefenseClaw.App.csproj` | stay | none |

### 8.5 Every file, from to

Generated from the rules above against `git ls-files DefenseClaw.App` at 45e6f8f (324 files). `X{,.cs}` means `X` and `X.cs`. The destination folder is shown; the file name does not change.

#### C2: DesignSystem (27 files)

```text
Themes/Controls.xaml -> DesignSystem/Themes/
Themes/DefenseClaw.xaml -> DesignSystem/Themes/
Themes/Styles/Cisco.Dark.xaml -> DesignSystem/Themes/Styles/
Themes/Styles/Cisco.Light.xaml -> DesignSystem/Themes/Styles/
Themes/Styles/Default.xaml -> DesignSystem/Themes/Styles/
Themes/Styles/Linear.Dark.xaml -> DesignSystem/Themes/Styles/
Themes/Styles/Linear.Light.xaml -> DesignSystem/Themes/Styles/
Themes/Styles/Tui.Dark.xaml -> DesignSystem/Themes/Styles/
Themes/Styles/Tui.Light.xaml -> DesignSystem/Themes/Styles/
Views/Controls/DcBadges.cs -> DesignSystem/Controls/
Views/Controls/DcCardHeader.cs -> DesignSystem/Controls/
Views/Controls/DcCommandOutput.xaml{,.cs} -> DesignSystem/Controls/
Views/Controls/DcConnectorScopeChip.cs -> DesignSystem/Controls/
Views/Controls/DcGlyphs.cs -> DesignSystem/Controls/
Views/Controls/DcIcon.cs -> DesignSystem/Controls/
Views/Controls/DcInspector.cs -> DesignSystem/Controls/
Views/Controls/DcPageToolbar.cs -> DesignSystem/Controls/
Views/Controls/DcRowMenu.cs -> DesignSystem/Controls/
Views/Controls/DcSegmented.cs -> DesignSystem/Controls/
Views/Controls/DcSpinner.cs -> DesignSystem/Controls/
Views/Controls/DcSymbolIcon.cs -> DesignSystem/Controls/
Views/Controls/HourlyBarChart.cs -> DesignSystem/Controls/
Views/Panels/ColumnSizing.cs -> DesignSystem/Layout/
Views/Panels/CompactLayout.cs -> DesignSystem/Layout/
Views/Panels/NestedScroll.cs -> DesignSystem/Layout/
Views/Panels/TranscriptList.cs -> DesignSystem/Layout/
```

#### C3: Overview (29 files)

```text
ViewModels/DoctorReconciliation.cs -> Features/Overview/
ViewModels/DurabilityAttention.cs -> Features/Overview/
ViewModels/OverviewAgents.cs -> Features/Overview/
ViewModels/OverviewDetectedConnectors.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Actions.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Activity.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.AddConnector.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Agent360.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Agents.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Attention.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.BomCoverage.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Configuration.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Durability.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.EffectivePolicy.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Enforcement.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.GatewayAdmin.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Installation.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Observability.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.PolicyCards.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Restart.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Runtime104.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.RuntimeMonitoring.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Scope.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.Services.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.StartGateway.cs -> Features/Overview/
ViewModels/OverviewPanelViewModel.cs -> Features/Overview/
ViewModels/OverviewStatusReader.cs -> Features/Overview/
Views/Panels/OverviewPanel.xaml{,.cs} -> Features/Overview/
```

#### C4: Alerts, Logs, Audit, Activity (36 files)

```text
ViewModels/ActivityMutations.cs -> Features/Activity/
ViewModels/ActivityMutationsViewModel.cs -> Features/Activity/
ViewModels/ActivityPanelViewModel.cs -> Features/Activity/
ViewModels/ActivityTranscript.cs -> Features/Activity/
ViewModels/AlertsPanelViewModel.Detail.cs -> Features/Alerts/
ViewModels/AlertsPanelViewModel.Events.cs -> Features/Alerts/
ViewModels/AlertsPanelViewModel.Runtime.cs -> Features/Alerts/
ViewModels/AlertsPanelViewModel.Search.cs -> Features/Alerts/
ViewModels/AlertsPanelViewModel.cs -> Features/Alerts/
ViewModels/AuditPanelViewModel.Actionable.cs -> Features/Audit/
ViewModels/AuditPanelViewModel.Archive.cs -> Features/Audit/
ViewModels/AuditPanelViewModel.Correlation.cs -> Features/Audit/
ViewModels/AuditPanelViewModel.Live.cs -> Features/Audit/
ViewModels/AuditPanelViewModel.Search.cs -> Features/Audit/
ViewModels/AuditPanelViewModel.cs -> Features/Audit/
ViewModels/JudgeHistoryViewModel.cs -> Features/Logs/
ViewModels/LogEntry.cs -> Features/Logs/
ViewModels/LogPresets.cs -> Features/Logs/
ViewModels/LogsPanelViewModel.Actionable.cs -> Features/Logs/
ViewModels/LogsPanelViewModel.Keys.cs -> Features/Logs/
ViewModels/LogsPanelViewModel.Search.cs -> Features/Logs/
ViewModels/LogsPanelViewModel.cs -> Features/Logs/
Views/JudgeHistory/JudgeHistoryView.xaml{,.cs} -> Features/Logs/
Views/JudgeHistory/JudgeHistoryWindow.xaml{,.cs} -> Features/Logs/
Views/Panels/ActivityMutationsView.xaml{,.cs} -> Features/Activity/
Views/Panels/ActivityPanel.xaml{,.cs} -> Features/Activity/
Views/Panels/AlertsPanel.xaml{,.cs} -> Features/Alerts/
Views/Panels/AuditPanel.xaml{,.cs} -> Features/Audit/
Views/Panels/LogsPanel.xaml{,.cs} -> Features/Logs/
```

#### C5: Catalogs, Policies, Sandboxes (36 files)

```text
ViewModels/Govern/CatalogTrust.cs -> Features/Catalogs/
ViewModels/Govern/GovernJson.cs -> Features/Catalogs/
ViewModels/Govern/GovernPanelViewModelBase.cs -> Features/Catalogs/
ViewModels/Govern/GovernRow.cs -> Features/Catalogs/
ViewModels/McpsPanelViewModel.cs -> Features/Catalogs/
ViewModels/PluginsPanelViewModel.cs -> Features/Catalogs/
ViewModels/PoliciesPanelViewModel.Actions.cs -> Features/Policies/
ViewModels/PoliciesPanelViewModel.cs -> Features/Policies/
ViewModels/PolicyFormViewModel.cs -> Features/Policies/
ViewModels/PolicyModelItems.cs -> Features/Policies/
ViewModels/PolicyModelViewModel.Actions.cs -> Features/Policies/
ViewModels/PolicyModelViewModel.cs -> Features/Policies/
ViewModels/PolicyRow.cs -> Features/Policies/
ViewModels/SkillsPanelViewModel.cs -> Features/Catalogs/
ViewModels/ToolsPanelViewModel.OperatorLists.cs -> Features/Catalogs/
ViewModels/ToolsPanelViewModel.cs -> Features/Catalogs/
Views/Panels/Govern/ChangeTipConverter.cs -> Features/Catalogs/
Views/Panels/Govern/FillHeightConverter.cs -> Features/Catalogs/
Views/Panels/Govern/GovernConfirmOverlay.xaml{,.cs} -> Features/Catalogs/
Views/Panels/Govern/GovernOutputCard.xaml{,.cs} -> Features/Catalogs/
Views/Panels/Govern/GovernPanelKeys.cs -> Features/Catalogs/
Views/Panels/Govern/GovernTable.xaml -> Features/Catalogs/
Views/Panels/McpsPanel.xaml{,.cs} -> Features/Catalogs/
Views/Panels/PluginsPanel.xaml{,.cs} -> Features/Catalogs/
Views/Panels/PoliciesPanel.xaml{,.cs} -> Features/Policies/
Views/Panels/PolicyModelView.xaml{,.cs} -> Features/Policies/
Views/Panels/SkillsPanel.xaml{,.cs} -> Features/Catalogs/
Views/Panels/ToolsPanel.xaml{,.cs} -> Features/Catalogs/
```

#### C6: Discover, Agents (44 files)

```text
ViewModels/AgentsPanelViewModel.Focus.cs -> Features/Agents/
ViewModels/AgentsPanelViewModel.cs -> Features/Agents/
ViewModels/AgentsRows.cs -> Features/Agents/
ViewModels/AiDiscoveryModelRows.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryNovelty.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryPanelViewModel.AddConnector.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryPanelViewModel.Coverage.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryPanelViewModel.Models.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryPanelViewModel.Navigation.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryPanelViewModel.Tuning.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryPanelViewModel.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryProvenance.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoverySignals.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryTuningViewModel.cs -> Features/Discover/AiDiscovery/
ViewModels/AiDiscoveryUsage.cs -> Features/Discover/AiDiscovery/
ViewModels/AiRuntimePanelViewModel.Actions.cs -> Features/Discover/Runtime/
ViewModels/AiRuntimePanelViewModel.PlaneC.cs -> Features/Discover/Runtime/
ViewModels/AiRuntimePanelViewModel.Prerequisites.cs -> Features/Discover/Runtime/
ViewModels/AiRuntimePanelViewModel.cs -> Features/Discover/Runtime/
ViewModels/AiRuntimeRows.cs -> Features/Discover/Runtime/
ViewModels/DiscoverActionReview.cs -> Features/Discover/
ViewModels/InventoryBomBrowser.Extras.cs -> Features/Discover/Inventory/
ViewModels/InventoryBomBrowser.cs -> Features/Discover/Inventory/
ViewModels/InventoryPanelViewModel.Bom.cs -> Features/Discover/Inventory/
ViewModels/InventoryPanelViewModel.IdeInventory.cs -> Features/Discover/Inventory/
ViewModels/InventoryPanelViewModel.cs -> Features/Discover/Inventory/
ViewModels/RegistriesPanelViewModel.Actions.cs -> Features/Discover/Registries/
ViewModels/RegistriesPanelViewModel.Entries.cs -> Features/Discover/Registries/
ViewModels/RegistriesPanelViewModel.cs -> Features/Discover/Registries/
ViewModels/RegistryIndexInfo.cs -> Features/Discover/Registries/
Views/Panels/AgentsPanel.xaml{,.cs} -> Features/Agents/
Views/Panels/AiDiscoveryPanel.xaml{,.cs} -> Features/Discover/AiDiscovery/
Views/Panels/AiDiscoveryTuningDialog.xaml{,.cs} -> Features/Discover/AiDiscovery/
Views/Panels/AiRuntimePanel.xaml{,.cs} -> Features/Discover/Runtime/
Views/Panels/DiscoverReviewOverlay.xaml{,.cs} -> Features/Discover/
Views/Panels/InventoryPanel.xaml{,.cs} -> Features/Discover/Inventory/
Views/Panels/RegistriesPanel.xaml{,.cs} -> Features/Discover/Registries/
```

#### C7: Shell and Tray (36 files)

```text
MainWindow.xaml{,.cs} -> Features/Shell/
ViewModels/BatchObservableCollection.cs -> Features/Shell/
ViewModels/ConnectorColumn.cs -> Features/Shell/
ViewModels/ConnectorScopeViewModel.cs -> Features/Shell/
ViewModels/MainWindowViewModel.Unread.cs -> Features/Shell/
ViewModels/MainWindowViewModel.cs -> Features/Shell/
ViewModels/PanelViewModelBase.cs -> Features/Shell/
ViewModels/StatusStripViewModel.cs -> Features/Shell/
ViewModels/StripChip.cs -> Features/Shell/
ViewModels/TrayFlyoutViewModel.Rows.cs -> Features/Shell/Tray/
ViewModels/TrayFlyoutViewModel.cs -> Features/Shell/Tray/
Views/FlyoutPlacement.cs -> Features/Shell/Tray/
Views/Shell/AccessibleNavigation.cs -> Features/Shell/
Views/Shell/AppearanceFlyout.xaml{,.cs} -> Features/Shell/
Views/Shell/ChipStripLayout.cs -> Features/Shell/
Views/Shell/ChipStripPanel.cs -> Features/Shell/
Views/Shell/CommandPaletteControl.xaml{,.cs} -> Features/Shell/
Views/Shell/CommandReviewControl.xaml{,.cs} -> Features/Shell/
Views/Shell/GatewayActionDialog.xaml{,.cs} -> Features/Shell/
Views/Shell/LiveRegion.cs -> Features/Shell/
Views/Shell/SelfUpdateBanner.xaml{,.cs} -> Features/Shell/
Views/Shell/ShortcutsOverlay.xaml{,.cs} -> Features/Shell/
Views/Shell/SidebarBadge.cs -> Features/Shell/
Views/Shell/StatusStripControl.xaml{,.cs} -> Features/Shell/
Views/Shell/UpdateBanner.xaml{,.cs} -> Features/Shell/
Views/TrayFlyoutWindow.xaml{,.cs} -> Features/Shell/Tray/
```

#### C8: Updates (11 files)

```text
ViewModels/Updates/InstallScriptUpgradeViewModel.cs -> Features/Shell/Updates/
ViewModels/Updates/UpdatesWindowViewModel.cs -> Features/Shell/Updates/
ViewModels/Updates/UpgradeSectionViewModel.cs -> Features/Shell/Updates/
Views/Updates/InstallScriptReviewOverlay.xaml{,.cs} -> Features/Shell/Updates/
Views/Updates/InstallScriptUpgradeCard.xaml{,.cs} -> Features/Shell/Updates/
Views/Updates/SelfUpdateWindow.xaml{,.cs} -> Features/Shell/Updates/
Views/Updates/UpdatesWindow.xaml{,.cs} -> Features/Shell/Updates/
```

#### C9: Setup root (26 files)

```text
ViewModels/ConnectorBatchViewModel.cs -> Features/Setup/
ViewModels/CredentialRowViewModel.Entry.cs -> Features/Setup/
ViewModels/CredentialTerminal.cs -> Features/Setup/
ViewModels/CredentialsViewModel.InApp.cs -> Features/Setup/
ViewModels/CredentialsViewModel.cs -> Features/Setup/
ViewModels/NotificationRoutingViewModel.cs -> Features/Setup/
ViewModels/NotificationSwitch.cs -> Features/Setup/
ViewModels/ReadinessViewModel.cs -> Features/Setup/
ViewModels/RestartQueueReview.cs -> Features/Setup/
ViewModels/RuntimeMonitoringViewModel.cs -> Features/Setup/
ViewModels/SetupDialogViewModel.cs -> Features/Setup/
ViewModels/SetupPanelViewModel.Restart.cs -> Features/Setup/
ViewModels/SetupPanelViewModel.cs -> Features/Setup/
ViewModels/SetupPlan.cs -> Features/Setup/
ViewModels/SetupReadiness.cs -> Features/Setup/
ViewModels/WizardCardStates.cs -> Features/Setup/
ViewModels/WizardTileIcons.cs -> Features/Setup/
Views/Panels/ConnectorBatchDialog.xaml{,.cs} -> Features/Setup/
Views/Panels/CredentialEntryBox.cs -> Features/Setup/
Views/Panels/NotificationRoutingDialog.xaml{,.cs} -> Features/Setup/
Views/Panels/RuntimeMonitoringDialog.xaml{,.cs} -> Features/Setup/
Views/Panels/SetupPanel.xaml{,.cs} -> Features/Setup/
```

#### C10: Setup wizards (16 files)

```text
ViewModels/Wizards/WizardFieldViewModel.Models.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardFieldViewModel.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardGoalsViewModel.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardGuideViewModel.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardViewModel.Goals.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardViewModel.KeysSet.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardViewModel.Models.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardViewModel.Secrets.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardViewModel.TrustHint.cs -> Features/Setup/Wizards/
ViewModels/Wizards/WizardViewModel.cs -> Features/Setup/Wizards/
Views/Wizards/InverseBoolToVisibilityConverter.cs -> Features/Setup/Wizards/
Views/Wizards/ModelPickerBox.xaml{,.cs} -> Features/Setup/Wizards/
Views/Wizards/SecretEntryBox.cs -> Features/Setup/Wizards/
Views/Wizards/WizardWindow.xaml{,.cs} -> Features/Setup/Wizards/
```

#### C11: Setup config editor (21 files)

```text
ViewModels/ConfigEditor/ConfigDiffReview.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigDraftRebase.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigEditorWindowViewModel.ExternalChange.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigEditorWindowViewModel.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigFieldCatalog.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigFieldValidator.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigFormBuilder.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigFormModels.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigLoadProblem.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigRuntimeNotes.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/ConfigSaveService.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/SensitiveKeyClassifier.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/UnsavedChangesPrompt.cs -> Features/Setup/ConfigEditor/
ViewModels/ConfigEditor/YamlSectionEditor.cs -> Features/Setup/ConfigEditor/
Views/ConfigEditor/ConfigEditorConverters.cs -> Features/Setup/ConfigEditor/
Views/ConfigEditor/ConfigEditorWindow.xaml{,.cs} -> Features/Setup/ConfigEditor/
Views/ConfigEditor/FormFieldTemplateSelector.cs -> Features/Setup/ConfigEditor/
Views/ConfigEditor/UnsavedChangesDialog.xaml{,.cs} -> Features/Setup/ConfigEditor/
Views/ConfigEditor/YamlHighlighting.cs -> Features/Setup/ConfigEditor/
```

#### C12: Setup redaction, guardrail, list editors, first run (29 files)

```text
ViewModels/FirstRun/FirstRunViewModel.cs -> Features/Setup/FirstRun/
ViewModels/GuardrailControlsViewModel.cs -> Features/Setup/Guardrail/
ViewModels/Redaction/RedactionFormViewModel.cs -> Features/Setup/Redaction/
ViewModels/Redaction/RedactionOutcome.cs -> Features/Setup/Redaction/
ViewModels/Redaction/RedactionRows.cs -> Features/Setup/Redaction/
ViewModels/Redaction/RedactionViewModel.Apply.cs -> Features/Setup/Redaction/
ViewModels/Redaction/RedactionViewModel.Changes.cs -> Features/Setup/Redaction/
ViewModels/Redaction/RedactionViewModel.Status.cs -> Features/Setup/Redaction/
ViewModels/Redaction/RedactionViewModel.cs -> Features/Setup/Redaction/
ViewModels/SetupResources/ObservabilityDestinationsViewModel.cs -> Features/Setup/SetupResources/
ViewModels/SetupResources/SetupResourceRow.cs -> Features/Setup/SetupResources/
ViewModels/SetupResources/SetupResourceViewModel.Changes.cs -> Features/Setup/SetupResources/
ViewModels/SetupResources/SetupResourceViewModel.cs -> Features/Setup/SetupResources/
ViewModels/SetupResources/TrustedPathsViewModel.cs -> Features/Setup/SetupResources/
ViewModels/SetupResources/WebhooksViewModel.cs -> Features/Setup/SetupResources/
Views/FirstRun/FirstRunWindow.xaml{,.cs} -> Features/Setup/FirstRun/
Views/Guardrail/GuardrailControlsView.xaml{,.cs} -> Features/Setup/Guardrail/
Views/Guardrail/GuardrailControlsWindow.xaml{,.cs} -> Features/Setup/Guardrail/
Views/Redaction/RedactionView.xaml{,.cs} -> Features/Setup/Redaction/
Views/Redaction/RedactionWindow.xaml{,.cs} -> Features/Setup/Redaction/
Views/SetupResources/SetupResourceView.xaml{,.cs} -> Features/Setup/SetupResources/
Views/SetupResources/SetupResourceWindow.xaml{,.cs} -> Features/Setup/SetupResources/
```

#### C13: Settings (10 files)

```text
ViewModels/GatewayAdminToggleViewModel.cs -> Features/Settings/
ViewModels/SettingsPanelViewModel.Advanced.cs -> Features/Settings/
ViewModels/SettingsPanelViewModel.Archive.cs -> Features/Settings/
ViewModels/SettingsPanelViewModel.Installation.cs -> Features/Settings/
ViewModels/SettingsPanelViewModel.cs -> Features/Settings/
ViewModels/SettingsPathRow.cs -> Features/Settings/
ViewModels/SettingsPlatform.cs -> Features/Settings/
Views/Panels/SettingsPanel.xaml{,.cs} -> Features/Settings/
Views/Panels/SettingsRow.cs -> Features/Settings/
```

#### C14: App folder (3 files)

```text
App.xaml{,.cs} -> App/
AssemblyInfo.cs -> App/
```


### 8.6 WPF constraints to preserve

| Constraint | What to do |
|---|---|
| `x:Class` and namespaces | **Recommendation: keep every namespace unchanged** (`DefenseClaw.App.Views.Panels`, `...ViewModels`, ...). `x:Class` does not have to match the folder, so no XAML or code changes; 130 files declare `DefenseClaw.App.ViewModels`, and 48 `clr-namespace:` references target `Views.Controls`. Changing namespaces is a separate later wave (every `x:Class`, `xmlns:`, `using` and `typeof`). A folder named `App/` makes a folder-derived namespace `DefenseClaw.App.App` collide with the class `DefenseClaw.App.App`; another reason to leave them |
| XAML and code-behind pairs | Move `X.xaml` and `X.xaml.cs` in the same commit and the same folder |
| Relative `Source=` | Resolved from the file that contains it. `Themes/DefenseClaw.xaml` merges `Styles/Default.xaml` and `Controls.xaml` (lines 175, 177): move `Themes/` as one unit so they need no edit. `App.xaml` merges `Themes/DefenseClaw.xaml` relatively; C2 makes it absolute so C14 can move `App.xaml` without touching it |
| Merge order in `App.xaml` | WPF-UI `ThemesDictionary`, then `ControlsDictionary`, then `DefenseClaw.xaml`; `AppearanceService` appends the chosen style last. Do not reorder |
| Pack URIs | Form `pack://application:,,,/DefenseClaw.App;component/<project-relative path>` or `/DefenseClaw.App;component/<path>`. BAML paths mirror the project-relative path, so a stale URI **compiles and fails at run time** with a `XamlParseException` when the dictionary or panel first loads. List in 8.7 |
| csproj globbing | SDK-style: `**/*.xaml` are `Page` items and `**/*.cs` are `Compile` items; the csproj has no explicit item for any moved file. Only `ApplicationIcon` (`Assets\DefenseClaw.ico`, line 27) names a path and `Assets/` stays. Do not add `Page`/`Compile` includes |
| Test projects | `DefenseClaw.App.Tests.csproj` lists no App source file (it links `DefenseClaw.Tests` fixtures and three helper `.cs` files only). 14 test files name App source paths or resource URIs (8.7) |
| Generated files | After the first move run `dotnet clean` (or delete `DefenseClaw.App/obj`) once: stale generated `.g.cs` and BAML for the old paths are harmless but a clean build proves nothing depends on them |
| File-name uniqueness | Flattened folders (`Govern/` into `Catalogs/`) need unique names. The rule check found none at 45e6f8f; re-run it after CUST-383..386 |
| Case-insensitive file system | No rename in this plan differs only by case |

### 8.7 Path-sensitive references (all of them)

References that **must change in the commit that moves their target**:

| Where | Reference | Change | Commit |
|---|---|---|---|
| `DefenseClaw.App/App.xaml:32` | `Source="Themes/DefenseClaw.xaml"` (relative) | `/DefenseClaw.App;component/DesignSystem/Themes/DefenseClaw.xaml` | C2 |
| `DefenseClaw.App/Services/Appearance/AppearanceModel.cs:132` | `pack://application:,,,/DefenseClaw.App;component/Themes/Styles/{file}` | `.../DesignSystem/Themes/Styles/{file}` | C2 |
| `DefenseClaw.App.Tests/TestSupport/UiThread.cs:121` | `pack://application:,,,/DefenseClaw.App;component/Themes/DefenseClaw.xaml` (comments at lines 14-15 too) | `.../DesignSystem/Themes/DefenseClaw.xaml` | C2 |
| `DefenseClaw.App.Tests/Appearance/AppearanceCiscoTests.cs:38,39` | `EndsWith("/Themes/Styles/Cisco.Dark.xaml")` and `Cisco.Light.xaml` | `/DesignSystem/Themes/Styles/...` | C2 |
| `DefenseClaw.App/Views/Panels/SkillsPanel.xaml:31`, `McpsPanel.xaml:30`, `PluginsPanel.xaml:29`, `ToolsPanel.xaml:32` | `/DefenseClaw.App;component/Views/Panels/Govern/GovernTable.xaml` | `/DefenseClaw.App;component/Features/Catalogs/GovernTable.xaml` | C5 |
| `DefenseClaw.Tests/UpdateClaimsTests.cs:32,34,35,76` | `DefenseClaw.App/Views/Updates/UpdatesWindow.xaml`, `ViewModels/Updates/UpgradeSectionViewModel.cs`, `UpdatesWindowViewModel.cs` | `Features/Shell/Updates/...` | C8 |
| `DefenseClaw.Tests/UpdateClaimsTests.cs:33,84` | `DefenseClaw.App/Views/FirstRun/FirstRunWindow.xaml` | `Features/Setup/FirstRun/FirstRunWindow.xaml` | C12 |

References **made move-proof by C1** (resolve by file name through `AppSource`, so no later commit touches them):

| Test file | Lines | What it reads |
|---|---|---|
| `Activity/StderrMarkerTests.cs` | 50, 54, 73 | `Views/Panels/ActivityPanel.xaml`; anchor `MainWindow.xaml` |
| `Appearance/AppearanceCiscoTests.cs` | 230, 237 | anchor `Themes/DefenseClaw.xaml` |
| `Icons/IconViewTests.cs` | 71-75, 346-351, 361 | `Views/Panels/<ViewType>.xaml` for every panel; `MainWindow.xaml`; `Views/TrayFlyoutWindow.xaml`; every `*.xaml` under `Views/Panels`, `Views/Shell`, `Views/Updates`; anchor |
| `Monitoring/TrayFlyoutWindowTests.cs` | 269, 287 | `Views/TrayFlyoutWindow.xaml`; anchor |
| `Overview/AttentionToneTests.cs` | 303, 310 | anchor `Themes/DefenseClaw.xaml` |
| `Overview/OverviewLayoutTests.cs` | 544, 551 | anchor `Views/Panels/OverviewPanel.xaml` |
| `Runtime/PlaneCReadinessPanelTests.cs` | 220-221 | `DefenseClaw.App\ViewModels\AiRuntimePanelViewModel.PlaneC.cs` (and `Services\WindowsPlaneCProbes.cs`, which stays) |
| `Shell/LiveRegionTests.cs` | 407, 440 | every `*.xaml` under the app folder; anchor `MainWindow.xaml` |
| `Shell/MainWindowBindingTests.cs` | 17, 33, 54 | `MainWindow.xaml` |
| `Shell/PageChromeTests.cs` | 488-489, 503, 553-558, 811 | `Themes/Controls.xaml`, `DefenseClaw.xaml`, `Styles/Default.xaml`; `Views/Panels/<view>.xaml`; anchor |
| `Shell/SpinnerTests.cs` | 237-260, 462 | every `*.xaml` under the app folder; anchor |
| `Shell/UpdateBannerViewTests.cs` | 266, 273 | anchor `Views/Shell/UpdateBanner.xaml` |

References that **do not change**:

| Where | Why it stays valid |
|---|---|
| `DefenseClaw.App/DefenseClaw.App.csproj:27` `ApplicationIcon`, `installer/DefenseClawWin.iss:78` `SetupIconFile`, `DefenseClaw.App.Tests/Branding/AppIconTests.cs:85` | `Assets/` does not move |
| `.github/workflows/ci.yml:96`, `.github/dependabot.yml:17,19`, `DefenseClaw.Win.sln:8` | name the project folder or csproj, not a file inside it |
| `DefenseClaw.App.Tests/GatewayAdmin/GatewayAdminServiceTests.cs:693` | enumerates every `*.cs` under `DefenseClaw.App`; anchor is the `.sln` |
| `DefenseClaw.Tests/SqlitePoolHygieneTests.cs:40-42` | enumerates the two test projects, not the app |
| `DefenseClaw.App/Services/PanelCatalog.cs` and all code | refer to types (`typeof(OverviewPanel)`), not paths |
| `docs/PERFORMANCE-DURABILITY-EVAL.md`, `docs/QUICK-WINS-LOG.html`, `docs/PLAN.md:39` | dated records or generic; leave as written |
| `docs/PARITY-FOUNDATIONS.md` lines 16-28 and 542 | path lists in prose; updated in C15 (no build effect) |

### 8.8 Risks

| # | Risk | Likelihood / impact | Mitigation |
|---|---|---|---|
| 1 | Renames collide with open branches (CUST-383..386 edit `MainWindow.xaml`, `PanelCatalog.cs`, `ShellCommandRegistry.cs`, `SetupPanel*`, `SettingsPanel*`) | High if started early / large | Start after all four merge; announce a freeze on `DefenseClaw.App/` paths; land C1..C15 back to back; other branches rebase with rename detection on |
| 2 | A stale pack URI builds and then throws at run time | Medium / high (a blank panel or no theme) | Only 7 URIs (8.7); C2 and C5 gates render the theme and the four catalog panels; offscreen scene tests exercise the theme URI |
| 3 | Source-reading tests pass vacuously or fail | Medium / medium | C1 routes all 12 through one `AppSource` that throws on zero or several matches; `UpdateClaimsTests` edited by hand in C8 and C12 |
| 4 | Files added by CUST-383..386 match no rule or collide | Medium / low | Re-run the rule check on merged `main` before C2; prefix rules for `Sandbox*`, `Settings*`, `Setup*`, `Views/Shell/**` |
| 5 | Folder and namespace no longer match | Certain / low | Deliberate (8.6); recorded in this file; decide a namespace wave separately |
| 6 | History and blame lost | Low / medium | `git mv` only; no edits to moved files beyond the 7 URIs; check `git diff -M90% --summary` shows one rename per file |
| 7 | Stale `obj/` after the first move | Medium / low | `dotnet clean` first; CI starts clean |
| 8 | Over-large folders (`Setup` 92, `Shell` 47, `Discover` 39) | Certain / low | Sub-folders as in 8.2; re-split after CUST-385 turns the config editor into an inline view |
| 9 | Shared files housed with one surface (`ConnectorColumn`, `ConnectorScopeViewModel`, `BatchObservableCollection` in Shell; `DiscoverActionReview` in Discover; `RestartQueueReview` in Setup) | Medium / low | Listed in 8.4; moving one later is a single rename |
| 10 | Long paths | Low / low | Longest new relative path is 73 characters (`Features/Setup/ConfigEditor/ConfigEditorWindowViewModel.ExternalChange.cs`) |
| 11 | Dedicated assets moved by mistake (`Assets/`) | Low / high (icon test, installer) | `Assets/` is a "stay" rule |
| 12 | A reader still follows old paths in docs | Medium / low | C15 and this file's Section 2.1 |

### 8.9 Check after each commit

1. `git diff -M90% --summary HEAD~1` lists one rename per moved file and nothing else besides the edits named in 8.3.
2. `dotnet build DefenseClaw.Win.sln -c Debug` has no warnings.
3. `dotnet test DefenseClaw.Tests -c Debug --no-build --filter "Category!=Live"` and the same for `DefenseClaw.App.Tests`. Known load flakes (pass in isolation): `AlertsKindTilesDetailTests`, `LogsPanelViewModelTests`, `WizardInstallationGuardTests`, the `AppSettingsStoreTests` retry test.
4. The rule check from 8.4 reports no unmapped file and no two files with one destination.

## 9. Issue key

Each issue below is cited in a table above. Titles are the oldest commit subject that names the number; the issues of this round are marked *(in progress)*.

| Issue | What it did |
|---|---|
| CUST-173 | Consolidate the command-review dialogs into one shared control |
| CUST-174 | Config editor: prompt before losing unsaved edits; Overview HIGH row uses the High tone |
| CUST-175 | Inventory scrolling page and column sizing; Activity virtualized transcripts |
| CUST-176 | Logs/Audit data-path fixes and a persistent setup-help cache |
| CUST-177 | Verify skill/MCP/registry/AI BOM/AI runtime data paths against their emitters |
| CUST-178 | Inventory scrolling page and column sizing; Activity virtualized transcripts |
| CUST-179 | in-app entry of Splunk/observability tokens via child-process environment |
| CUST-193 | Review-surface round: read-only pins for Overview and config-editor commands, Activity outer-list virtualization, Galileo key entry, registry entry errors |
| CUST-194 | Appearance: style themes (Default, Linear, TUI) and a live light/dark toggle |
| CUST-195 | App icon: a Cisco-blue shield carrying a lobster claw, from one source |
| CUST-200 | Parity A0 foundations: settings store, navigation requests, alert counts, connector scope |
| CUST-201 | Parity A1: unacknowledged-findings badges and tray count |
| CUST-202 | Parity A2: notifications v2 |
| CUST-203 | Settings page: monitoring, notifications, startup, connection, updates |
| CUST-204 | Tray flyout parity with the Mac menu-bar popover, and a real Pause monitoring |
| CUST-205 | Overview recomposition in the Mac's order, with clickable enforcement tiles |
| CUST-206 | Background update awareness and update banner |
| CUST-207 | Add the Cisco appearance style: the Mac app's look as a fourth style |
| CUST-209 | Overview recomposition in the Mac's order, with clickable enforcement tiles |
| CUST-210 | First-run guided setup and "Add" for detected connectors |
| CUST-211 | Opt-in gateway auto-start |
| CUST-212 | Govern: Scan action on skills, MCP servers and plugins, Scan all skills, and skill install |
| CUST-213 | Overview attention parity, doctor STALE reconciliation, token-rejected banner |
| CUST-214 | Parity B5: dense sortable zebra tables, row action menu, severity and state badges; Govern panels as dense tables |
| CUST-216 | Logs - canonical Verdicts/Events streams, presets, redaction |
| CUST-217 | Audit presets, same-run correlation, export |
| CUST-218 | Alerts severity tiles, kind filter, findings and target history, egress and silent bypass |
| CUST-219 | Activity Mutations tab with before/after diff |
| CUST-220 | config editor pre-save diff and reviewed gateway restart |
| CUST-221 | Credentials: set a value in the app by typing it at the CLI's hidden prompt in a pseudo-console |
| CUST-222 | guardrail controls - HILT, block message, judge gate |
| CUST-223 | shared connector scope across panels |
| CUST-224 | Mac menu chords in the palette and curated CLI commands |
| CUST-225 | Setup wizards as a 233x92 tile grid |
| CUST-247 | Bind the gateway-token peer check to the actual connection |
| CUST-249 | Ignore version and health claims from an unverified /health peer |
| CUST-250 | Cap unbounded reads: config.yaml size and flow depth, file reads, HTTP bodies, CLI lines |
| CUST-255 | Alerts: acknowledge/dismiss selection acts on the selected ids, not the severity class |
| CUST-258 | Overview Hook Calls / Blocks: all-time per-connector totals, TUI captions |
| CUST-260 | Judge response history viewer on Logs -> Verdicts |
| CUST-261 | Search tokens, correlation ids, hook rows, Current state and the Connector column for Alerts, Audit and Logs |
| CUST-262 | Events view with the TUI's actionable filter, Audit actionable switch and live refresh |
| CUST-263 | Logs: the TUI's presets, labelled verdict/event fields, pause count and keys |
| CUST-264 | Activity: outcome footer, next-step hints and Rerun; palette recents, re-run last, cancel running, gateway verbs |
| CUST-265 | Sidebar "new since last visit" capsules on Audit, Activity and AI Discovery |
| CUST-266 | Setup: Credentials card + readiness checklist with Fix buttons |
| CUST-267 | Setup: app-wide pending-restart queue |
| CUST-268 | Config editor: typed field validation and curated choice lists |
| CUST-269 | Setup: guided LLM and guardrail wizards |
| CUST-270 | Setup: list-first editors for observability destinations, webhooks and trusted paths |
| CUST-271 | Setup: multi-command flows - notification routing, AI Discovery tuning, connector batch, card state lines |
| CUST-272 | Overview Observability card: the compiled plan merged with /health, a Local SQLite line, the aggregate redaction label |
| CUST-273 | Status strip: Watchdog, Guardrail, Keys, policy, redaction, running and stale chips that collapse by priority |
| CUST-274 | Overview: the TUI's notices, configuration rows and reviewed buttons |
| CUST-275 | Inventory: AI BOM browser (Summary, Skills, Plugins, MCPs, Agents, Tools, Models, Memory) from aibom scan --json |
| CUST-276 | Registries: attribution badges, Open in Registries, cross-source Entries and Approved tabs, source detail counts |
| CUST-277 | AI Discovery: Products / Models, signal state, runtime lines, component bands |
| CUST-278 | Tray flyout shows the Overview's all-time hook totals through one shared reader |
| CUST-279 | Persist the hook block tally between launches |
| CUST-281 | Policies panel on the 0.8.10 `policy` commands: list, show, validate, test, activate, create, edit, delete |
| CUST-282 | Command palette: every entry of the connected runtime's TUI command registry |
| CUST-283 | Catalog safety: partial reads show rows with a warning; stale or failed data never authorizes changes |
| CUST-284 | Audit readers: unchanged snapshots are not read twice; an oversized row is unavailable, not empty |
| CUST-291 | Runtime detection, capability gating and a developer runtime selector |
| CUST-292 | Runtime compatibility with DefenseClaw source commit 95159fd |
| CUST-293 | Policies panel: the policy model on a runtime that has it |
| CUST-295 | Redaction window: advanced redaction controls gated on the pinned runtime |
| CUST-296 | Windows sandboxes (canceled: sandboxes stay unsupported on Windows) |
| CUST-299 | Audit panel: read-only Archive source for pre-upgrade history |
| CUST-308 | Installation context and managed read-only guard |
| CUST-309 | Runtime panel: planes, findings and Windows prerequisites, gated on the pinned runtime |
| CUST-310 | AI Discovery models: owner, relevance, confidence, lineage and the recommended view, from the files and GET /api/v1/ai-usage |
| CUST-311 | Setup: offer Local OTel (setup local-observability) on Windows behind the Docker probe |
| CUST-312 | Stale-data gating: catalogue, policy and runtime trust follow config.yaml and .env |
| CUST-313 | Overview Services: the TUI's nine service cards, Agent roll-up, Sinks, Sandbox |
| CUST-314 | Tray icon: paused and scanning states and an alert-count glyph |
| CUST-315 | Companion self-update: check this app's releases, reviewed install, swap with rollback |
| CUST-317 | Setup: offer the Splunk dashboards (Terraform) on Windows behind a Terraform probe |
| CUST-324 | Runtime panel: read-only Plane C readiness card (developer flag); Security-log wording |
| CUST-325 | Help docs palette entry (A13) and hide-until-relaunch for id-less alerts (AL4) |
| CUST-328 | Type secrets in the app everywhere: palette keys set and the wizards' key cards |
| CUST-329 | Logs inspector copy actions and Settings Python row |
| CUST-341 | Route every YamlDotNet entry point through ConfigYamlGuard; scrub CLI line cuts |
| CUST-346 | In-app upgrade to DefenseClaw 1.0 through the verified install.ps1 |
| CUST-348 | Runtime monitoring card for DefenseClaw 1.0 |
| CUST-352 | Gateway durability: exit reasons, post-start check, guided recovery, fail-mode visibility |
| CUST-354 | Open Agent360: deep links from Overview, connector rows and the palette |
| CUST-355 | Agents panel: in-app agent view over the gateway correlation ledger |
| CUST-358 | "Codex is running": ignore the always-on Codex sandbox service |
| CUST-359 | Upgrade preflight: block on pending CAS receipts; show the installer's own failure reason |
| CUST-360 | Stuck-write row: classify each pending CAS write by hash |
| CUST-373 | Runtime 1.0.4: removed connectors, per-user fail-mode rule, install lock, status cleanup |
| CUST-374 | Alerts: merge the runtime's alert list and say what detection-only hides |
| CUST-375 | Overview: Effective policy, Admission overrides and Operator lists cards; read-only lists on Tools |
| CUST-376 | AI BOM: IDE Plugins and Agent identities tabs, read from the gateway |
| CUST-377 | Config editor: draft survives external changes, hot-reload awareness, unloadable state, 1.0.4 keys |
| CUST-378 | Govern, upgrade guard, pack re-pin and audit paging: 1.0.4 parity |
| CUST-379 | Wizards: sequence display, 1.0.4 contract audit, private-network opt-in, per-user fail mode |
| CUST-380 | Alerts: badge, tray and Overview counts include the runtime's alert kinds |
| CUST-381 | AI BOM: agent identity opens its row in the Agents panel |
| CUST-382 | Command review: live per-step output for multi-step reviews |
| CUST-383 | Policies moves to Govern after Tools; new non-executing Sandboxes page; Agents stays as a Windows extension (in progress) |
| CUST-384 | New application menu bar (in progress) |
| CUST-385 | Setup: area grid first, inline config editor sharing one draft (in progress) |
| CUST-386 | Settings: General, Monitoring, Notifications, Connection tabs; file roles (in progress) |
| CUST-387 | This map and the wave-2 move proposal |
| CUST-389 | Mac 1.1.32 delta polish: audit tie order, access-key underscores, IDE plugin summary and N+, Scanners card aibom and keys rows (in progress) |

