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

