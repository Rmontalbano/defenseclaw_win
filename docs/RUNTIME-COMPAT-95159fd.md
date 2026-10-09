# Runtime compatibility: DefenseClaw source commit 95159fd

This page records what the app was checked against and what changed for it. It was **verified against DefenseClaw source commit
`95159fd`; it requires a compatible runtime**. That commit is unreleased source that reports its own version as 1.0.0; nothing here is a
statement about a release, and the 0.8.10 behaviour is unchanged (the 0.8.10 fixtures and suites are untouched and still pass).

## What "verified" means

Nothing in this repository runs that source. The evidence is a set of **synthetic fixtures** under
`DefenseClaw.Tests/Fixtures/runtime-95159fd/`, shaped exactly like the output of a fresh install of that source (CLI JSON and text, the REST
routes the app reads, the `audit.db` DDL, `config.yaml`, the `--help` screens, the Click command tree) with every machine, user, id, digest and
timestamp replaced (`C:\Users\operator\...`, ids `00000000-0000-4000-8000-...`, dates 2030-01-15, destination names `example-otlp`). No token,
key or real path is in them (a test scans the whole directory for that). The App test project links the same directory.

What the fixtures cannot show: a fresh install has no connector, no findings, no scans and no history, so the payloads that need those are
either absent (empty lists) or built from the emitting code and marked as such below. Everything in the "not verified" list further down is
exactly that gap.

