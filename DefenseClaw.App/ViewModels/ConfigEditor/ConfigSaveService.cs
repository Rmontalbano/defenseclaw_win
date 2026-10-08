using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>Which step of the pipeline a <see cref="SaveOutcome"/> stopped at.</summary>
public enum SaveStage
{
    DriftDetected,
    BackupFailed,
    WriteFailed,
    ValidationFailed,
    Succeeded,
}

public sealed record SaveOutcome(bool Success, SaveStage Stage, string Message, string? BackupPath, string? BackupSha256Hex);

public sealed record RestoreOutcome(bool Success, string Message);

/// <summary>
/// The config editor's only write path: hash-checked backup, atomic write, CLI
/// validation, and one-click restore. Takes <see cref="DefenseClawPaths"/> and
/// <see cref="CliRunner"/> directly (not <c>AppServices</c>) precisely so it can be
/// pointed at a temp-directory fixture — real or fake CLI included — for verification,
/// the same way <c>DefenseClaw.Tests</c> points <see cref="ConfigStore"/> at fixtures.
/// <para>
/// Stage order matters: drift is checked before anything touches disk, the backup is
/// written and its SHA-256 recorded before the real file is touched, the write itself is
/// temp-file-plus-<see cref="File.Replace(string, string, string?)"/> so a crash mid-write
/// never leaves a half-written config.yaml, and CLI validation only runs after all of that
/// has already succeeded — a validation failure still leaves a good backup to restore.
/// </para>
/// <para>
/// <b>Backups are never overwritten.</b> The name carries a millisecond timestamp and the
/// file is created with <see cref="FileMode.CreateNew"/>, retrying with a numeric suffix if
/// the name is taken — two saves inside one second (or one clock tick) must not replace the
/// pre-edit backup of the first with the already-edited content of the second.
/// </para>
/// <para>
/// <b>No litter.</b> The temp file (<c>.config.yaml.tmp-&lt;guid&gt;</c>) is deleted in a
/// <c>finally</c>, so a failed or cancelled <see cref="File.Replace(string, string, string?)"/>
/// does not leave it behind next to the user's real config.
/// </para>
/// </summary>
public sealed class ConfigSaveService
{
    /// <summary>How many numeric suffixes to try when a same-millisecond backup name is already taken.</summary>
    private const int MaxBackupNameAttempts = 100;

    /// <summary>How many times the final swap onto config.yaml is tried before a sharing violation is reported as a failed write.</summary>
    internal const int ReplaceAttempts = 6;

    /// <summary>Wait before retry N of the swap is this times N (50, 100, 150, 200, 250 ms: about three quarters of a second in all).</summary>
    internal static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Verified against `defenseclaw config validate --help` on 0.8.10: "Verify the config file parses and references valid enums." Exit 0/1; `--quiet` is deliberately not used so failures explain themselves.</summary>
    internal static readonly string[] ValidateArgv = { "config", "validate" };

    private readonly DefenseClawPaths _paths;
    private readonly CliRunner? _cli;

    /// <summary>Test seam: runs between the drift check and the backup read, the window in which a CLI write can land.</summary>
    internal Action? AfterDriftCheck { get; set; }

