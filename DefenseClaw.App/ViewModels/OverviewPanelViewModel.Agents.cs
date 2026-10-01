using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Discovered AI agents card (CUST-209): the top eight agents <c>defenseclaw</c> found on this machine, with the Mac's state tags
/// (<c>[NEW]</c> <c>[CHG]</c> <c>[GONE]</c>) and confidence, read from <c>ai_discovery_state.json</c>: a file read and a parse of a few
/// milliseconds, off the UI thread, on the panel's slow cadence. The full table is the AI Discovery panel's; "See all" goes there.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    public ObservableCollection<DiscoveredAgentRow> AgentRows { get; } = new();

    /// <summary><c>59 active · 2 new · scanned 50s ago · mode enhanced</c>.</summary>
    [ObservableProperty]
    private string _agentsSummary = string.Empty;

    /// <summary>Why the list is empty (off, never scanned, unreadable, nothing found), with the command that would change it; empty when there are rows.</summary>
    [ObservableProperty]
    private string _agentsNote = "Reading ai_discovery_state.json…";

    [ObservableProperty]
    private bool _hasAgents;

    /// <summary><c>+7 more</c>, or empty.</summary>
    [ObservableProperty]
    private string _agentsOverflowText = string.Empty;

    [RelayCommand]
    private void SeeAllAgents() => RequestNavigation("ai-discovery");

    internal async Task RefreshAgentsAsync(CancellationToken cancellationToken)
    {
        var config = Services.Config.Config.AiDiscovery;
        var path = Services.Paths.AiDiscoveryStatePath;

        try
        {
            if (!File.Exists(path))
            {
                // Off in config.yaml, and not running in the gateway either (a config with no ai_discovery section is the gateway's default).
                var enabled = config.Enabled || _snapshot.Health?.AiDiscovery?.IsRunning == true;
                SetAgents(
                    DiscoveredAgents.None,
                    enabled
                        ? "No scan has been recorded yet. Try: defenseclaw agent discovery scan"
                        : "AI discovery is off (ai_discovery.enabled is false in config.yaml).");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var agents = await Task.Run(() => OverviewAgentReader.Parse(File.ReadAllText(path), now), cancellationToken).ConfigureAwait(true);
            SetAgents(agents, agents.IsEmpty ? "No AI usage detected yet. Try: defenseclaw agent discovery scan" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // The panel left the screen; the next activation reads again.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SetAgents(DiscoveredAgents.None, $"ai_discovery_state.json could not be read: {ex.Message}");
        }
    }

    private void SetAgents(DiscoveredAgents agents, string note)
    {
        SyncByEquality(AgentRows, agents.Rows, static row => row.Id);
        HasAgents = agents.Rows.Count > 0;
        AgentsNote = note;
        AgentsOverflowText = agents.Overflow > 0 ? $"+{agents.Overflow.ToString(CultureInfo.CurrentCulture)} more" : string.Empty;

        if (agents.IsEmpty && agents.Active == 0)
        {
            AgentsSummary = string.Empty;
            return;
        }

        var parts = new List<string> { $"{agents.Active.ToString(CultureInfo.CurrentCulture)} active" };
        if (agents.New > 0)
        {
            parts.Add($"{agents.New.ToString(CultureInfo.CurrentCulture)} new");
        }

        if (agents.Changed > 0)
        {
            parts.Add($"{agents.Changed.ToString(CultureInfo.CurrentCulture)} changed");
        }

        if (agents.Gone > 0)
        {
            parts.Add($"{agents.Gone.ToString(CultureInfo.CurrentCulture)} gone");
        }

        var health = _snapshot.Health?.AiDiscovery;
        var scanned = DateTimeOffset.TryParse(health?.DetailString("last_scan"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastScan)
            ? lastScan
            : agents.UpdatedAt;
        if (scanned is { } at)
        {
            parts.Add($"scanned {OverviewAgentReader.Age(at, DateTimeOffset.UtcNow)}");
        }

        var mode = health?.DetailString("mode") ?? Services.Config.Config.AiDiscovery.Mode;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            parts.Add($"mode {mode}");
        }

        AgentsSummary = string.Join(" · ", parts);
    }
}
