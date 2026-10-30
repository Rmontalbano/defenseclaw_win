using System.Globalization;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The read-only "Guard status" card of the ACP Guard window (CUST-401), drawn from <c>defenseclaw acp status --json</c> on a 1.0.5 runtime: each bound
/// editor and agent pair, whether it is healthy, the profile and mode it runs under and, for a pair that is not healthy, the CLI's own findings (which name
/// the editor entries that start the guard with the lock). The card is information only; fixing a pair is the setup below it again, which has its own review.
/// </summary>
public static class AcpGuardStatusView
{
    /// <summary>The card's heading.</summary>
    public const string Title = "Guard status";

    /// <summary>The caption while every pair is healthy.</summary>
    public const string HealthyNote = "Read just now with defenseclaw acp status --json; nothing was changed. Healthy means the editor entry starts the guard with this pair's contract lock and agent.";

    /// <summary>The caption while a pair needs attention: the way back, in the app's own terms.</summary>
    public const string AttentionNote = "Setting the pair up again below rewrites its editor entry and lock, after a review of the command. defenseclaw acp verify in a terminal reports the same findings.";

    /// <summary>The card for <paramref name="read"/>; null while there is nothing to say (the runtime has no such command, or the read was never made).</summary>
    public static WizardInfoBlock? Block(AcpRead<AcpGuardStatus>? read)
    {
        if (read is null || read.Status == AcpReadStatus.Unsupported)
        {
            return null;
        }

        if (read.Value is not { } status)
        {
            return new WizardInfoBlock(Title, new[] { new WizardInfoLine("Status", "Could not be read. " + read.Message) });
        }

        var lines = new List<WizardInfoLine>
        {
            new("ACP guard", status.Enabled
                ? string.Create(CultureInfo.InvariantCulture, $"On, {status.Bindings.Count} guarded {(status.Bindings.Count == 1 ? "pair" : "pairs")}")
                : "Off (acp.enabled is false)"),
        };

        if (status.Bindings.Count == 0)
        {
            lines.Add(new WizardInfoLine("Pairs", "None yet. Choose an editor and an agent below, then review the command."));
        }

        foreach (var binding in status.Bindings)
        {
            lines.Add(new WizardInfoLine(binding.Client + " / " + binding.Agent, Describe(binding), binding.Healthy ? "Ok" : "Warn"));
            lines.AddRange(binding.Problems.Select(static problem => new WizardInfoLine(string.Empty, problem, "Warn")));
        }

        return new WizardInfoBlock(Title, lines, status.NeedsAttention ? AttentionNote : HealthyNote);
    }

    /// <summary>"Healthy · profile default (observe, inherited)".</summary>
    internal static string Describe(AcpGuardBinding binding)
    {
        var health = binding.Healthy ? "Healthy" : "Needs attention";
        var qualifiers = new List<string>();
        if (binding.Mode.Length > 0)
        {
            qualifiers.Add(binding.Mode);
        }

        if (binding.ProfileInherited)
        {
            qualifiers.Add("inherited");
        }

        return binding.Profile.Length == 0
            ? health
            : health + " · profile " + binding.Profile + (qualifiers.Count > 0 ? " (" + string.Join(", ", qualifiers) + ")" : string.Empty);
    }
}
