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
  It covers the 1.0 releases 1.0.0 to 1.0.9; a feature added by a later release is offered only when the installed runtime has it.
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
  installer over an older runtime the card says that command needs 1.0.9. The installer's "uv cache folder is N characters long" stop shows as a refusal with
  nothing changed and its fix, and a pre-flight "uv cache path" row warns first (Warn, not Block) and says to restart the app after setting `UV_CACHE_DIR`.
  The 0.8.x Setup leftover row explains what 1.0.9 `defenseclaw uninstall` and `init` do with a Setup install kept beside 1.0. Overview shows when the watcher
  turns admission scanning off for linked skill or plugin folders; the gateway's repair for a connector it did not start (copy only, never run); a hook launcher
  or guard that differs from the installer's recorded copy or has no record (from a recent doctor run); and that the host sensor's dropped-events count stays
  until the gateway restarts. The judge `max_parallel` hint is dropped for a local judge when the key is unset.
- DefenseClaw 1.0.9, Cowork: a plugin built by an older release (`cowork_plugin_stale`) is flagged on Overview ("Rebuild the Cowork plugin", also the AI Discovery button's text then), in Setup readiness
  and in the Cowork wizard, which leads to building it again and uploading the new zip. Removing the Claude Code connector (any of its names) warns in the review
  that Cowork stops being protected. The Cowork removal sentence follows the installed runtime in the uninstall and reset review, the Updates card's rollback
  note and the Add/Remove Programs leftover row (on 1.0.9 a plugin built by it lets calls run after the data folder is removed; older plugins may block them).
- DefenseClaw 1.0.9, rules: Alerts, Logs and Audit explain `ACTION-OPERAND-BOUND`, `ACTION-SHARE-BODY-UNREAD`, `TOOL-PATH-UNVERIFIED`,
  `tamper.agent_hook_config_disable` and `tamper.agent_hook_config_write`, and an `ACTION-COMPOUND-LIMIT` row says whether the call ran or was blocked. Alerts mark
  Cowork calls listed only by the runtime. The protected-files card states 1.0.9's shell-write floor and self-protection. Rule-pack levels follow
  `defenseclaw-pack.json` when present, else the folder name.
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

### Security
- Hardening of the gateway connection (only the local gateway, owner-checked), of file and process handling, and of the
  repository itself: pinned CI actions, a read-only token everywhere except the release job, secret scanning, and dependency audits
  that fail the build.
- The app and its installer are not Authenticode-signed; releases are verified by SHA-256 and attestation (see `SECURITY.md`).

## [1.0.0]

