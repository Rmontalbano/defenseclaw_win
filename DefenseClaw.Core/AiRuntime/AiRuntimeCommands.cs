using System.Globalization;

namespace DefenseClaw.Core.AiRuntime;

/// <summary>
/// What <c>agent discovery runtime enable</c> is asked to change. Every member that is null means "leave it as it is": the CLI's
/// own default, and the reason the argv carries only what the operator chose.
/// </summary>
/// <param name="HostPlane">Plane C (agent actions, kernel events): true <c>--enable-host-plane</c>, false <c>--no-enable-host-plane</c>, null untouched.</param>
/// <param name="DnsCapture">Passive DNS observation: true <c>--dns-capture</c>, false <c>--no-dns-capture</c>, null untouched.</param>
/// <param name="PollIntervalSeconds"><c>--poll-interval-s</c>, <see cref="AiRuntimeCommands.MinPollIntervalSeconds"/> to <see cref="AiRuntimeCommands.MaxPollIntervalSeconds"/>.</param>
/// <param name="MinRiskToReport"><c>--min-risk-to-report</c>, <see cref="AiRuntimeCommands.MinRiskToReport"/> to <see cref="AiRuntimeCommands.MaxRiskToReport"/>.</param>
/// <param name="Restart">False adds <c>--no-restart</c>: the setting is saved and the running gateway keeps its planes until it restarts.</param>
public sealed record AiRuntimeEnableOptions(
    bool? HostPlane = null,
    bool? DnsCapture = null,
    int? PollIntervalSeconds = null,
    int? MinRiskToReport = null,
    bool Restart = true);

/// <summary>
/// The commands the Runtime panel builds, and nothing else. Each is a fixed argv, assembled here from constants and the numbers the
/// operator typed (range-checked): there is no argument text from outside, no secret, and - on purpose - no way to ask for
/// <c>permissions --grant</c> or <c>--revert</c>, which change the machine's audit policy and are never run by this app.
/// <para>
/// The flags are the ones the command's own help lists at DefenseClaw source commit 95159fd (<c>runtime_enable</c>, <c>runtime_disable</c>,
/// <c>runtime_scan</c> and <c>runtime_permissions</c> in <c>cli/defenseclaw/commands/cmd_agent.py</c>).
/// </para>
/// </summary>
public static class AiRuntimeCommands
{
    /// <summary>The shortest poll interval the CLI accepts, in seconds.</summary>
    public const int MinPollIntervalSeconds = 5;

    /// <summary>The longest poll interval the CLI accepts (one hour), in seconds.</summary>
    public const int MaxPollIntervalSeconds = 60 * 60;

    /// <summary>The lowest reporting floor the CLI accepts.</summary>
    public const int MinRiskToReport = 1;

    /// <summary>The highest reporting floor the CLI accepts.</summary>
    public const int MaxRiskToReport = 100;

    /// <summary>The subcommands the panel gates its buttons on, by name, as <c>agent discovery runtime --help</c> lists them.</summary>
    public const string ScanCommand = "scan";

    public const string EnableCommand = "enable";

    public const string DisableCommand = "disable";

    public const string PermissionsCommand = "permissions";

    private static readonly string[] Group = ["agent", "discovery", "runtime"];

    /// <summary><c>agent discovery runtime scan</c>: poll the planes now. Changes state (a poll is recorded and refreshes the snapshot), so it is reviewed.</summary>
    public static IReadOnlyList<string> PollNow { get; } = [.. Group, ScanCommand];

    /// <summary>
    /// <c>agent discovery runtime permissions --json</c>: what each plane needs, read-only. This exact argv, with no <c>--grant</c>,
    /// <c>--revert</c> or <c>--yes</c>, is the only form of the command the app runs.
    /// </summary>
    public static IReadOnlyList<string> ReadPermissions { get; } = [.. Group, PermissionsCommand, "--json"];

    /// <summary><c>agent discovery runtime disable --yes [--no-restart]</c>.</summary>
    public static IReadOnlyList<string> Disable(bool restart = true)
    {
        var argv = new List<string>([.. Group, DisableCommand, "--yes"]);
        if (!restart)
        {
            argv.Add("--no-restart");
        }

        return argv;
    }

    /// <summary>
    /// <c>agent discovery runtime enable --yes [--enable-host-plane|--no-enable-host-plane] [--dns-capture|--no-dns-capture]
    /// [--poll-interval-s N] [--min-risk-to-report N] [--no-restart]</c>. <c>--yes</c> is always there: the app has no console to
    /// answer the CLI's prompt, and the review the operator confirmed already showed this exact line.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A number is outside the range the CLI accepts (see <see cref="Validate"/>).</exception>
    public static IReadOnlyList<string> Enable(AiRuntimeEnableOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Validate(options) is { } problem)
        {
            throw new ArgumentOutOfRangeException(nameof(options), problem);
        }

        var argv = new List<string>([.. Group, EnableCommand, "--yes"]);
        if (options.HostPlane is { } hostPlane)
        {
            argv.Add(hostPlane ? "--enable-host-plane" : "--no-enable-host-plane");
        }

        if (options.DnsCapture is { } dns)
        {
            argv.Add(dns ? "--dns-capture" : "--no-dns-capture");
        }

        if (options.PollIntervalSeconds is { } interval)
        {
            argv.Add("--poll-interval-s");
            argv.Add(interval.ToString(CultureInfo.InvariantCulture));
        }

        if (options.MinRiskToReport is { } floor)
        {
            argv.Add("--min-risk-to-report");
            argv.Add(floor.ToString(CultureInfo.InvariantCulture));
        }

        if (!options.Restart)
        {
            argv.Add("--no-restart");
        }

        return argv;
    }

    /// <summary>Null when every number is inside the CLI's range; otherwise the sentence that says which is not and what the range is.</summary>
    public static string? Validate(AiRuntimeEnableOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PollIntervalSeconds is { } interval && interval is < MinPollIntervalSeconds or > MaxPollIntervalSeconds)
        {
            return $"The poll interval must be between {MinPollIntervalSeconds} and {MaxPollIntervalSeconds} seconds.";
        }

        if (options.MinRiskToReport is { } floor && floor is < MinRiskToReport or > MaxRiskToReport)
        {
            return $"The reporting floor must be between {MinRiskToReport} and {MaxRiskToReport}.";
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="argv"/> is exactly the read-only permissions command. The panel checks this before it starts the
    /// process, so a change elsewhere cannot turn the prerequisites card into a <c>--grant</c>.
    /// </summary>
    public static bool IsPermissionsRead(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return argv.SequenceEqual(ReadPermissions, StringComparer.Ordinal);
    }
}
