# Updating DefenseClaw for Windows

This page covers updating the companion app itself. The DefenseClaw runtime (CLI and gateway) is upgraded from the Updates window; see "DefenseClaw 1.0" in the README.
The app works with runtime 0.8.10 and 1.0.0 to 1.0.9. Updating the app does not change which runtime is installed; the app offers what the installed runtime has.

For a runtime upgrade to 1.0.8, the Updates pre-flight warns when the installed version is outside the range that release supports upgrading from (1.0.2 to 1.0.7)
and, from 1.0.7, when the data folder or `DEFENSECLAW_HOME` is a link, which the 1.0.7 launcher refuses. The upgrade still runs. A rollback puts back the
config.yaml saved at upgrade time; its note names the settings made since then that the older release does not accept.

For a runtime upgrade to 1.0.9, three more things apply:

- **A stuck pending connector write.** On 1.0.9 it is finished with `defenseclaw-gateway connector recover-config`, which Overview offers as "Finish the
  interrupted change" (reviewed before it runs). On 1.0.8 and older the way out is still to move the files aside, as the row says. With the 1.0.9 installer and
  an older runtime installed, the Updates card says the command needs 1.0.9.
- **The uv cache path.** The 1.0.9 installer stops with "uv cache folder is N characters long" and changes nothing when the uv cache folder path is longer than
  108 characters (uv needs 151 more characters for one package, and the total must stay under 260) and Windows long paths are off. The pre-flight shows a
  "uv cache path" row as a warning first. To fix it, set the user environment variable `UV_CACHE_DIR` to a short folder only your account can write, then restart
  this app (the installer reads the app's environment, so a variable set after the app started is not seen), or have an administrator turn on long paths.
- **A 0.8.x Setup install kept beside 1.0.** With 1.0.9, `defenseclaw uninstall` also removes it (its data folder is kept), but it stops this release's gateway and
  removes the connectors' hooks, so run setup again afterwards. `defenseclaw init` will not set up hooks while Setup's hooks remain in the agent's settings.

## Automatic checks

The app asks GitHub for the latest release:

- at launch, and then every six hours (the time of the last check is kept, so restarts do not re-check);
- on demand, from "Check for app updates…" in the tray menu or "Check for Updates…" in Settings.

The request is `GET https://api.github.com/repos/Rmontalbano/defenseclaw_win/releases/latest`. It uses ETag conditional requests, so an unchanged release costs no API quota.

A release is newer only when its version is greater than the running version (SemVer). Drafts and prereleases are ignored. A failed or unavailable check does not mean the app is up to date.

## What the app downloads and checks

When you accept an update, the app:

1. downloads `DefenseClawWin-X.Y.Z-win-x64.zip` and `checksums.txt` from the same release;
2. checks the zip's SHA-256 against `checksums.txt`, and against GitHub's asset digest when the API gives one; any mismatch stops the update;
3. checks the zip's entries: exactly `DefenseClaw.App.exe` and `DefenseClaw.GatewayAdmin.exe`, at the root, with no paths and nothing else;
4. checks that the new `DefenseClaw.App.exe` reports the release's version.

The executables are not Authenticode-signed. The trust is the SHA-256 match from the release's own checksums and GitHub's digest, over HTTPS.

## What the swap does

The app replaces its own files without a separate updater process:

1. The running `DefenseClaw.App.exe` is renamed to `DefenseClaw.App.exe.old`, and `DefenseClaw.GatewayAdmin.exe` to `DefenseClaw.GatewayAdmin.exe.old`.
2. The new files are moved into the app folder.
3. The app restarts through the same restart path it uses after an in-app upgrade.
4. On the next start, if the new version is healthy, the `.old` files are deleted.

If the new version cannot start, the `.old` files are the way back (see Rollback). The update is refused, with a reason shown, when the app folder is not writable. Running from a repository `publish\` folder is allowed, with a warning.

For an installer install, the Add/Remove Programs version is updated to match.

## Manual update

- **Installer:** run the new `DefenseClawWin-X.Y.Z-Setup-x64.exe`. It asks a copy of the app running from the install folder to close first (the app hides to the tray, so you may be asked to exit it from the tray icon), then installs over the old version. A copy running from another folder, such as an unzipped portable copy, is not touched: exit it yourself. Check the download first ([README, "Verify a download"](../README.md#verify-a-download)).
- **Portable zip:** close the app, unzip the new release over the folder, and keep both executables together. Check the download first.

## Rollback

Before the `.old` files are removed, the previous version can be restored by hand:

1. Close the app (tray menu, then Exit).
2. In the app folder, move the new `DefenseClaw.App.exe` and `DefenseClaw.GatewayAdmin.exe` to a folder outside the app folder, to keep them. Then rename `DefenseClaw.App.exe.old` to `DefenseClaw.App.exe`, and `DefenseClaw.GatewayAdmin.exe.old` to `DefenseClaw.GatewayAdmin.exe`.
3. Start the app.

Once the `.old` files are gone (after a healthy start of the new version), a rollback means installing the previous release again.

## Verify a download by hand

```powershell
Get-FileHash .\DefenseClawWin-X.Y.Z-win-x64.zip -Algorithm SHA256
Select-String -Path .\checksums.txt -Pattern 'DefenseClawWin-X.Y.Z-win-x64.zip'
gh attestation verify .\DefenseClawWin-X.Y.Z-win-x64.zip --repo Rmontalbano/defenseclaw_win
```

The two hashes must match. The attestation check needs the GitHub CLI, signed in (`gh auth login` or a `GH_TOKEN`), and shows that the file was built by this repository's release workflow.