| Fixture group | Files | Derived from |
|---|---|---|
| `cli/` | `version-json`, `gateway-version-json`, `version.txt`, `status-json`, `status-json.connectors` (*), `doctor-json`, `doctor-cache` (*), `keys-list`, `policy-list`, `policy-show-default`, `observability-plan`, `guardrail-status` (json and text), `guardrail-list-packs`, `guardrail-protection-list`, `config-show-guardrail` (each also as `.connectors` (*)), `agent-discovery-*`, `agent-discovery-runtime-permissions.windows.synthetic` (*), `alerts`, `mcp-list`, `skill-list`, `plugin-list`, `tool-list`, `aibom-scan`, `config-path`, `config-validate`, `cli-tree` (*) | CLI output of a fresh install |
| `policy-model/` | `phase2-model.json`, `tool-chains.json` | the runtime's own model document (its catalog read and the rows it rendered from it) and its built-in chain catalog |
| `rest/` | `health`, `status`, `alerts`, `guardrail-config`, `enforce-blocked/-allowed`, `mcps`, `skills-not-connected`, `tools-catalog-not-connected`, `ai-usage-runtime` (the disabled answer), `ai-usage-runtime.populated.synthetic` (*), `ai-usage-runtime.degraded.synthetic` (*), `ai-usage-runtime.planes-ab.synthetic` (*), `unauthorized` | the gateway's GET routes |
| `help/` | `setup`, `setup-windows`, and the pages of `claude-code`, `cursor`, `codex`, `guardrail`, `observability`, `redaction`, `acp`, `gateway`, `trusted-paths`, `rotate-token`, `routing`, `local-observability` | `--help` screens |
| `audit/` | `audit-schema.sql` (the database's own `.schema`, 38 tables, 53 migrations), `judge-bodies-schema.sql`, `migrations.txt` | the schema only, no rows |
| `config/` | `config.fresh.yaml`, `config.connectors.yaml` (*) | a fresh `config.yaml`; the second is written from the Go config structs |

(*) not a capture. `status-json.connectors` adds two `connectors[]` rows built from the emitting code (`_connector_roster` and
`connector_fail_mode_report`); `doctor-cache` is the doctor JSON plus the `captured_at` the CLI adds when it writes the cache;
`cli-tree` is the whole command tree converted to the format the 0.8.10 tree fixture uses (secondary switch spellings such as `--no-restart`
recovered from the help screens); `config.connectors.yaml` uses the key names of the Go structs; the three `.connectors` Policies fixtures
describe two active connectors (one with its own mode, rule pack, alert level and an opt-in pack, one with its own block level), written in the
shape the capture has and checked against the cases the runtime's own tests pin (`DefenseClaw.Tests/PolicyModelTests.cs`).
The four `*.synthetic` Runtime-panel fixtures (CUST-309) are **synthetic, not captures** - no populated runtime snapshot exists from a Windows run, so they are written by hand from the emitting code: the wire shape from
`internal/gateway/ai_runtime_api.go:31-97`, the plane names, mechanisms and reasons from `internal/sensor/platform/windows.go`, `internal/sensor/plane/windows.go`
and `internal/sensor/service.go` (`degradedReasonsFor`, `planeHealth`), the severities, signal ids, chain wording and verdicts from `internal/sensor/scoring`,
`agentchain` and `correlate`, and the permissions document from `_RUNTIME_GRANTS["windows"]` in `cli/defenseclaw/commands/cmd_agent.py`. Processes, users, hosts and addresses are
invented (`EXAMPLE\operator`, `203.0.113.10`, `*.example`); the one credential-looking flag in a command line is the placeholder `synthetic-synthetic`, there to prove it is masked.
`agent-discovery.txt` and `agent-discovery-runtime.txt` at the root of the set are the two help screens the Runtime capability is decided from, laid out the way `root.txt` was
(Click's layout over each command's help text in the tree); `runtime-0.8.10/agent-discovery.txt` is the installed 0.8.10's own `agent discovery --help`.

The audit database is **not a copied database**. `RuntimeFixtures.CreateAuditDatabase()` builds one at test time from `audit-schema.sql`,
adds `schema_version` rows 1..53, and the tests insert a few synthetic rows written the way that source's event-history writer writes them.

## Results by area

| Area | Result |
|---|---|
| Gateway HTTP client | **Fixed.** Sends `X-DefenseClaw-Client` on every request. All the GET payloads parse. |
| `/health`, `/status`, `/alerts`, list routes | Compatible as is. New subsystem blocks are kept in the extension bag. |
| `--version-json`, `status --json`, `keys list --json`, doctor JSON and cache | Compatible as is. |
| `guardrail status` (text) | **Fixed.** New `Block/alert` column is fine; the narrow-terminal block layout was not read. |
| Audit, alert queue, mutations, hook totals, hourly activity, recent metrics, event stream, judge history, egress, correlation | Compatible as is. The whole Core suite passes against the 53-migration schema (see below). |
| `config.yaml` reader and editor | Compatible as is. |
| Install layout, gateway-peer trust | **Fixed.** Both layouts resolve; Setup's wins. |
| Setup tile grid | Compatible as is (35 targets). |
| Policies panel | **Added** for a runtime that has the policy model: six views (Windows has no Sandbox packs view), the 0.8.10 table is unchanged elsewhere. See below. |
| Command classifier (review tiers) | Compatible; the new commands were reviewed. One 0.8.10 read verb is gone. |
| Fixed argv built by the app | Compatible: all 47 still name a command and options that exist. |
| Runtime planes (`agent discovery runtime`, `GET /api/v1/ai-usage/runtime`) | **New panel** (CUST-309), offered only when `agent discovery --help` lists `runtime` and `agent discovery runtime --help` lists `status`, `scan`, `findings`, `permissions`. Reads the route; runs only `scan`, `enable` and `disable` (each reviewed) and `permissions --json` (read-only, never `--grant`). Synthetic fixtures only (see above). |

### Gateway HTTP client

At the source commit the authenticated routes are behind `tokenAuth` and then `apiCSRFProtect` (`internal/gateway/api.go`). The CSRF gate
**lets GET and HEAD through** and requires a non-empty `X-DefenseClaw-Client` on every other method; the value is not compared (any text will
do), except on one internal route that wants a fixed value and that the app does not call. The token may be sent as `Authorization: Bearer`
(what the app does), `X-DefenseClaw-Token` or `X-DC-Auth: Bearer`. `GET /health` is exempt from authentication, as in 0.8.10.

The app only ever GETs, so it already worked. It now **always sends `X-DefenseClaw-Client: defenseclaw-win`**, so a gateway that tightens the
gate to cover reads does not break it. Is that safe on 0.8.10? An unauthenticated `GET /health` carrying the header was answered 200 by the
installed 0.8.10 gateway, and an HTTP server ignores a header it does not read; the authenticated routes of 0.8.10 were not exercised with it
(that would need the token), so this is "tolerated by every check that did not need the token", not a proof for each route.

Other REST facts, all parsed correctly:

- `/health` carries `routing`, `ai_runtime`, `acp` (with `schema_sha256`, `scoped_token_ready`) and a richer `telemetry.details` (per-destination
  `queue`, `queues`, `signal_health`, `circuit_state`); a fresh install has neither `connector` nor `connectors`, which read as empty.
  `provenance.schema_version` is 7, `binary_version` is `1.0.0`.
- `/skills` and `/tools/catalog` answer HTTP 502 `{"error":"gateway: not connected"}` without an OpenClaw upstream; the client reads that as
  *not connected*, which is what the panels show.
- `/v1/acp/*` answers 401 even with the gateway token (they use their own signed-request auth); the app does not call them.
- `/api/v1/ai-usage/runtime` **exists** at this commit and answers `{"enabled":false,"findings":[],...}`; the AI Discovery panel already reads
  that as "Off". It is a 404 on 0.8.10, which the panel also handles.

### CLI output

- `--version-json`: `{"name":"defenseclaw-cli","schema_version":1,"version":"1.0.0"}`; the gateway adds `commit` and `built`. Same keys for the CLI as 0.8.10.
  An installed version above the newest published release reads as up to date.
- `status --json`: same keys the Overview reads (`environment`, `deployment_mode`, `data_dir`, `sandbox`, `enforcement`, `activity`, `sidecar`,
  `application_protection`, `connectors[]` with `fail_mode`). New top-level blocks (`semantic_routing`, `hook_guardian`, `native_otlp_delivery`,
  `scanners`) are ignored.
- `doctor`: `--json-output` gives `schema_version` 2 with `checks[]` (`check_id`, `section`, `status`, `label`, `detail`, `reason_code`,
  `remediation`, `duration_ms`), `repair_summary` and `repairs`. `doctor_cache.json` is the same document plus `captured_at`. The Overview reads
  `passed/failed/warned/skipped`, `checks[].status/label/detail` and `captured_at`, all present. The labels the app keys on (`Sidecar API`,
  `Guardrail proxy`, `credential <NAME>`) still exist.
- `keys list --json`: same row keys; `requirement` is now upper case (`NOT_USED`). The parser lower-cases it, as it did before.
- `guardrail status`: a `Block/alert` column sits between `Rule pack` and `HILT`, and when the table would be wider than the terminal the CLI prints
  one block per connector (`- Claude Code`, then `key:`, `state:`, `mode:`, `fail:` ...). The table is read by the dashed rule so the extra column is
  harmless, but the block layout produced no rows (the Setup hub fell back to the raw text). **`GuardrailStatusParser` now reads the block layout
  too.** `guardrail status --json` now exists; the app does not use it.
- `policy list/show --json`, `observability plan --json`, `agent discovery status --json`: no app reader depends on them except
  `agent discovery status` (parsed by the AI Discovery panel; a fresh install reports the service off).
- Removed: `migrations status` (replaced by `migrate --check`). The app never runs it; it is still in the read-only allow-list (harmless: a command that
  does not exist cannot run).

### Audit database (schema 8, 53 migrations)

Compared with the 0.8.10 DDL kept in `Fixtures/audit-schema.sql`: **nothing renamed or removed**; 9 new tables (`finding_scopes`, `finding_states`,
`guardrail_chain_*` x7), one new column (`scan_findings.finding_fingerprint`) and 22 new indexes. `audit_events`, `activity_events`,
`alert_acknowledgement_projection`, `network_egress` and `judge_responses` keep their columns, including `bucket`, `event_name`, `source`, `signal`.

How the readers fare:

- Every reader probes the columns it needs with `pragma_table_info` and works from whatever exists, so the additions are invisible to it.
- To check the query plans the Core suite pins (index use, no temp B-tree) as well, `TestAuditDatabase` can build every database in the suite from another DDL:
  `DEFENSECLAW_TEST_AUDIT_SCHEMA=runtime-95159fd/audit/audit-schema.sql dotnet test DefenseClaw.Tests -c Release`. Run that way, **all 1632 Core tests
  pass** against the 53-migration schema. CI runs the default (0.8.10) schema; the newer one is covered there by `Runtime95159fdCompatTests`.
- Timestamps are written with nine fractional digits (`2030-01-15T10:01:00.123456789Z`) and parse; the reader rounds to .NET's 100 ns tick.
- `activity_events` is **no longer written**. Operator changes are `audit_events` rows (`bucket = compliance.activity`, `action = config.change.applied`,
  actor `cli`, `tui:operator` ...). The Mutations reader already merged both sources, so the list is populated from the audit rows.
- Migration 33 ("privacy: purge pre-cutover audit evidence") **deletes every row of `findings`, `scan_findings`, `scan_results` and `audit_events`** when an
  older database is opened by that gateway, then compacts the file. This is the runtime's behaviour, not the app's: after such an upgrade the Audit,
  Alerts and hook-total views start from zero, and on a large database the first start is slow and needs disk for the rollback copy. The readers
  cope with an empty database; nothing in the app compensates for the lost history.
- Event stream filters `signal = 'logs'`; the writer fills `signal` with `logs` for every row, so nothing is hidden.
- `judge_bodies.db` has the schema the Judge history reader already probes (`raw_response`, `timestamp_unix_nano`), plus two cutover tables it ignores.

### `config.yaml`

`config_version` is 8, the same as 0.8.10. Every key the app reads exists under the same name (`gateway.api_port`, `token_env`, `token`, `host`,
`port`; `guardrail.connector`, `enabled`, `scanner_mode`, `detection_strategy_completion`, `connectors.<name>.mode/hook_fail_mode/block_message/rule_pack_dir`;
`claw.mode`; `llm.api_key_env`; `cisco_ai_defense.api_key_env`; `ai_discovery.*`; `observability.local.path/judge_bodies_path`). New keys
(`gateway.api_bind`, per-connector `block_at`, `alert_at`, `hilt`, `enabled`, `ai_discovery.runtime`, `include_user_email`, ...) are ignored by the reader
and survive an edit, because the editor replaces whole top-level blocks and leaves every other byte alone. A fresh install writes only its overrides
(about 230 bytes); the effective configuration (`config show`) is 83 KB.

### Install layouts

| Layout | Where | Resolved by |
|---|---|---|
| Setup (preferred) | `%LOCALAPPDATA%\Programs\DefenseClaw\bin` | PATH, then `DefenseClawPaths.BinDirectory` |
| Installer script (`scripts/install.ps1`) | `%USERPROFILE%\.local\bin` (`defenseclaw.exe`, `defenseclaw-gateway.exe`, `defenseclaw-hook.exe`), Python environment in `<data dir>\.venv` | PATH, then the new `FallbackBinDirectories` |

The script puts `.local\bin` on the user PATH, so a fresh terminal and, after a PATH refresh, the app find it; the new fallbacks cover the time
before that (and a venv-only `defenseclaw.exe`). PATH order still decides when both exist and are on PATH; with neither on PATH, the Setup directory is
tried first. The fallbacks apply only when the caller did not name a bin directory, so no existing test changed.

The gateway-peer check (the app sends the bearer token only to a process that is `defenseclaw-gateway` running from an install directory) knew only the
Setup directory and wherever PATH resolved the gateway. It now also trusts `.local\bin`, so a gateway started by the script is trusted even when PATH does not carry it.
The app does not resolve a HookRuntime directory itself (nothing reads it), so nothing changed there. The Settings page's "found in the install
directory" note still says "on PATH" for a fallback hit; that text belongs to the runtime-selector work and was left alone.

### Setup tile grid

The roster comes from `defenseclaw setup --help`: 35 commands at this commit (the Windows rendering marks `openclaw`, `openhands` and `zeptoclaw` as
`<Name>: unsupported on windows.`, which the catalog reads as unsupported). The parser reads every captured page, including the new shapes: an option
with four spellings (`-y, --yes, --non-interactive, --accept-defaults`), `--workspace, --workspace-dir`, and lower-case choice lists
(`--hilt-min-severity [high|medium|low|critical]`). New targets (`acp`, `redaction`, `routing`, `rotate-token`, `trusted-paths`, `notifications-set`,
`provider`) appear as generated cards; none has a curated layout.

### Command classifier and fixed argv

The tree has 263 leaves (0.8.10: 179). `CommandTierPinnedTreeTests` runs the 0.8.10 tree checks over it. The classifier called 11 new leaves read-only (`acp status`,
`agent discovery runtime status`, `guardrail protection list`, `sandbox doctor/image list/list/pack list/pack show/pack validate/policy show/status`);
each one's help was read and they only list, show, validate or probe. None is on the app's no-review allow-list, so none runs without review; no
mutating command became read-only. The 47 fixed argv the app builds (`ArgvContractTests`, the Runtime panel's seven among them) all name existing commands with existing options.

### Policies: the policy model (CUST-293)

The Policies panel is two surfaces behind one panel. A runtime whose probe finds `RuntimeCapability.PolicyModel` (`guardrail --help` lists
`protection`) gets the model panel (`PolicyModelViewModel`, `PolicyModelView`); 0.8.10, a runtime that has not been probed, one whose probe failed
and any runtime that lacks the marker get the CUST-281 table of named policies, **untouched**. The choice is made when the panel is first used (the
first visit waits for the app's own first probe) and again whenever the probe's answer changes while the panel is on screen. A panel built with an
explicit backend (`IPolicyBackend.Surface`) keeps that one.

**Six views, not seven, on Windows.** The runtime's model (`PoliciesPanelModel`) has seven views, and drops the last, Sandbox packs, when
`openshell_sandboxes_supported()` is false: any host that is not Linux or macOS (`platform_support.py`; the CLI's `sandbox` group exits 3 there, and
`sandbox --help` says "Linux, and macOS on Apple silicon, only"). Windows is on the dropping side of that one rule, so the app shows posture, opt-in
packs, chains, rule families, policies and rule packs, has no sandbox view and no placeholder for it, never starts a `sandbox` command to build the
panel, and ignores sandbox data should a model document carry it (the Phase 2 capture does: its seventh view is kept as captured and not read).
A container runtime on Linux would show seven upstream; this build shows the six there too.

**Data source: the CLI's JSON, not the runtime's Python bridge.** The Mac app asks the runtime's Python model for its catalog through a bridge. This
app starts no Python of its own: it reads four commands that exist at the pin and print JSON, all read-only, run through one door that refuses
anything else (`PolicyActionGuard.IsAllowedRead`):

| Command | Gives |
|---|---|
| `policy list --json` | the named policies, with their LLM thresholds, which is active, built-in or custom |
| `guardrail list-packs --json` | the global rule pack, each connector's pack (own or inherited) and every pack on disk |
| `guardrail protection list --json` | the opt-in packs with their rules, and the packs enabled per scope |
| `config show --section guardrail --format json` | mode, human approval and the block / alert levels, global and per connector |

and three files from the runtime's own install, never written: the built-in chain catalog (`tool-chains.json`), the rule files of a pack (rule
families) and a composed pack's `defenseclaw-pack.json`. They are found from the path the runtime prints for a built-in policy; where they cannot
be reached (a container runtime) those views show the "not found" state. A part that cannot be read empties only its own views and turns the read
into a partial one (below).

The model itself is a port of the runtime's pure code, not new wording: `policy_state.py` (scopes, the rows of each view, what "weaker" means) and
`policy_catalog.resolve_levels` (a connector's own level, else the global one, else its rule pack's profile; the alert level clamped to the block
level), and the consequence text of each change from `policy_panel.py`'s modals. `PolicyModelTests` check it against the rows the runtime itself
rendered in the capture (all six views, at the width it rendered them) and against the cases the runtime's own tests pin. Each scope's posture is
composed from the three scope-aware commands above (`PolicyPostureComposer`), because no command prints it whole.

**Tool-call levels and LLM thresholds are different things** and the panel keeps them apart. Posture shows and changes a scope's block and alert
level for *tool calls* (`guardrail block-at|alert-at`, per connector or global); the Policies view shows and changes a named policy's thresholds for
*LLM traffic through the guardrail proxy* (`policy edit guardrail --block-threshold|--alert-threshold`). The columns say which (`Blocks at` against
`LLM block`), and every consequence says which it is about.

**Changes** are limited to `guardrail mode | block-at | alert-at | hilt | use-pack | protection`, `policy activate` and `policy edit guardrail`, each
in the exact argv shape `PolicyActionGuard.IsAllowedChange` checks. Every one goes through the shared review (the exact command, the tier, the
gateway-restart bar the `guardrail` verbs need, the runtime's own account of what it does and leaves alone); one the model calls weaker needs the
acknowledgement tick before the confirm button works. A rule pack is validated (`guardrail validate-pack FOLDER --json`, a read) before `use-pack` is
offered and must say valid: invalid, validator unavailable and did-not-complete all stop it. `policy validate` runs before `policy activate` the same
way. A folder the CLI would rewrite (`%VAR%`, `~`, wildcards expand in every argument on Windows) is refused before anything runs.

**When changes are off.** The data must be a complete, recent read (`CatalogTrust`, as in the other panels): a partial read (a part failed), a
refresh that failed (the last good rows stay, labelled), a read older than its window, or a `config.yaml` / `.env` that changed since the read (checked
again at the moment of the request) turns every change off with the reason as the button's tooltip. Reads stay on.

## Not verified

- The model panel against a running pinned runtime. Everything above is built on the fixtures: the single-connector scenario is the Phase 2 capture;
  the two-connector scenario is synthetic. `guardrail protection enable|disable`, `use-pack`, `hilt`, `block-at|alert-at` and `policy edit guardrail`
  were never run (the app never starts the pinned runtime; its commands restart a gateway). The posture composition was not cross-checked against a
  runtime that has several connectors.
- Chains and rule families read the runtime's own data files; their location is derived from a built-in policy's path, which was seen only in the
  capture. A different install layout shows those two views as "not found" rather than guessing.
- A runtime with a connector, findings, scans, hook traffic or history: the readers were exercised with synthetic rows, not with rows from a populated install.
- A populated runtime-plane snapshot from a Windows run, and the exact reason text the gateway reports for plane C while unelevated (the Runtime panel's fixtures are built from the emitting code and show the text as data, so a different sentence changes nothing but the words).
- `inventory.db`, `gateway.log` / `gateway.jsonl` formats, the Logs panel's tailing, and the agent-config files the runtime plants for a connector.
- Upgrade and rollback of a real installation (the in-app updater assumes release assets that this source has no release for).
- Whether the authenticated 0.8.10 routes accept the new header (see above).
- Credential entry (`keys set`, `setup <connector>` with secrets) was not run with that source.
- A Windows run was captured only for the CLI (no connector, gateway on a different port); the REST fixtures are from a Linux container run of the same source.

## Re-running

```
dotnet test DefenseClaw.Tests -c Release --filter "FullyQualifiedName~Runtime95159fdCompatTests|FullyQualifiedName~CommandTierPinnedTreeTests"
dotnet test DefenseClaw.App.Tests -c Release --filter "FullyQualifiedName~Runtime."
# the Policies model (Core model and readers, then the panel)
dotnet test DefenseClaw.Tests -c Release --filter "FullyQualifiedName~PolicyModel|FullyQualifiedName~PolicyLevels|FullyQualifiedName~PolicyCatalogRead|FullyQualifiedName~PolicyAction"
dotnet test DefenseClaw.App.Tests -c Release --filter "FullyQualifiedName~Policies"
# whole Core suite against the 53-migration audit schema
DEFENSECLAW_TEST_AUDIT_SCHEMA=runtime-95159fd/audit/audit-schema.sql dotnet test DefenseClaw.Tests -c Release
```

To refresh the fixtures for a newer commit, capture the same commands from a fresh install of it, replace the identity values the way the existing files do
(paths under `C:\Users\operator`, synthetic ids and digests, one fixed date), and run the tests; the hygiene test fails if a machine name, a home path or a
token-shaped string is left.
