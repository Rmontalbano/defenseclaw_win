using System.Text.Json;
using DefenseClaw.Core.Correlation;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Inventory;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The AI BOM's IDE Plugins and Agent identities tabs (CUST-376): what the Mac folds into its BOM on a 1.x runtime (it runs <c>aibom scan --only
/// ide_plugins</c> and <c>agent identities --json --limit 1000</c>), read here from the two gateway routes those commands themselves call.
/// <para>
/// <b>Why not the CLI.</b> <c>aibom scan --only ide_plugins</c> is a state-changing command (it records a scan event and posts to the gateway) and
/// <c>agent identities</c> is a process that makes the same authenticated GET as the app's own gateway client. Both are read-only GETs the app can make
/// directly: <c>GET /api/v1/ai-usage/ide-plugins</c> (cmd_aibom.py <c>_fetch_ide_plugins</c>, cmd_agent.py <c>ide_plugins</c>) and <c>GET
/// /api/v1/agents/identities</c> (the Agents panel's directory). No process starts, no review is needed, and nothing is written anywhere. Both are
/// read when the AI BOM view is shown and again on Refresh, and are gated by <see cref="RuntimeCapability.IdeInventory"/>.
/// </para>
/// </summary>
public sealed partial class InventoryPanelViewModel
{
    private int _extrasVersion;

    /// <summary>Test seam: the gateway read, so a test feeds exact answers and opens no socket.</summary>
    internal Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>>? ExtrasFetch { get; set; }

    private void InitBomExtras()
    {
        BomBrowser.OpenAgentsRequested += (_, _) => Services.Navigation.Request("agents");
        ApplyExtrasGate();
    }

    /// <summary>Offers the two tabs only on a runtime that has the routes (hidden on 0.8.10, exactly as before), and the Agents link where the panel exists.</summary>
    private void ApplyExtrasGate()
    {
        BomBrowser.SetExtrasAvailable(Services.Runtime.Check(RuntimeCapability.IdeInventory).IsAvailable);
        BomBrowser.AgentsLinkAvailable = Services.Runtime.Check(RuntimeCapability.CorrelationApi).IsAvailable;
    }

    partial void OnActiveViewChanged(string value)
    {
        if (value == ViewBom)
        {
            _ = LoadBomExtrasSafelyAsync();
        }
    }

    private async Task LoadBomExtrasSafelyAsync()
    {
        try
        {
            await LoadBomExtrasAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A background read must not take the panel down; the tabs say they could not be read.
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"AI BOM IDE and identity read failed: {ex}");
        }
#pragma warning restore CA1031
    }

    /// <summary>Reads both lists. A newer read supersedes an older one still running: only the latest answer is shown.</summary>
    internal async Task LoadBomExtrasAsync(CancellationToken cancellationToken)
    {
        ApplyExtrasGate();
        if (!BomBrowser.ExtrasAvailable)
        {
            return;
        }

        var version = ++_extrasVersion;
        BomBrowser.BeginExtrasLoad();

        Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>> fetch =
            ExtrasFetch ?? ((path, max, ct) => Services.Gateway.GetBoundedJsonAsync(path, max, true, ct));
        var plugins = new IdeInventoryService(fetch).ReadAsync(cancellationToken);
        var identities = new CorrelationService(fetch).ReadDirectoryAsync(InventoryBomBrowser.MaxIdentities, cancellationToken);
        await Task.WhenAll(plugins, identities).ConfigureAwait(true);

        if (version != _extrasVersion)
        {
            return;
        }

        BomBrowser.ShowIdePlugins(plugins.Result);
        BomBrowser.ShowIdentities(identities.Result);
    }
}
