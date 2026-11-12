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
  It covers the 1.0 releases 1.0.0 to 1.0.8; a feature added by a later release is offered only when the installed runtime has it.
- DefenseClaw 1.0.7: the Decision model card (`setup decision-model`, including which connectors it answers alone); an Overview row
  when the audit store is over its size cap; a "not enforced" row for a connector the gateway did not start, with reviewed Remove and
  Restart; hook refusals during a gateway outage labelled in Logs and Audit; JUDGE-UNAVAILABLE-ESCALATED labelled wherever its rule id appears; the per-user
  launcher's own failure reason in Settings, Updates and About; Updates warnings for a linked data folder.
- DefenseClaw 1.0.8: Claude Cowork protection (an Overview row, a Setup wizard that builds the plugin and shows the upload steps in
  Claude Desktop, a readiness row, and the uninstall and reset warnings); Claude Desktop in AI Discovery; Cowork rows marked in Logs,
  Audit and Alerts; `openai-compatible` and `custom-openai` providers and `guardrail.judge.extra_body` in the config editor; OpenCode
  shown as supported; an Overview row for agent hooks still on the old PowerShell bridge; OTLP counters scope and replay on the
  Observability card; the upgrade-path warning and the config.yaml settings a rollback leaves behind.
- In-app upgrade to DefenseClaw 1.0, with an install-state panel showing what was changed and how to roll back.
- Runtime monitoring: gateway, audit and alert status kept current, with start-gateway buttons where the gateway is down.
- Administrator mode: an optional, separately confirmed helper (`DefenseClaw.GatewayAdmin.exe`) that starts, stops and restarts
  the gateway elevated, after showing its SHA-256 and asking Windows for one prompt.
- Restart the app from the post-upgrade bar and from the tray.
- Installer (`DefenseClawWin-X.Y.Z-Setup-x64.exe`, per user, no administrator rights), portable zip, `install.ps1` one-line
  installer, `checksums.txt`, and GitHub build-provenance attestations for every release asset.

### Security
- Hardening of the gateway connection (only the local gateway, owner-checked), of file and process handling, and of the
  repository itself: pinned CI actions, a read-only token everywhere except the release job, secret scanning, and dependency audits
  that fail the build.
- The app and its installer are not Authenticode-signed; releases are verified by SHA-256 and attestation (see `SECURITY.md`).

## [1.0.0]

