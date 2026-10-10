using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>Where Terraform stands on this machine, as far as a read-only look can tell.</summary>
public enum TerraformState
{
    /// <summary>The probe itself failed or could not finish; nothing is known. The CLI runs Terraform itself, so this does not block.</summary>
    Unknown = 0,

    /// <summary>Terraform answered <c>version -json</c> with a version the dashboards module accepts.</summary>
    Ready,

    /// <summary>No Terraform executable was found: not on PATH, or <c>TERRAFORM_BIN</c> names a file that is not there.</summary>
    NotInstalled,

    /// <summary>Terraform answered, but it is older than the <c>required_version</c> of the module the CLI ships (<see cref="TerraformProbe.MinimumVersion"/>).</summary>
    TooOld,

    /// <summary>
    /// The executable is there but <c>version -json</c> failed or did not say a version: a broken install, a wrapper that is not
    /// Terraform, or a Terraform too old to have the flag (0.12 and earlier, which cannot run the module either).
    /// </summary>
    NotWorking,
}

/// <summary>The answer of one <see cref="ITerraformProbe"/> look.</summary>
/// <param name="State">What was found.</param>
/// <param name="Summary">One sentence for the card.</param>
/// <param name="Version">The version Terraform reported, or empty when it did not say one.</param>
/// <param name="Path">The executable that answered (or that was looked for, when none did); empty when unknown.</param>
public sealed record TerraformStatus(TerraformState State, string Summary, string Version, string Path)
{
    public static TerraformStatus Checking { get; } = new(TerraformState.Unknown, "Checking for Terraform…", string.Empty, string.Empty);

    /// <summary>
    /// True when the Splunk dashboards may be offered: Terraform is there and new enough, or the look itself could not run (the CLI
    /// checks again). Anything but a definite "not here", "too old" or "not working".
    /// </summary>
    public bool AllowsDashboards => State is TerraformState.Ready or TerraformState.Unknown;
}

/// <summary>
/// A read-only look at Terraform. <b>It never runs a plan, an init or anything that touches a working directory</b>: the only command it
/// issues is <c>terraform version -json</c>, which prints the version of the binary and does nothing else. Injectable so tests never touch a
/// real Terraform.
/// </summary>
public interface ITerraformProbe
{
    Task<TerraformStatus> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>What a probe's process run produced.</summary>
public sealed record TerraformProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Which executable the look will ask, and what it was asked to find.</summary>
/// <param name="Path">The file to run, or null when none was found.</param>
/// <param name="Requested">What was looked for: <c>terraform</c> on PATH, or the value of <c>TERRAFORM_BIN</c>.</param>
/// <param name="FromVariable">True when <c>TERRAFORM_BIN</c> named it, which is how the CLI would have been told too.</param>
public sealed record TerraformLocation(string? Path, string Requested, bool FromVariable);

/// <summary>
/// <see cref="ITerraformProbe"/> over <c>terraform version -json</c>. The executable is the one the CLI itself would run: its
/// <c>--terraform-bin</c> option is bound to the <c>TERRAFORM_BIN</c> variable and defaults to <c>terraform</c>
/// (<c>commands/cmd_setup_splunk_o11y_dashboards.py</c>, 0.8.10), and this app never passes that option, so the child inherits the same
/// variable. Every dependency on the machine is a delegate, so the logic is tested with canned answers.
/// </summary>
public sealed class TerraformProbe : ITerraformProbe
{
    /// <summary>The variable the CLI's <c>--terraform-bin</c> option reads, so the look asks the binary the command would run.</summary>
    public const string BinaryVariable = "TERRAFORM_BIN";

    /// <summary>
    /// The oldest Terraform the bundled module accepts: its <c>required_version = "&gt;= 1.5.0"</c>
    /// (<c>_data/splunk_o11y_dashboards/terraform/main.tf</c>, 0.8.10; unchanged at the pinned source). An older one makes the CLI's
    /// <c>terraform validate</c> fail after it has already copied files and initialised, so it is refused up front.
    /// </summary>
    public static readonly Version MinimumVersion = new(1, 5, 0);

