using DefenseClaw.Core.Cli;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

/// <summary>Whether the running gateway has applied a tool list change yet, as <c>tool block|allow|unblock</c> reported it.</summary>
public enum GatewayEnforcement
{
    /// <summary>The CLI said nothing about it (an older runtime, a gateway it could not ask, or a command that does not report it).</summary>
    NotReported,

    /// <summary>"The running gateway enforces it now": hook calls already get the new verdict.</summary>
    Enforced,

    /// <summary>"Pending: the running gateway has not applied it yet": hook calls keep the previous verdict until it reloads config.yaml.</summary>
    Pending,
}

/// <summary>
/// The line DefenseClaw 1.0.5 adds to the end of <c>tool block</c>, <c>tool allow</c> and <c>tool unblock</c> (CUST-400). The tool lists live in config.yaml and the
/// gateway reloads them a moment after the write, so the CLI waits up to 5 seconds for the gateway to apply the new policy digest and then prints one of two sentences
/// (cli/defenseclaw/commands/cmd_tool.py <c>_echo_gateway_enforcement_checked</c>: "The running gateway enforces it now (policy digest …)." or "Pending: the running
/// gateway has not applied it yet. Hook calls keep the previous verdict until it reloads config.yaml, usually within a few seconds."). Nothing is said when no gateway
/// of the account can be asked. The app keys on those two sentences in the command's standard output, not on a version, so an older runtime (which prints neither)
/// keeps the fixed success text, and a runtime that words them differently simply shows no extra line. Pure: it reads text and runs nothing.
/// </summary>
public static class GatewayEnforcementNotice
{
    /// <summary>The start of the enforced sentence. The digest that follows it in the CLI's line is left out of what the app shows (it is in Activity).</summary>
    private const string EnforcedPrefix = "The running gateway enforces it now";

    /// <summary>The start of the pending sentence.</summary>
    private const string PendingPrefix = "Pending: the running gateway has not applied it yet";

    /// <summary>What the app shows for an enforced change.</summary>
    public const string EnforcedText = "The running gateway enforces it now.";

    /// <summary>What the app shows for a pending change: the CLI's own words.</summary>
    public const string PendingText =
        "Pending: the running gateway has not applied it yet. Hook calls keep the previous verdict until it reloads config.yaml, usually within a few seconds.";

    /// <summary>
    /// The last enforcement line in <paramref name="standardOutput"/> (any line ending), or <see cref="GatewayEnforcement.NotReported"/>. The text a line carries is
    /// <see cref="EnforcedText"/> or <see cref="PendingText"/>; the CLI's own line is not echoed back, so a longer line from a newer runtime cannot widen the message.
    /// </summary>
    public static GatewayEnforcement Parse(string? standardOutput, out string text)
    {
        text = string.Empty;
        var result = GatewayEnforcement.NotReported;
        if (string.IsNullOrEmpty(standardOutput))
        {
            return result;
        }

        foreach (var raw in standardOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(EnforcedPrefix, StringComparison.Ordinal))
            {
                result = GatewayEnforcement.Enforced;
                text = EnforcedText;
            }
            else if (line.StartsWith(PendingPrefix, StringComparison.Ordinal))
            {
                result = GatewayEnforcement.Pending;
                text = PendingText;
            }
        }

        return result;
    }

    /// <summary>The same, read from a finished command's standard output.</summary>
    public static GatewayEnforcement Parse(CliInvocation invocation, out string text)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var stdout = string.Join('\n', invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));
        return Parse(stdout, out text);
    }

    /// <summary>
    /// The result bar for a mutation that exited 0: the fixed success text, then the gateway's sentence when the CLI printed one. An enforced change is a
    /// success; a pending one is a warning (it is saved, and hook calls keep the old verdict for a moment). Every other command prints neither sentence, so
    /// its bar is exactly what it was.
    /// </summary>
    public static (string Title, string Message, InfoBarSeverity Severity) Done(string successMessage, CliInvocation invocation)
    {
        var enforcement = Parse(invocation, out var text);
        var message = successMessage + (enforcement == GatewayEnforcement.NotReported ? string.Empty : " " + text) + " Recorded in Activity.";
        return enforcement == GatewayEnforcement.Pending
            ? ("Saved, not enforced yet", message, InfoBarSeverity.Warning)
            : ("Done", message, InfoBarSeverity.Success);
    }
}
