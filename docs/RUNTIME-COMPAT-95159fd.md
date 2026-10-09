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
| `rest/` | `health`, `status`, `alerts`, `guardrail-config`, `enforce-blocked/-allowed`, `mcps`, `skills-not-connected`, `tools-catalog-not-connected`, `ai-usage` (the disabled answer), `ai-usage.populated.synthetic` (*), `ai-usage-runtime` (the disabled answer), `ai-usage-runtime.populated.synthetic` (*), `ai-usage-runtime.degraded.synthetic` (*), `ai-usage-runtime.planes-ab.synthetic` (*), `unauthorized` | the gateway's GET routes |
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
`rest/ai-usage.json` (CUST-310) is the Docker capture of `GET /api/v1/ai-usage` for a service that is off, byte for byte (line endings aside); `rest/ai-usage.populated.synthetic.json`
is **synthetic, not a capture** - no populated report exists from a Windows run, so it is written by hand from the emitting code: the four members and their order from `handleAIUsage`
(`internal/gateway/ai_usage.go:37-71`), every signal member from `AISignal`, the model block from `LocalModelInfo` and its lineage from `LocalModelProvenance`
(`internal/inventory/ai_discovery.go:276-424`), the summary from `AIDiscoverySummary` (`:448-468`), the detectors' own values (`model_file`, `model_api`, `model_runtime`; a loaded model is
`model_runtime`, an installed one listed by a server `model_api`; a model file carries an owner, a modality, a relevance and a discovery confidence, a server's listing none of those,
`ai_model_files.go:2158-2197`, `ai_model_api.go:494-499,1033-1224,1364-1421`), and the sort of the signals (`sortAISignals`, `ai_discovery.go:4854`). Thirteen signals: two products with the per-signal scores the answer adds, ten
local models of every kind the panel has a rule for (see `AiUsageFixtureTests`), and one gone model, which the state file would not carry. Names, applications, models and hashes are
invented (`Example Notes`, `example-labs/chat-3b`, ids and digests made of zeros and a counter, 2030-01-15); there is no path in it, as in a real answer, where the gateway clears
`raw_path` from every evidence row (`SanitizeEvidenceForWire`) whatever `ai_discovery.store_raw_local_paths` says. Because nothing can compare the file with a live answer, a test holds every
value in it to the rules the same source applies to a report it is given (`ValidateSanitizedAIDiscoveryReport`, `validateLocalModelProvenance`): the allowed categories, statuses,
relevances, provenance sources and confidences, the seven countries of the publisher catalog, a derivation that agrees with its flags, digests of 64 hex digits, no separator in a basename.

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
| AI Discovery models: owner, relevance, discovery confidence, lineage (`GET /api/v1/ai-usage`) | **Added**, shown by presence (CUST-310): Owners, Relevance and Confidence columns, the Mac's recommended / all scope, an inspector Provenance block and the `model-lookup=` chip. 0.8.10's data and fixtures are untouched and give the panel they always gave. See below. |
| Inventory: AI BOM browser (`aibom scan --json`) | **Added**, shown by presence (CUST-275): Summary and one table per kind with the TUI's detail fields, the `--only` scope chips, a Connector column. A newer runtime's extra members, a Tools tab, a Rules count and the "not collected" marks appear when the output has them; 0.8.10's output gets what the TUI shows it. See below. |

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
- `policy list/show --json`, `agent discovery status --json`: no app reader depends on them except
  `agent discovery status` (parsed by the AI Discovery panel; a fresh install reports the service off).
- `observability plan --format json` (CUST-272): the Overview's Observability card reads it (`ObservabilityPlanReader`, one run on a pool thread, 30 s limit,
  reused for 5 minutes). Its emitter (`commands/cmd_observability.py`) is the same code in 0.8.10 and at the pin and prints the same document -
  `{basis, config_version, plan_digest, network_validation, delivery[], connector_export_custody, rows[]}`, a row per `(bucket, signal, destination)` with the
  compiled plan's decision - so one parser reads both by presence. The pin differs only in `observability destination test` (a `result:` line and the
  network-path note) and a note under the custody table, neither of which the app reads. The document names destinations and never addresses: the
  address, the retention window, the local files and the judge-body setting come from `config.yaml`, and what each destination is doing from `/health`.
  Fixtures: `cli/observability-plan.json` (the capture, one local store) and `runtime-0.8.10/cli/observability-plan.destinations.synthetic.json`
  (**synthetic, not a capture**: the emitter's own plan functions, copied unchanged into a scratch script and fed an invented effective plan - the local store,
  an OTLP destination that takes everything, a Splunk one with advanced routes whose severity-constrained route the plan cannot settle, a disabled
  HTTP one, and a metrics-only Prometheus one - with `example.test` names and a digest of zeros).
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