    /// <summary>
    /// How long <c>version -json</c> gets. It is a local call that takes milliseconds, but the first run of a 90 MB executable can sit
    /// behind an antivirus scan for several seconds.
    /// </summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    private static readonly Regex VersionPattern = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private readonly Func<Task<TerraformLocation>> _locate;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<TerraformProcessResult>> _run;

    public TerraformProbe(
        Func<Task<TerraformLocation>> locate,
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<TerraformProcessResult>> run)
    {
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    /// <summary>The probe that looks at the real machine: <c>TERRAFORM_BIN</c> or PATH for the executable, a child process for its version.</summary>
    public static TerraformProbe CreateDefault(DefenseClawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new TerraformProbe(
            () => LocateAsync(paths, Environment.GetEnvironmentVariable(BinaryVariable), File.Exists),
            RunProcessAsync);
    }

    /// <summary>
    /// Finds the executable the way the CLI's <c>--terraform-bin</c> resolves it: the file <c>TERRAFORM_BIN</c> names when it is set (a
    /// full path, or a name looked up on PATH), otherwise <c>terraform</c> on PATH. The variable's value and the file test are parameters
    /// so a test can hand it a fake environment and disk.
    /// </summary>
    internal static async Task<TerraformLocation> LocateAsync(DefenseClawPaths paths, string? configured, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileExists);

        var named = (configured ?? string.Empty).Trim().Trim('"').Trim();
        if (named.Length == 0)
        {
            return new TerraformLocation(await paths.FindExecutableAsync("terraform").ConfigureAwait(false), "terraform", FromVariable: false);
        }

        // A name with a directory in it is a file; a bare name is looked up on PATH, as the OS would for the CLI's child. A relative path is
        // relative to where the CLI runs (its own neutral, empty working directory), never to wherever this app happened to be started: it
        // finds nothing there, exactly as it would for the CLI, and this app does not run a file out of a directory it did not choose.
        if (named.Contains('\\', StringComparison.Ordinal) || named.Contains('/', StringComparison.Ordinal))
        {
            string full;
            try
            {
                full = Path.GetFullPath(named, CliWorkingDirectory.DefaultPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new TerraformLocation(null, named, FromVariable: true);
            }

            var candidates = Path.HasExtension(full) ? new[] { full } : new[] { full + ".exe", full };
            return new TerraformLocation(candidates.FirstOrDefault(fileExists), named, FromVariable: true);
        }

        return new TerraformLocation(await paths.FindExecutableAsync(named).ConfigureAwait(false), named, FromVariable: true);
    }

    public async Task<TerraformStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var location = await _locate().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(location.Path))
            {
                return new TerraformStatus(
                    TerraformState.NotInstalled,
                    location.FromVariable
                        ? $"{BinaryVariable} is set to \"{location.Requested}\", and no such Terraform was found."
                        : "Terraform was not found on this machine's PATH.",
                    string.Empty,
                    location.Requested);
            }

            var executable = location.Path;
            var result = await _run(executable, new[] { "version", "-json" }, VersionTimeout, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut)
            {
                // A look that did not finish says nothing about Terraform: the CLI runs it itself, so this must not lock the card.
                return new TerraformStatus(
                    TerraformState.Unknown,
                    $"Terraform at {executable} did not answer \"version -json\" within {(int)VersionTimeout.TotalSeconds} seconds, so it could not be checked from here. The command checks it again itself.",
                    string.Empty,
                    executable);
            }

