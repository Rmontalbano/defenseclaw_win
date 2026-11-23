# Changelog

All notable changes to DefenseClaw for Windows (community) are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

To cut a release: move what is under `[Unreleased]` into a new `## [X.Y.Z]` section, then push the tag `vX.Y.Z`. The release
workflow uses that section as the release's Highlights and fails when it is empty (it falls back to the tag's annotated message).

## [Unreleased]

### Added
- Feature parity with the Mac companion app (keitheobrien/defenseclaw_mac 1.1.26) and with the DefenseClaw 0.8.10 terminal UI:
  the same panels, setup flows, policy and inventory views, and runtime views, with every state-changing action reviewed and
  shown in the Activity panel before it runs.
- Support for DefenseClaw 1.0: the app detects which runtime is installed and only offers the features that runtime has.
  It covers the 1.0 releases 1.0.0 to 1.0.10; a feature added by a later release is offered only when the installed runtime has it.
- DefenseClaw 1.0.7: the Decision model card (`setup decision-model`, including which connectors it answers alone); an Overview row
  when the audit store is over its size cap; a "not enforced" row for a connector the gateway did not start, with reviewed Remove and
  Restart; hook refusals during a gateway outage labelled in Logs and Audit; JUDGE-UNAVAILABLE-ESCALATED labelled wherever its rule id appears; the per-user
  launcher's own failure reason in Settings, Updates and About; Updates warnings for a linked data folder.
- DefenseClaw 1.0.8: Claude Cowork protection (an Overview row, a Setup wizard that builds the plugin and shows the upload steps in
  Claude Desktop, a readiness row, and the uninstall and reset warnings); Claude Desktop in AI Discovery; Cowork rows marked in Logs,
  Audit and Alerts; `openai-compatible` and `custom-openai` providers and `guardrail.judge.extra_body` in the config editor; OpenCode
  shown as supported; an Overview row for agent hooks still on the old PowerShell bridge; OTLP counters scope and replay on the
  Observability card; the upgrade-path warning and the config.yaml settings a rollback leaves behind.
- DefenseClaw 1.0.9: a stuck pending connector write offers "Finish the interrupted change" on Overview (`defenseclaw-gateway connector recover-config`,
  reviewed) in place of moving the files aside, and the upgrade hint and pre-flight say the same; 1.0.8 and older are unchanged, and with the 1.0.9
  installer over an older runtime the card says that command needs 1.0.9. If the command cannot finish a change, the text says the recovery files stay in the
  data folder and the upgrade stays refused; the copyable command pastes in PowerShell and Command Prompt. The installer's "uv cache folder is N characters
  long" stop shows as a refusal with nothing changed and the app's fix (not the installer's terminal-only one), and a pre-flight "uv cache path" row warns first (Warn, not Block) and says to restart the app after setting `UV_CACHE_DIR`.
  The 0.8.x Setup leftover row explains what 1.0.9 `defenseclaw uninstall` and `init` do with a Setup install kept beside 1.0. Overview shows when the watcher
  turns admission scanning off for linked skill or plugin folders; the gateway's repair for a connector it did not start (copy only, never run); a hook launcher
  or guard that differs from the installer's recorded copy or has no record (from a recent doctor run); and that the host sensor's dropped-events count stays
  until the gateway restarts. The judge `max_parallel` hint is dropped for a local judge when the key is unset.
