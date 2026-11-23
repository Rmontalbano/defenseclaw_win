using System.Net.Http;
using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Time;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The "Model routing" and "Tokenomics" rows of the Configuration card (DefenseClaw 1.0.10, CUST-457): state rows only, in the runtime's own words.
/// <list type="bullet">
/// <item><b>Model routing</b> comes from <c>/health</c>'s <c>routing</c> block, which the poll already holds: <c>disabled</c>, or
/// <c>running · rules · 2 models</c>; one row per route-only connector (<c>ready</c>, or <c>not ready (cause)</c> with the runtime's problem and fix).</item>
/// <item><b>Tokenomics</b> is on only when config.yaml turns on both <c>observability.tokenomics.enabled</c> and
/// <c>observability.tokenomics.local_store.enabled</c> (both default off). Only then is the authenticated <c>GET /usage</c> asked, one bounded read of the
/// last 24 hours by model, at most every <see cref="ModelUsageRefreshInterval"/>; a failure is a neutral "usage could not be read".</item>
/// </list>
/// Both rows exist only on a runtime that has the capability (<see cref="RuntimeCapability.RoutingCli"/>, <see cref="RuntimeCapability.Tokenomics"/>), so an
/// older runtime's card is exactly what it was. A default install reads calm: <c>disabled</c> and <c>off, no usage recorded</c> in the neutral tone, and no
/// attention row at all - routing is something a person turns on, and its problems are the runtime's to report on the row.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>How old the usage read may be before the poll asks again: a few calls an hour do not need a fresher number.</summary>
    internal static readonly TimeSpan ModelUsageRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Why the usage figure is smaller than what an agent used when the agent is in hook mode. The runtime records the model calls it carries itself
    /// (its judge and decision-model calls) and, with route-only or the proxy, the agents' calls; a hook sees none of an agent's own calls.
    /// </summary>
    internal const string HookModeUsageNote = "DefenseClaw records only the model calls it carries itself (its own judge calls, for instance), not the agents' own model calls in hook mode.";

    private static readonly string[] ModelProxyConnectors = { "openclaw", "zeptoclaw" };

    private ModelUsageSummary? _modelUsage;
    private DateTimeOffset? _modelUsageReadAt;
    private MonotonicStamp _modelUsageStamp = MonotonicStamp.Never;
    private int _modelUsageReading;
    private ConfigDocument? _tokenomicsSource;
    private (bool Enabled, bool LocalStore) _tokenomicsConfig;

    /// <summary>Replaces the read of <c>GET /usage</c> (a test's canned answer); null reads the real gateway.</summary>
    internal Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>>? ModelUsageFetch { get; set; }

    /// <summary>
    /// Adds the routing and tokenomics rows to the Configuration card's rows. No I/O: the routing block is the poll's, the usage figure the last read's.
    /// A runtime without the capability adds nothing.
    /// </summary>
    private void AddModelRoutingRows(List<(string Label, string Value, string Tone)> rows, string suffix)
    {
        var capabilities = Services.Runtime.Capabilities;
        if (capabilities.Has(RuntimeCapability.RoutingCli) && RoutingHealth.From(_snapshot.Health) is { } routing)
        {
            rows.Add(("Model routing" + suffix, routing.Text, routing.ReportsProblem ? "Warn" : "Neutral"));
            if (!routing.IsOff)
            {
                foreach (var connector in routing.RouteOnly)
                {
                    rows.Add(($"Route-only {connector.Connector}" + suffix, connector.Text, connector.Ready ? "Neutral" : "Warn"));
                }
            }
        }

        if (capabilities.Has(RuntimeCapability.Tokenomics))
        {
            rows.Add(("Tokenomics" + suffix, TokenomicsText(), "Neutral"));
        }
    }

    private string TokenomicsText()
    {
        var (enabled, localStore) = TokenomicsConfig();
        if (!enabled)
        {
            return "off, no usage recorded";
        }

        if (!localStore)
        {
            return "on, but the local usage store is off, so no usage is kept to show";
        }

        var usage = _modelUsage;
        if (usage is null)
        {
            return "on, usage not read yet";
        }

        switch (usage.Status)
        {
            case ModelUsageStatus.Unreadable:
                return "on, usage could not be read";
            case ModelUsageStatus.StoreOff:
                return "on in config.yaml, but the running gateway reports its usage store off";
            default:
                var text = "on, " + ModelUsageReader.Describe(usage);

                // An old figure is never shown as current: with no /health answer, or a read older than two refresh intervals, say when it was read.
                if (_modelUsageReadAt is { } readAt && (_snapshot.Health is null || _modelUsageStamp.HasElapsed(ModelUsageRefreshInterval * 2)))
                {
                    text += $" (read at {readAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture)})";
                }

                return CarriesNoAgentCalls() ? text + ". " + HookModeUsageNote : text;
        }
    }

    /// <summary>True when nothing on this install sends an agent's model calls through DefenseClaw: no route-only connector reported, and no proxy connector.</summary>
    private bool CarriesNoAgentCalls() =>
        (RoutingHealth.From(_snapshot.Health)?.RouteOnly.Count ?? 0) == 0 &&
        !Roster().Any(static name => ModelProxyConnectors.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>The two tokenomics switches of config.yaml, read once per parsed config. Absent, unreadable or oversized text is off, the runtime's default.</summary>
    private (bool Enabled, bool LocalStore) TokenomicsConfig()
    {
        var config = Services.Config;
        if (!ReferenceEquals(config, _tokenomicsSource))
        {
            _tokenomicsConfig = (false, false);
            if (config.RawText is { Length: > 0 } text && text.Length <= OverviewConfigFacts.MaxLength && ConfigYamlFlatten.TryFlatten(text) is { } keys)
            {
                _tokenomicsConfig = (IsOn(keys, "observability.tokenomics.enabled"), IsOn(keys, "observability.tokenomics.local_store.enabled"));
            }

            _tokenomicsSource = config;
        }

        return _tokenomicsConfig;
    }

    /// <summary>A switch the runtime's loader reads as true: the YAML words for it, unquoted.</summary>
    private static bool IsOn(IReadOnlyDictionary<string, string> keys, string key) =>
        keys.TryGetValue(key, out var value) && value.StartsWith("p:", StringComparison.Ordinal) &&
        ConfigYamlFlatten.Display(value).Trim().ToLowerInvariant() is "true" or "yes" or "on";

    /// <summary>
    /// Reads the last 24 hours of model usage when it is due (or <paramref name="force"/>d: Refresh) and tokenomics is on in config.yaml; never otherwise, so an
    /// install with it off never calls the route. One read at a time; it never throws, and a failure is the row's neutral "usage could not be read".
    /// </summary>
    internal async Task RefreshModelUsageAsync(bool force, CancellationToken cancellationToken)
    {
        var (enabled, localStore) = TokenomicsConfig();
        if (!Services.Runtime.Capabilities.Has(RuntimeCapability.Tokenomics) || !enabled || !localStore)
        {
            if (_modelUsage is not null)
            {
                _modelUsage = null;
                _modelUsageReadAt = null;
                _modelUsageStamp = MonotonicStamp.Never;
                BuildConfiguration();
            }

            return;
        }

        // Nothing answers while the gateway is down: keep what was read, and ask again when it is back.
        if (_snapshot.Health is null || (!force && !_modelUsageStamp.HasElapsed(ModelUsageRefreshInterval)))
        {
            return;
        }

        if (Interlocked.Exchange(ref _modelUsageReading, 1) == 1)
        {
            return;
        }

        try
        {
            _modelUsageStamp = MonotonicStamp.Now();
            var fetch = ModelUsageFetch ?? ((path, max, ct) => Services.Gateway.GetBoundedJsonAsync(path, max, true, ct));
            var result = await fetch(ModelUsageReader.Route, ModelUsageReader.MaxBytes, cancellationToken).ConfigureAwait(true);
            try
            {
                _modelUsage = ModelUsageReader.FromGateway(result);
                _modelUsageReadAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                result.Value?.Dispose();
            }

            BuildConfiguration();
        }
        catch (OperationCanceledException)
        {
            // The panel left the screen; the next activation reads again.
            _modelUsageStamp = MonotonicStamp.Never;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _modelUsage = ModelUsageSummary.Unreadable;
            _modelUsageReadAt = null;
            BuildConfiguration();
        }
        finally
        {
            _ = Interlocked.Exchange(ref _modelUsageReading, 0);
        }
    }

    /// <summary>
    /// Starts <see cref="RefreshModelUsageAsync"/> without waiting for it, so a slow gateway can never hold up the reads after it in the refresh chain
    /// (<c>status --json</c>). Nothing escapes: the read is the row's own business, and a failure there is its "usage could not be read".
    /// </summary>
    private async Task StartModelUsageRefreshAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshModelUsageAsync(force, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Left as it was; the next poll asks again.
        }
    }
}
