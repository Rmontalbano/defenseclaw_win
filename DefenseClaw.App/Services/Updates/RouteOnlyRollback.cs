using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// A rollback to a release before route-only (1.0.10) while today's config.yaml has <c>routing.route_only.connectors</c>: install.ps1 1.0.10 runs
/// <c>defenseclaw-gateway connector prepare-rollback</c> before it swaps the install, and that puts back the model settings route-only changed in the agent's own
/// settings (scripts/install.ps1@1.0.10:5063-5084). Read-only: nothing is run or changed, and no routing command is offered.
/// </summary>
public static class RouteOnlyRollback
{
    public const string WarningTitle = "Route-only agents";

    // The schema's enum for routing.route_only.connectors; the order here is the order the text names them.
    private static readonly string[] Agents = ["claudecode", "codex"];

    /// <param name="configText">Today's config.yaml text.</param>
    /// <param name="backTo">The version in <c>previous\VERSION</c>.</param>
    /// <returns>The warning text, or null when the target has route-only, the config does not name an agent, or anything is unreadable.</returns>
    public static string? Evaluate(string? configText, string? backTo)
    {
        if (string.IsNullOrWhiteSpace(configText) ||
            !InstallScriptPlan.TryNormalizeVersion(backTo, out var normalized) || !Version.TryParse(normalized, out var target) ||
            target >= RuntimeProbe.Release1010)
        {
            return null;
        }

        // Route-only routes a connector only while routing is on and the connector is listed (internal/config/routing_policy.go@1.0.10:303-305, RouteOnlyRouted).
        var reader = ConfigYamlReader.Parse(configText);
        var named = reader.Bool("routing", "enabled") == true ? reader.List("routing", "route_only", "connectors") : null;
        if (named is null)
        {
            return null;
        }

        var names = Agents.Where(agent => named.Contains(agent, StringComparer.Ordinal)).ToArray();
        if (names.Length == 0)
        {
            return null;
        }

        return "Route-only is on for " + string.Join(" and ", names) + ". DefenseClaw " + normalized + " has no route-only, so before it swaps the install the installer has " +
               "DefenseClaw put back the model settings route-only changed in those agents' settings. Restart those agents after the rollback. If that step fails, the installer " +
               "warns that a route-only agent gets 401 on its model calls until the DefenseClaw base URL and route header are removed from its settings.";
    }
}