            if (result.ExitCode != 0 || !TryReadVersion(result.StandardOutput, out var reported))
            {
                var detail = FirstLine(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
                var failure = result.ExitCode != 0 ? $"failed (exit code {result.ExitCode})" : "did not report a version";
                return new TerraformStatus(
                    TerraformState.NotWorking,
                    $"Terraform was found at {executable}, but \"terraform version -json\" {failure}" +
                    (detail.Length > 0 ? $": {detail}" : string.Empty) +
                    (detail.EndsWith('.') ? " " : ". ") +
                    $"The dashboards need Terraform {MinimumVersion} or later.",
                    string.Empty,
                    executable);
            }

            if (!TryParseVersion(reported, out var version))
            {
                return new TerraformStatus(
                    TerraformState.NotWorking,
                    $"Terraform at {executable} reported a version this app cannot read (\"{Truncate(reported, 40)}\"). The dashboards need Terraform {MinimumVersion} or later.",
                    reported,
                    executable);
            }

            return version < MinimumVersion
                ? new TerraformStatus(
                    TerraformState.TooOld,
                    $"Terraform {reported} at {executable} is older than the {MinimumVersion} that the dashboards module requires.",
                    reported,
                    executable)
                : new TerraformStatus(TerraformState.Ready, $"Terraform {reported} is available.", reported, executable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A probe that cannot even run says nothing about Terraform. The CLI will run it itself, so this must not lock the card.
            return new TerraformStatus(
                TerraformState.Unknown,
                "Terraform could not be checked from here (" + ex.Message + "). The command checks it again itself.",
                string.Empty,
                string.Empty);
        }
    }

    /// <summary>
    /// The <c>terraform_version</c> of <c>terraform version -json</c>. The document is one JSON object, indented over several lines by
    /// Terraform; a line of something else before or after it (a wrapper's banner) is cut away rather than failing the look.
    /// </summary>
    internal static bool TryReadVersion(string output, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var candidates = new List<string> { output.Trim() };
        var first = output.IndexOf('{', StringComparison.Ordinal);
        var last = output.LastIndexOf('}');
        if (first >= 0 && last > first)
        {
            candidates.Add(output[first..(last + 1)]);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("terraform_version", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } text)
                {
                    version = text.Trim();
                    return true;
                }
            }
            catch (JsonException)
            {
                // Not the document; try the next candidate.
            }
        }

        return false;
    }

    /// <summary>
    /// <c>1.9.5</c>, <c>v1.10.0-beta1</c>, <c>1.5</c> → the numbers before any pre-release suffix. A pre-release of the minimum counts as the
    /// minimum: the look errs toward offering the card, and the CLI's own <c>terraform validate</c> is the authority.
    /// </summary>
    internal static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text) || VersionPattern.Match(text.Trim()) is not { Success: true } match)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor))
        {
            return false;
        }

        var patch = match.Groups["patch"].Success && int.TryParse(match.Groups["patch"].Value, out var p) ? p : 0;
        version = new Version(major, minor, patch);
        return true;
    }

    private static string FirstLine(string text)
    {
        var line = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return Truncate(line, 160);
    }

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] + "…" : text;

    // ------------------------------------------------------------------ the real machine

    /// <summary>
    /// What <see cref="RunProcessAsync"/> starts, separate from the start itself so a test can read it without running anything: the
    /// executable, its arguments as a list (no shell), and an environment that stops <c>terraform version</c> from phoning HashiCorp.
    /// <c>CHECKPOINT_DISABLE</c> is Terraform's own switch for its update check; without it the command asks the network whether a newer
    /// release exists, which a read-only look at this machine has no business doing and which makes it slow behind a proxy.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Nowhere a Terraform working directory (a .terraform.lock.hcl whose providers it would list) could be.
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        _ = ChildEnvironment.StripSecrets(info);
        info.Environment["CHECKPOINT_DISABLE"] = "1";
        return info;
    }

    /// <summary>Runs <c>terraform</c> with an argument list (no shell), a hard timeout, and the whole tree killed when it fires.</summary>
    private static async Task<TerraformProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments) };
        _ = process.Start();
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new TerraformProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new TerraformProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: false);
    }
}