The Splunk dashboards (CUST-317) are not a roster entry on either runtime: `setup splunk dashboards plan | apply | destroy` is a nested group, so the
hub derives its card from the `splunk` one. Nothing about it is gated by runtime, because nothing differs: both trees list the same three leaves with
the same options (`CommandTierSplunkDashboardsTests`), and at this commit `cli/defenseclaw/commands/cmd_setup_splunk_o11y_dashboards.py`, `bundles/splunk_o11y_dashboards/terraform/main.tf`
and `detectors.tf` are identical to the installed 0.8.10's, line endings aside (the module's `required_version = ">= 1.5.0"` is what the Terraform look checks).

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
refresh that failed (the last good rows stay, labelled), a read older than its window, or a `config.yaml` / `.env` that changed since the read (a stat of
both files, never their content, checked again at the moment of the request and when a review is confirmed; `docs/PARITY-FOUNDATIONS.md` section 9) turns
every change off with the reason as the button's tooltip. Reads stay on.

### AI Discovery models: owner, relevance, confidence, lineage (CUST-310)

**What the pin adds.** The model block of a local-model signal (`LocalModelInfo`, `internal/inventory/ai_discovery.go:282-303`) gains four members:
`owner_application`, `relevance` (`primary`, `supporting`, `embedded`, `unknown`), `discovery_confidence` (a pointer: absent is "not reported", an explicit 0 is a score)
and `provenance` (`publisher`, `country_code`, `root_model`, `base_models`, tri-state `quantized` and `distilled`, `quantization`, `derivation`, `source`, `confidence`), and
`GET /api/v1/ai-usage` answers with `lookup_model_provenance_online` beside `enabled`, `summary` and `signals`. The state file (`aiStoredSignal` embeds `AISignal`) and
`inventory.db` (`model_json` is `json.Marshal(sig.Model)`, `store.go:595-600`) carry the same block, so the app reads it from all three; the route also carries per-signal identity and
presence scores, evidence rows without raw paths, and the gone signals of the scan that noticed them.