    public ConfigSaveService(DefenseClawPaths paths, CliRunner? cli)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _cli = cli;
    }

    /// <summary>Captures the on-disk identity of config.yaml — call this once, right after loading, and keep it for the eventual save.</summary>
    public FileSignature CaptureSignature() => FileSignature.Capture(_paths.ConfigFilePath);

    /// <summary>Reads the current on-disk config.yaml verbatim — used to populate the drift banner's "view on-disk version" action.</summary>
    public async Task<string> ReadCurrentTextAsync(CancellationToken cancellationToken = default)
    {
        var path = _paths.ConfigFilePath;
        return File.Exists(path)
            ? await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    public async Task<SaveOutcome> SaveAsync(string newRawText, FileSignature signatureAtLoad, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newRawText);

        var path = _paths.ConfigFilePath;

        // A host copy of a container's data folder (developer runtime selector) is read-only: the container owns the real file.
        if (_paths.DataDirectoryReadOnly)
        {
            return new SaveOutcome(
                false,
                SaveStage.WriteFailed,
                "Nothing was saved: this data folder is a read-only copy of a container's. Change the container's configuration with its own CLI.",
                null,
                null);
        }

        // 0. The validate step at the end runs a CLI verb without a confirmation, which is only allowed for a
        //    read-only verb by the shared classifier. Checked here, before anything touches disk, so a
        //    classifier change can never leave a half-finished save behind.
        if (_cli is not null && CommandTiers.Classify(ValidateArgv) != CommandTier.ReadOnly)
        {
            return new SaveOutcome(
                false,
                SaveStage.WriteFailed,
                "Nothing was saved: `defenseclaw config validate` is no longer classified read-only, so the save pipeline would need a review step before running it.",
                null,
                null);
        }

        // 1. Drift check, before anything touches disk.
        var onDiskNow = FileSignature.Capture(path);
        if (!onDiskNow.Equals(signatureAtLoad))
        {
            return new SaveOutcome(
                false,
                SaveStage.DriftDetected,
                "config.yaml changed on disk since it was loaded. Reload to pick up the new content, or view the on-disk version to compare before deciding.",
                null,
                null);
        }

        AfterDriftCheck?.Invoke();

        // 2. Hash-checked backup, before the real file is touched.
        string? backupPath = null;
        string? backupSha = null;
        if (File.Exists(path))
        {
            try
            {
                var originalBytes = await DefenseClaw.Core.IO.SharedFile.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

                // The drift check above compared signatures a moment ago; the bytes read here are the ones that are
                // about to be backed up and then replaced. If they are not the ones the editor loaded, something (the
                // CLI, a second editor) wrote in between, and going on would back that write up as if it were the
                // original and overwrite it.
                if (!signatureAtLoad.DescribesContent(originalBytes))
                {
                    return new SaveOutcome(
                        false,
                        SaveStage.DriftDetected,
                        "config.yaml changed on disk while the save was starting, so nothing was written. Reload to pick up the new content, or view the on-disk version to compare before deciding.",
                        null,
                        null);
                }

                backupSha = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
                backupPath = await WriteBackupAsync(path, originalBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SaveOutcome(false, SaveStage.BackupFailed, $"Could not create a backup before saving: {ex.Message}", null, null);
            }
        }

        // 3. Atomic write: temp file in the same directory, then a filesystem-level replace.
        try
        {
            await WriteAtomicallyAsync(Utf8NoBom.GetBytes(newRawText), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveOutcome(false, SaveStage.WriteFailed, $"Could not write config.yaml: {ex.Message}", backupPath, backupSha);
        }

        // 4. Validate the file that is now live, via the CLI — never by re-parsing our own write.
        //    `config validate` ("parses and references valid enums", exit 0/1) rather than
        //    `config show`: show only proves the file parses, so a bad enum would still have
        //    been reported as "Saved and validated". On 0.8.10 validate prints "config is valid"
        //    against a known-good file; its failure output goes to stdout, so the message below
        //    reads both streams.
        if (_cli is not null)
        {
            CliInvocation invocation;
            try
            {
                invocation = await _cli.RunAsync(ValidateArgv, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (CliNotFoundException)
            {
                return new SaveOutcome(
                    true,
                    SaveStage.Succeeded,
                    "Saved. Could not validate: the defenseclaw CLI was not found on PATH.",
                    backupPath,
                    backupSha);
            }

            if (invocation.FailureReason is { Length: > 0 } failure)
            {
                return new SaveOutcome(false, SaveStage.ValidationFailed, $"Saved, but validation could not run: {failure}", backupPath, backupSha);
            }

            if (invocation.ExitCode is not 0)
            {
                return new SaveOutcome(false, SaveStage.ValidationFailed, BuildValidationMessage(invocation), backupPath, backupSha);
            }
        }

        return new SaveOutcome(true, SaveStage.Succeeded, "Saved and validated.", backupPath, backupSha);
    }

    /// <summary>Copies a backup file back over config.yaml, atomically. The one-click restore offered after a validation failure.</summary>
    public async Task<RestoreOutcome> RestoreFromBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupPath);

        if (!File.Exists(backupPath))
        {
            return new RestoreOutcome(false, $"Backup file not found: {backupPath}");
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false);
            await WriteAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return new RestoreOutcome(true, "Restored config.yaml from the backup.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RestoreOutcome(false, $"Could not restore from backup: {ex.Message}");
        }
    }

    /// <summary>
    /// UTF-8 without a BOM — what <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/>
    /// has always written here, kept explicit now that the write goes through
    /// <see cref="WriteAtomicallyAsync"/> as bytes.
    /// </summary>
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="originalBytes"/> next to <paramref name="configPath"/> as
    /// <c>config.yaml.bak-&lt;yyyyMMddHHmmssfff&gt;</c> (plus <c>-N</c> if that exact name exists),
    /// created with <see cref="FileMode.CreateNew"/> so an existing backup can never be replaced.
    /// A backup that fails part-way is deleted rather than left truncated.
    /// </summary>
    private static async Task<string> WriteBackupAsync(string configPath, byte[] originalBytes, CancellationToken cancellationToken)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var baseName = configPath + ".bak-" + stamp;

        for (var attempt = 0; attempt < MaxBackupNameAttempts; attempt++)
        {
            var candidate = attempt == 0 ? baseName : baseName + "-" + attempt.ToString(CultureInfo.InvariantCulture);

            FileStream stream;
            try
            {
                stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Name already taken (a second save in the same millisecond) — try the next suffix.
                continue;
            }

            try
            {
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(originalBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                return candidate;
            }
            catch
            {
                // The stream is already disposed by the time control reaches here.
                TryDeleteQuietly(candidate);
                throw;
            }
        }

        throw new IOException($"Could not find an unused backup file name for '{configPath}' after {MaxBackupNameAttempts} attempts.");
    }

    /// <summary>
    /// The shared atomic-write step: bytes go to a uniquely named temp file in the same
    /// directory, then <see cref="File.Replace(string, string, string?)"/> (or a move, if
    /// config.yaml does not exist yet) swaps it in. The temp file is removed in a
    /// <c>finally</c> whichever way that goes.
    /// </summary>
    private async Task WriteAtomicallyAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var path = _paths.ConfigFilePath;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            directory = _paths.DataDirectory;
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".config.yaml.tmp-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);

            // The swap is retried on an IOException: a file watcher (this app's own ConfigChangeToken hashes
            // config.yaml whenever the directory moves), an indexer or an antivirus scanner that has config.yaml open
            // for a moment turns File.Replace into a sharing violation ("Unable to remove the file to be replaced").
            // That is transient and the temp file is still intact, so waiting it out beats telling the operator their
            // save failed. A failure that outlasts the retries is reported exactly as before.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(tempPath, path, null);
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }

                    break;
                }
                catch (IOException) when (attempt < ReplaceAttempts)
                {
                    await Task.Delay(ReplaceRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // After a successful Replace/Move the temp name no longer exists and this is a no-op.
            TryDeleteQuietly(tempPath);
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp/partial-backup file is untidy, not harmful.
        }
    }

    private static string BuildValidationMessage(CliInvocation invocation)
    {
        // Stderr first; `config validate` reports its findings on stdout, so fall back to that
        // (minus blank lines) before settling for a bare exit code.
        static string Join(IEnumerable<CliOutputLine> lines) => string.Join(
            Environment.NewLine,
            lines.Select(l => l.Text.Trim()).Where(text => text.Length > 0));

        var stderr = Join(invocation.OutputLines.Where(l => l.Stream == CliStream.StandardError));
        var stdout = Join(invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput));

        var detail = stderr.Length > 0 ? stderr
            : stdout.Length > 0 ? stdout
            : $"defenseclaw config validate exited with code {invocation.ExitCode}.";
        return "The saved file failed validation — restore the backup or fix it in the RAW tab: " + detail;
    }
}
