using System.IO;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "What needs attention" when the OpenCode on this account is not the one the installed DefenseClaw guards (CUST-426). DefenseClaw guards OpenCode V1 up
/// to 1.0.5 and OpenCode V2 from 1.0.6 (internal/gateway/connector/opencode_v2.go, <c>RequireOpenCodeV2</c>), and neither says so loudly: on 1.0.6 the gateway
/// refuses V1 and doctor fails the OpenCode hooks row, but the tool calls of that V1 run unchecked; on 1.0.5 and earlier V2 never loads the plugin, yet
/// <c>status</c> shows OpenCode protected and <c>doctor</c> passes (changelog "Known limit", GAP-0029). The row appears only when OpenCode is a connector of
/// this install and the files (<see cref="OpenCodeInstallEvidence"/>, read off the UI thread with the metrics read) show the unguarded version. Evidence that
/// cannot be read is no row. A managed installation is not asked: upstream's <c>_opencode_configured</c> is false there (the administrator's host reports
/// <c>opencode_v1_unprotected</c> itself), and the files here are the account's, not the machine's.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The row title of an OpenCode V1 a 1.0.6 or later DefenseClaw does not guard.</summary>
    internal const string OpenCodeV1NotGuardedTitle = "OpenCode V1 is not guarded";

    /// <summary>The row title of an OpenCode V2 a DefenseClaw before 1.0.6 cannot guard.</summary>
    internal const string OpenCodeV2UncheckedTitle = "OpenCode V2 runs unchecked on this DefenseClaw";

    /// <summary>Reads the files (a test aims it at its own folders); the account's own npm prefixes by default.</summary>
    internal Func<string, OpenCodeInstallFacts> OpenCodeReader { get; set; } = static dataDirectory => OpenCodeInstallEvidence.Read(dataDirectory);

    private OpenCodeInstallFacts _openCode = OpenCodeInstallFacts.NotConfigured;

    private int _openCodeReading;

    /// <summary>The last OpenCode read (a test reads it).</summary>
    internal OpenCodeInstallFacts LastOpenCodeFacts => _openCode;

    /// <summary>
    /// Reads what the files say about OpenCode when the metrics are read (15 s while the panel is active; a few small size-capped files, read-only), and only
    /// for a per-user install that has OpenCode on its roster and a runtime whose version is known. Rebuilds the list when the answer changed. Never throws.
    /// </summary>
    internal async Task RefreshOpenCodeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _openCodeReading, 1) == 1)
        {
            return;
        }

        try
        {
            var wanted = !Services.Installation.Context.IsManaged &&
                         Services.Runtime.Current.Identity?.ParsedVersion is not null &&
                         IsActiveConnector("opencode");
            var dataDirectory = Services.Paths.DataDirectory;
            var reader = OpenCodeReader;
            var found = wanted
                ? await Task.Run(() => reader(dataDirectory), cancellationToken).ConfigureAwait(true)
                : OpenCodeInstallFacts.NotConfigured;

            if (found != _openCode)
            {
                _openCode = found;
                BuildAttention(_snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // The panel went away mid-read; the next activation reads again.
        }
#pragma warning disable CA1031 // Files that cannot be read are no evidence; they must not fault the panel.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceWarning($"overview: OpenCode could not be read: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _ = Interlocked.Exchange(ref _openCodeReading, 0);
        }
    }

    /// <summary>Adds the row. Called once from <see cref="BuildAttention"/>. The read is per poll: only what is still true now (roster, install kind, runtime) is drawn.</summary>
    private void AppendOpenCodeAttention(List<AttentionRow> rows)
    {
        if (Services.Installation.Context.IsManaged || !IsActiveConnector("opencode"))
        {
            return;
        }

        if (OpenCodeRow(Services.Runtime.Current, _openCode) is { } row)
        {
            rows.Add(row);
        }
    }

    /// <summary>
    /// The row for what the files show, or null. 1.0.6 or later (<see cref="RuntimeCapability.OpenCodeV2Only"/>) guards V2: a V1 with no V2 beside it is unguarded.
    /// A known runtime before 1.0.6 guards V1: a V2 is unguarded. A V1 and a V2 together say nothing (the installer, which runs the program, is the judge of
    /// which one runs), and an unknown runtime says nothing: the version decides which of the two is true.
    /// </summary>
    internal static AttentionRow? OpenCodeRow(RuntimeSnapshot runtime, OpenCodeInstallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.Configured || runtime.Identity?.ParsedVersion is not { } version || !runtime.Capabilities.IsKnown)
        {
            return null;
        }

        var release = version.ToString(3);
        if (runtime.Capabilities.Has(RuntimeCapability.OpenCodeV2Only))
        {
            return facts is { V1: { } v1, V2: null }
                ? new AttentionRow
                {
                    Title = OpenCodeV1NotGuardedTitle,
                    Detail = $"OpenCode {v1.Version} ({v1.Location}) is installed, and DefenseClaw {release} guards OpenCode V2 only: its gateway refuses V1, so this OpenCode's " +
                             "tool calls are not checked, and doctor fails the OpenCode hooks row. Install V2 with the command below, then run 'opencode service restart'. " +
                             "The app never runs either command.",
                    SeverityKey = "High",
                    Command = OpenCodeInstallEvidence.InstallV2Command,
                }
                : null;
        }

        return version < OpenCodeInstallEvidence.FirstV2Release && facts.V2 is { } v2
            ? new AttentionRow
            {
                Title = OpenCodeV2UncheckedTitle,
                Detail = $"OpenCode {v2.Version} ({v2.Location}) is installed, but DefenseClaw {release} guards OpenCode V1 only: V2 does not load its plugin, so its tool " +
                         "calls run unchecked, even though status shows OpenCode protected and doctor passes. Upgrade DefenseClaw to 1.0.6 or later (open Updates), " +
                         "which guards V2 and refuses V1.",
                SeverityKey = "High",
            }
            : null;
    }
}