- DefenseClaw 1.0.9, Cowork: a plugin built by an older release (`cowork_plugin_stale`) is flagged on Overview ("Rebuild the Cowork plugin", also the AI Discovery button's text then), in Setup readiness
  and in the Cowork wizard, which leads to building it again and uploading the new zip. Removing the Claude Code connector (any of its names) warns in the review
  that Cowork stops being protected (1.0.8 and 1.0.9 each in their own words). The Cowork removal sentence follows the installed runtime in the uninstall and reset review, the Updates card's rollback
  note and the Add/Remove Programs leftover row (on 1.0.9 a plugin built by it lets calls run after the data folder is removed; older plugins may block them).
- DefenseClaw 1.0.9, rules: on 1.0.9, Alerts, Logs and Audit add a plain-words line beside the runtime's own rule title for `ACTION-OPERAND-BOUND`, `ACTION-SHARE-BODY-UNREAD`, `TOOL-PATH-UNVERIFIED`,
  `tamper.agent_hook_config_disable` and `tamper.agent_hook_config_write`, and an `ACTION-COMPOUND-LIMIT` row says whether the call ran or was blocked. Alerts mark
  Cowork calls listed only by the runtime. The protected-files card states 1.0.9's shell-write floor and self-protection. Rule-pack levels follow
  `defenseclaw-pack.json` when present (read once per Policy refresh, its key matched as the gateway matches it), else the folder name.
- DefenseClaw 1.0.10, connectors: `setup -c`, `--detected` and `--all` add or update the connectors they name and keep every other one. On 1.0.10 the batch dialog
  starts configured connectors unticked and names only the ticked ones (`--connector` each; it does not offer `--detected` or `--all` there, which would give every connector they name,
  configured ones included, the chosen mode). A ticked configured connector reads "Set up X again (stays MODE)" at its own mode, a connector with no mode of its own shows the global
  `guardrail.mode`, the others are "Left as they were" in place of "will be dropped", and the dialog points to "Remove a connector…" for stopping one. Connector names from config.yaml
  are shown escaped and cut to 64 characters. A roster that is empty or holds OpenClaw or ZeptoClaw, and 1.0.9 and older, read as before.
- DefenseClaw 1.0.10, OpenClaw on native Windows (before 1.0.10 the CLI calls it unsupported there): the Setup OpenClaw card opens on what has to be in place, in the
  runtime's words (OpenClaw 2026.8.35 or later, `init --connector openclaw` on a new install, OpenClaw's own gateway running, the preflight command as text to copy,
  OpenClaw's gateway restarting after setup); the card follows a runtime probe that lands after it was loaded. OpenClaw runs alone: `setup openclaw` refuses while a hook
  connector is configured and `--replace` removes them all, so its review warns of that, is tiered Destructive on every review path (the wizard, Overview and the Activity rerun; the palette does not pass `--replace` on) and in any option order, and carries the "Cowork stops being protected" bar when Cowork was set up.
  The Gateway card, the config editor's connector pickers and the hook fail-mode rule text no longer call OpenClaw unsupported or proxy-based on Windows.
- DefenseClaw 1.0.10, Overview: a row for an agent that updated itself in place (Codex, Amp, Hermes, OpenCode; the gateway keeps running and does not start it: with several connectors it lists it as not started, with one it reports the guardrail in error), with the reviewed
  "Re-select Codex" for Codex and copy-only `defenseclaw setup <name> --yes` for the others (Critical when no other connector is enforced, else High); a High row when the configured OpenClaw plugin
  has not called in (not drawn for an OpenClaw the gateway did not start); an Info row when OpenClaw is found but not set up; the outcomes of a connector's hook when the gateway is down, does not
  answer or the hook errors (`status --json` `on_failure`); "Model routing" and "Tokenomics" rows on the Configuration card (state only; usage is the last 24 hours from `GET /usage`, and only when
  config.yaml turns tokenomics and its local store on); a Cascade row on the Effective policy card, and warnings when the gateway reports an invalid cascade (High when it says the fallback is weaker) or refuses a reload because of it. The tokenomics figure says when it was read if the gateway stops answering, and its read no longer delays the status read. The Overview blocks count leaves out shadow measurement rows. A malformed `openclaw_plugin` in `/health` is ignored instead of failing the health read.
- DefenseClaw 1.0.10, credentials: the gateway-token lookup skips `OPENCLAW_GATEWAY_TOKEN` and any value equal to it, as the gateway does, and no longer counts it as DefenseClaw's own token.
- DefenseClaw 1.0.10, commands: the palette uses the 1.0.10 command list (281 entries: 239 offered, 19 hidden by the runtime, 23 left out by the app: the 8 `substrate` commands and the 15 `routing`
  commands that change settings). `setup openclaw` is offered; the read-only `routing` commands (`status`, `models list`, `rules list`, `budgets list`, `pricing show`, `explain`, `usage`) and
  `routing test` are added, each reviewed first with a sentence saying what it does. The older command lists are unchanged. On 1.0.10 only nine `routing` settings ask for a gateway restart,
  and `routing test` is copy only; its sentence says the configured classifier receives the prompt. The Agents panel's Tokens and Cost tiles say where model usage is recorded. `tools/gen-tui-registry.py` takes an optional fourth `--routing` source.
- DefenseClaw 1.0.10, guardrail: Audit and Logs label the default-on shadow measurement rows (no action taken, left out of "Actionable only"), inspection timeouts (a scan timeout is actionable)
  and the cascade's "not applied as written" row; a hook refusal logged to `hook-failures.dropped` is labelled as a hook refusal, not a policy block. Alerts, Logs and Audit explain
  `ALIGNMENT-DRIFT` (saying when it only measured; a row the lane let through as an alert reads the same in every panel) and word `ACTION-COMPOUND-LIMIT` for commands, tool output and model replies; `ACTION-OPERAND-BOUND` keeps its label for old rows.
  Policies has a read-only Cascade block with an on-demand Explain (`defenseclaw guardrail cascade explain`), a severity-matrix caveat when a cascade is set, and CRITICAL shown at least
  at `admin.critical_floor`. The Decision model card offers the protocol (System One or Gemini Decision 1), no longer offers `--on-confident-block` on 1.0.10, and warns that a rollback needs
  `decision_model.protocol` removed, also when the CLI writes it from a Google endpoint URL; its "needs a URL and a model" bar counts what config.yaml already holds.
- DefenseClaw 1.0.10, rollback and upgrade: the rollback note and review warning "Settings made since the upgrade" name the settings the older release does not accept from a table of 67 keys
  (at most ten named, then a count; see [docs/UPDATING.md](docs/UPDATING.md)). When routing is on with Claude Code or Codex route-only and the older release has no route-only, the review says the installer
  puts their model settings back and those agents need a restart, and the result shows the installer's warning if it could not. When install.ps1 1.0.10 stops because Python packages still differ
  from their wheels after a fresh download, the card says nothing was changed and names the uv cache folder to delete before upgrading again. Both installer messages are recognised when the console wrapped them.
- In-app upgrade to DefenseClaw 1.0, with an install-state panel showing what was changed and how to roll back.
- Runtime monitoring: gateway, audit and alert status kept current, with start-gateway buttons where the gateway is down.
- Administrator mode: an optional, separately confirmed helper (`DefenseClaw.GatewayAdmin.exe`) that starts, stops and restarts
  the gateway elevated, after showing its SHA-256 and asking Windows for one prompt.
- Restart the app from the post-upgrade bar and from the tray.
- Installer (`DefenseClawWin-X.Y.Z-Setup-x64.exe`, per user, no administrator rights), portable zip, `install.ps1` one-line
  installer, `checksums.txt`, and GitHub build-provenance attestations for every release asset.

### Fixed
- Overview rule-pack rows (the connector table's "Rule pack" column, the scoped Configuration row, "Policy posture", the scoped policy row) and the strict-pack
  fail-mode note now name the pack the runtime picks: the connector's entry before the global one, and `rule_pack` before `rule_pack_dir`. Before, only a
  connector's `rule_pack_dir` was read, so a `rule_pack: strict` (what `defenseclaw setup` writes) showed "default". A folder whose `defenseclaw-pack.json` posture
  differs from its name shows it in brackets, for example "team (strict)". This is not limited to 1.0.9.
- AI Discovery no longer offers "Set up Cowork protection" on a managed install, where `setup cowork` is refused (1.0.8 and later).
- Setup > Routing no longer offers `--enable` from DefenseClaw 1.0.5 on, where the runtime refuses it on native Windows, and shows the runtime's sentence (on 1.0.10 it adds that route-only
  routing of hook connectors is separate and shown on Overview); `--disable` and `--status` stay, and 1.0.0 to 1.0.4 keep the card as it was. `setup routing --status` and a bare
  `setup routing` (which only print the state) no longer say they restart the gateway, on every runtime that has the command; `setup tokenomics` (1.0.10) is shown as not restarting it.
- Hook refusals the runtime logs to `hook-failures.dropped` (1.0.7 and later) are no longer counted as policy blocks in the Overview blocks count and the hourly activity, on every runtime.
- A rollback to a release before 1.0.3 or 1.0.6 now also names the settings added in those releases, as the runtime's own upgrade table does.

### Security
- Hardening of the gateway connection (only the local gateway, owner-checked), of file and process handling, and of the
  repository itself: pinned CI actions, a read-only token everywhere except the release job, secret scanning, and dependency audits
  that fail the build.
- The app and its installer are not Authenticode-signed; releases are verified by SHA-256 and attestation (see `SECURITY.md`).

## [1.0.0]