**0.8.10 carries none of it.** Learned from code, never from the live gateway. 0.8.10's own readers of the route (`commands/cmd_agent.py:283-300` and `:1307-1334`, `gateway.py:247`, and the
TUI's `AIUsageModel.from_mapping`) take the model members `id`, `status`, `format`, `provider`, `recipe`, `modality`, `device`, `size_bytes` and `pinned` and nothing else, and the snapshot's
`enabled`, `summary` and `signals`; and the installed `defenseclaw-gateway.exe`, searched as text and never run, contains none of the JSON names `owner_application`,
`discovery_confidence`, `lookup_model_provenance_online`, `country_code`, `root_model`, `base_models`, `quantization` or `distilled` (the six hits of the word `relevance` are the LLM-provider SDK's
rerank score). So the route of a 0.8.10 gateway does not carry these fields, nothing of them is shown for it, and its view is unchanged - which the tests prove with an answer built from the
0.8.10 state fixture the way its code builds one (`DiscoveryData.ReportOf0810`: evidence off the wire, scores on the component's signal, no new member, no `lookup_model_provenance_online`).

**The gate is the payload.** There is no marker for it in a help screen, so it is not a `RuntimeCapability`: a column, a filter or a chip exists only when some model carries what it shows
(the Mac has no probe for this panel either: what it shows depends on the payload). The gateway's per-signal scores and gone signals, which a 0.8.10 gateway sends too, are **not** taken from the route: the files stay the
list (cards, rows, states, evidence), and the route only fills in the four members for the signal with the same id and the same model, so a report adds no product, no model and no gone row.

**Reading.** One authenticated `GET /api/v1/ai-usage` per load of the panel (first visit, Refresh, a finished action, a stale return; never on a timer), through the gateway client the other
routes use (bearer token, `X-DefenseClaw-Client`, only to a verified gateway process), and **bounded** (`GatewayClient.GetBoundedJsonAsync`): at most 4 MiB (the Mac's limit; an announced
length over it is refused before a byte is read, a chunked body is dropped as it passes it), the client's timeout over the body as well as the headers, and at most 8,192 signals parsed. Every way
the read can end is a state of the Sources card (off, unreachable, token refused, not connected, no route, too large, not a report, failed) and never of the page; a failed read takes back what
an earlier one added. `ReadUsage` is the seam tests use, so none opens a socket.

**The recommended / all scope** is the Mac's `AIModelDiscoveryFilter`, case for case (`AiDiscoveryModelFilterTests`): with "Show all models" off a model is listed when it is at least 80%
confident (its reported discovery confidence; else, for a model no model server listed, its strongest detection score) and is primary, or is a supporting speech, audio, vision or embedding
model that an application owns; a model a local model server listed with no confidence of its own is always listed; choosing a Modality or a Relevance lifts the primary / supporting rule (the
80% floor stays, as in the Mac's code) and applies the choice as asked. **A legacy snapshot is forced to show-all:** when no model carries a confidence, an owner or a relevance - every model
0.8.10 reports - there is nothing to separate a primary model from an embedded artifact, so nothing is hidden and the switch is not offered. The Windows panel's own 80% Confidence picker stays and
composes with the scope; it cuts the number the column shows. The column says "93%" for a model's own confidence, "API" for a server's listing that has none (only where the runtime classifies
its models: a 0.8.10 view of a server-listed model has always said "NN% signal" and still does) and "NN% signal" for the score of a match.

**Columns, inspector and diagnostic.** The table gains Owners and Relevance when some model has one (Modality already worked that way); rows are merged across detectors on the case-folded, trimmed
model id; the inspector gains Owners, Relevance, Discovery confidence and a Provenance block (publisher, country as name and code - the Windows font has no flag glyphs - root model or "ambiguous (N)",
base models, derivation, quantized and distilled only when the runtime said, source, lineage confidence); the line of counts gains the TUI's last header part, `model-lookup=online` or `offline`,
only when an enabled gateway said so (a gateway that does not send the member is neither).

**Raw local paths.** The panel never reads `raw_path` or `raw_paths`. A value of the newer text fields (owner, publisher, root and base model, quantization, derivation, source) that looks like a local
path - a drive path, a UNC or backslashed path, `file:`, `~/`, `/x`, `%VAR%/x` - is shown as "(local path hidden)" unless config.yaml says `ai_discovery.store_raw_local_paths: true`, the runtime's own
switch for keeping them, and those values are also cut to 512 characters and have their control and bidirectional characters spelled out.

### Inventory: the AI BOM browser (CUST-275)

**What it is.** The Inventory page's second view (AI BOM, beside Components) browses what `defenseclaw aibom scan --json` printed, the way the TUI's Inventory
panel does (`tui/panels/inventory.py`, `tui/services/inventory_state.py`): a Summary and a table per kind (Skills, Plugins, MCPs, Agents, Models, Memory; Tools
when some connector lists one) with the TUI's columns, a detail pane with its fields in its order and labels, the Skills and Plugins status chips, a Connector
column when the scan covered several connectors, and the TUI's `--only` scope chips. Generating it is still the reviewed, state-changing command it always was
(a scan records an audit event and posts to the gateway); nothing runs on open. On a managed or invalid installation (CUST-308) both Generate buttons are off with the
installation's own sentence as their tooltip, and a review reached another way carries the read-only bar and cannot be confirmed; browsing a BOM already on the page,
Save JSON, search, the tabs and the scope chips stay on, because they read. `Core/Inventory/InventoryBomSnapshot.cs` parses and keeps it, bounded.

**Bounds.** Output over 4 MiB (the Mac's parse limit) is not parsed (`Too large to display: aibom scan output is 5.2 MB, over the 4 MB limit`); the run keeps its
whole output (`RetainFullOutput`, the runner's 200,000-line / 16 MiB ceiling), and output the runner still had to cut is not parsed either, because what is left
starts in the middle of the document. At most 5,000 rows of a kind per connector are kept (the summary's count still says how many there were), 24 extra members
per row, 512 characters per value. The raw JSON is not kept (Save JSON writes the run's own output). The TUI has no such limit and no such message; the wording is
the audit and mutation readers' (CUST-284).

**A connector that cannot be used is skipped and named** (an entry that is not an object, or not an inventory), where the TUI drops it without a word; one whose
own `errors` name failed commands is kept, with what it did list, and each failed command is named in the Summary's coverage notes.

**By presence, never by version.** The newer source (95159fd) adds to the output: `version` 4, a `rules` list and `summary.rules`, a `collected: false` mark on the
categories an `--only` run left out, plugin rows with `source_kind` and `enabled` instead of `origin` and `status`, a tool's `kind` and `description`, a skill's
`scan_eligible` and the `discovery-only` verdict, and per-connector `connector_rule_files` / `connector_policy_settings` (found by reading
`claw_inventory.py`, `cmd_aibom.py` and `tui/services/inventory_state.py` of both trees). Nothing here asks which runtime printed the output: a row's member that the
TUI has no field for is listed under "Also in this scan" when the row has it (so a tool's kind, a filesystem connector's `entry_count`, `base_url` and `kind` show up
exactly where the TUI shows blanks), a Rules row appears when the summary has `rules`, a kind marked not collected says so, the Tools tab appears when a tool was
listed, and a plugin with no `origin` or `status` shows its `source_kind` and its enabled state as the newer TUI does. Free text a row carries (an MCP server's
command line and URL, descriptions, every extra member) has its credentials masked first (`DisplayRedaction`) and its control and bidirectional characters spelled out
(`DisplayNames.Visible`).

**Fixtures** are synthetic and written from the emitting code, not captured (a run writes an audit record): `aibom-scan.openclaw.synthetic.json` (0.8.10's OpenClaw
path), `aibom-scan.95159fd.synthetic.json` and `aibom-scan.95159fd.only.synthetic.json` (the newer source's connectors and an `--only` run) and
`aibom-scan.partial.synthetic.json` (entries that cannot be read, a connector with failed commands), in `DefenseClaw.App.Tests/Fixtures/CliPayloads`, beside the 0.8.10
ones; `runtime-95159fd/cli/aibom-scan.json` is the capture of a fresh install of the newer source (`[]`: no connector).

## Not verified

- An `aibom scan --json` run of either runtime with a connector: its shape is taken from the code that prints it (see above); a populated BOM, the bound and the
  masking were exercised with synthetic output only.
- A populated `GET /api/v1/ai-usage` from a Windows run, and the recommended view against a running pinned runtime: `ai-usage.populated.synthetic.json` is built from the emitting code and held
  to that source's own validation rules, not compared with an answer. The online lookup being on (`lookup_model_provenance_online: true`) was never observed. The 0.8.10 gateway's answer was
  learned from its Python consumers and a text search of its binary, not from an answer of its own.
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
# AI Discovery models: the bounded read, the fixtures, then the filter, the fields, the report and the panel (CUST-310)
dotnet test DefenseClaw.Tests -c Release --filter "FullyQualifiedName~GatewayClientBoundedReadTests|FullyQualifiedName~AiUsageFixtureTests"
dotnet test DefenseClaw.App.Tests -c Release --filter "FullyQualifiedName~Discovery"
# whole Core suite against the 53-migration audit schema
DEFENSECLAW_TEST_AUDIT_SCHEMA=runtime-95159fd/audit/audit-schema.sql dotnet test DefenseClaw.Tests -c Release
```

To refresh the fixtures for a newer commit, capture the same commands from a fresh install of it, replace the identity values the way the existing files do
(paths under `C:\Users\operator`, synthetic ids and digests, one fixed date), and run the tests; the hygiene test fails if a machine name, a home path or a
token-shaped string is left.
