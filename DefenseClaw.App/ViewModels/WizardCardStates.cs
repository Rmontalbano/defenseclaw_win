using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Which Setup hub card says what about the installation today (CUST-271): the TUI's setup screens open with a line of current state
/// (<c>Guardrail: on · Mode: observe</c>), and so does a card here, from config.yaml alone - nothing runs, so the line is as old as the file the
/// app last read, and it is drawn again when that changes. The words are <see cref="SetupStateLines"/>'s; this is only the table that says which
/// target gets which. A target without an entry has no line (the tile keeps its CLI summary).
/// </summary>
internal static class WizardCardStates
{
    /// <summary>The line for a card, or null when the target has none.</summary>
    public static CardState? For(string target, string group, ConfigDocument? document)
    {
        ArgumentNullException.ThrowIfNull(target);

        switch (target)
        {
            case "llm":
                return SetupStateLines.Llm(document);
            case "guardrail":
                return SetupStateLines.Guardrail(document);
            case "remove":
                return SetupStateLines.Roster(document);
            case "notifications":
                return SetupStateLines.NotificationsSwitch(document);
            case "notifications-set":
                return SetupStateLines.NotificationCategories(document);
        }

        // A connector's own card: not the proxy connectors (Windows does not run them, and the card is disabled with its reason).
        return string.Equals(group, WizardGroups.Connectors, StringComparison.Ordinal) && !IsProxy(target)
            ? SetupStateLines.Connector(document, target)
            : null;
    }

    private static bool IsProxy(string target) =>
        string.Equals(target, "openclaw", StringComparison.Ordinal) || string.Equals(target, "zeptoclaw", StringComparison.Ordinal);
}
