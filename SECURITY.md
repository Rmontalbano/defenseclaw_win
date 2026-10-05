# Security Policy

## Reporting a vulnerability

Please report a suspected vulnerability **privately**. Do not open a public issue, pull request or discussion for it, and do
not post details anywhere public until a fix is out.

Use GitHub's private vulnerability reporting on this repository: open the **Security** tab and choose **Report a
vulnerability**, or go straight to
<https://github.com/Rmontalbano/defenseclaw_win/security/advisories/new>. That opens a private security advisory that only
you and the maintainer can see, where the fix can be worked on and you can be credited if you want to be.

A useful report says which version (or commit) you tested, what you did, what happened, what you expected, and what an
attacker gains. A minimal reproduction beats a long write-up. Please do not include real secrets, customer data or anything
from a machine you do not own.

This is a spare-time project with one maintainer: expect an acknowledgement within about a week, and a fix or a clear answer
within about 90 days, sooner for anything severe. There is no bug bounty.

## What is in scope

This repository: the DefenseClaw for Windows companion app (`DefenseClaw.App`, the WPF application, and `DefenseClaw.Core`),
the release files it publishes, and its build and CI configuration. For example:

- the app running a command, writing a file or exposing a secret it should not: a secret on a command line, in a log, in the
  UI or on the clipboard; argument injection into the `defenseclaw` CLI; path traversal; unsafe handling of gateway, CLI,
  configuration or database content;
- getting around the app's design rules: state changes go through the `defenseclaw` CLI as visible, reviewed commands (the
  config editor is the one direct writer, of `config.yaml`, behind a hash-checked backup and CLI validation); the audit and
  inventory databases are only ever opened read-only;
- a tampered or unverifiable release (the exe, `checksums.txt`, the attestation), or a flaw in the workflows that would let
  untrusted code publish one;
- a vulnerable dependency that this app actually reaches and the NuGet audit did not catch.

## What is out of scope

- **The DefenseClaw runtime itself**: the `defenseclaw` CLI, the gateway and sidecar, their policies and scanners. Report
  those to [cisco-ai-defense/defenseclaw](https://github.com/cisco-ai-defense/defenseclaw) as its own SECURITY.md describes.
  If you cannot tell which side a problem is on, report it here and it will be routed.
- Vulnerabilities in .NET, Windows, WPF-UI or the other third-party packages themselves, unless this app uses them in a way
  that makes them exploitable. Those belong upstream as well.
- An attacker who can already run code as the signed-in user, or as an administrator, on the machine. The app is a local
  companion that runs as the signed-in user and reads that user's own files.
- A crash or hang on malformed local input with no security impact is an ordinary bug: a public issue is fine.
- Scanner output with no demonstrated impact, and social engineering of the maintainer.

The test projects contain obviously fake credentials (`synth...` values, `hunter2`, hosts under the reserved `example.com` and
`example.test` names, bare private-key header lines). They are inputs for the secret-detection tests and the repository's
secret scanning is configured to expect them (`.gitleaks.toml`, `.trufflehog-exclude.txt`). A **real** credential anywhere in
the repository or its history is in scope: report it privately in the same way.

## Supported versions

Security fixes go into the latest release and `main`. Older releases do not get backports: upgrade to the latest release.

| Version        | Supported |
| -------------- | --------- |
| Latest release | Yes       |
| Anything older | No        |

## Verifying a release

Each release publishes `DefenseClaw.App.exe` (a self-contained single file) and `checksums.txt`.

- **Checksum.** `checksums.txt` has one `<sha256>  <file name>` line per file. Compare it with
  `Get-FileHash .\DefenseClaw.App.exe -Algorithm SHA256`.
- **Provenance.** The release workflow signs a build-provenance attestation (Sigstore, using the workflow's own identity) for
  both files. `gh attestation verify .\DefenseClaw.App.exe --repo Rmontalbano/defenseclaw_win` confirms the file was built by
  this repository's release workflow from a tagged commit. Older releases, built before the attestation step existed, have
  the checksum only.
- The exe is not Authenticode-signed, so Windows SmartScreen may warn the first time it runs. The two checks above are how to
  tell a genuine release from a copy.

## How the repository protects itself

- **Workflows.** The token is read-only by default. Only the release job can write, and only for a version tag pushed to this
  repository, never for a pull request. Every action is pinned to a full commit SHA.
- **Dependencies.** NuGet audit covers direct and transitive packages on every restore and fails the build on a known
  vulnerability. A weekly workflow repeats the check when nobody has pushed.
- **Secrets.** gitleaks and trufflehog scan the whole git history on every push and pull request to `main`, and weekly.
- **SQL.** The audit and inventory readers build statements from fixed fragments and bind every value as a parameter; the few
  table and column names that cannot be bound are checked against the database's own schema and quoted. Where a scanner still
  reports an interpolated statement, the line carries a `nosemgrep` comment that says why it is safe.
