using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Time;

namespace DefenseClaw.App.ViewModels;

/// <summary>What the Effective policy card is doing.</summary>
public enum EffectivePolicyCardState
{
    /// <summary>Not drawn: the runtime does not have the route, the gateway answered 404 or 405, or nothing answered. Older runtimes look exactly as before.</summary>
    Hidden,

    /// <summary>The resolved policy is on the card.</summary>
    Shown,

    /// <summary>The gateway answered with something that is not a policy; the card says what, and shows no rows rather than the base configuration under a policy's name.</summary>
    Error,
}

/// <summary>
/// The Effective policy card (CUST-375): the policy the gateway applies to this account on the scoped (or first) connector - the profile it resolved to, the
/// mode, the block-at and alert-at severities, the rule pack, and the policy digest and generation. It is the Mac's <c>loadAppliedPolicy</c>: one authenticated read
/// of <c>GET /api/v1/guardrail/profiles/resolve</c> through the app's own gateway client (the token never leaves it and is never logged) and one of
/// <c>/health</c> for the digest. It is drawn only while the runtime has the route (<see cref="RuntimeCapability.EffectivePolicy"/>) and the gateway serves it;
/// while it is drawn, the config-derived card is relabelled "Base configuration" so the two are never mistaken for each other.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>How long a resolved policy is reused for the same account and connector (the Mac's number).</summary>
    internal static readonly TimeSpan EffectivePolicyRefreshInterval = TimeSpan.FromSeconds(60);

    private MonotonicStamp _effectivePolicyStamp = MonotonicStamp.Never;
    private string _effectivePolicyKey = string.Empty;
    private int _effectivePolicyReading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEffectivePolicy))]
    private EffectivePolicyCardState _effectivePolicyState = EffectivePolicyCardState.Hidden;

    /// <summary><c>Effective policy · codex</c>.</summary>
    [ObservableProperty]
    private string _effectivePolicyTitle = "Effective policy";

    /// <summary>The caption under the rows: the gateway's own notes about the resolution, or the sentence that says why there are no rows.</summary>
    [ObservableProperty]
    private string _effectivePolicyNote = string.Empty;

    /// <summary>True while the card is drawn (a policy or an error).</summary>
    public bool ShowEffectivePolicy => EffectivePolicyState != EffectivePolicyCardState.Hidden;

    /// <summary>True when the card is drawn with a problem instead of a policy.</summary>
    public bool EffectivePolicyIsError => EffectivePolicyState == EffectivePolicyCardState.Error;

    public ObservableCollection<ConfigRow> EffectivePolicyRows { get; } = new();

    /// <summary>Test seam: the gateway read, so a test feeds exact answers and opens no socket.</summary>
    internal Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>>? EffectivePolicyFetch { get; set; }

    /// <summary>The title of the config-derived card: "Base configuration" while the Effective policy card is drawn beside it, "Configuration" otherwise.</summary>
    private string ConfigurationBaseTitle => ShowEffectivePolicy ? "Base configuration" : "Configuration";

    /// <summary>The label of the scoped config-derived mode row: "Configured mode" beside the effective card, "Mode" otherwise.</summary>
    private string ConfiguredModeLabel => ShowEffectivePolicy ? "Configured mode" : "Mode";

    /// <summary>The account the gateway resolves a policy for: the signed-in Windows account, as the Mac sends <c>NSUserName()</c>.</summary>
    private static string EffectivePolicyUser => Environment.UserName;

    /// <summary>The connector the card is about: the scope when there is one, else the first connector on the install; empty when there is none.</summary>
    private string EffectivePolicyConnector => Services.ConnectorScope.Current ?? Roster().FirstOrDefault() ?? string.Empty;

    partial void OnEffectivePolicyStateChanged(EffectivePolicyCardState oldValue, EffectivePolicyCardState newValue)
    {
        OnPropertyChanged(nameof(EffectivePolicyIsError));
        if ((oldValue == EffectivePolicyCardState.Hidden) != (newValue == EffectivePolicyCardState.Hidden))
        {
            RelabelConfiguration();
        }
    }

    /// <summary>Redraws the config-derived card under the title and mode label it has now. No I/O.</summary>
    private void RelabelConfiguration()
    {
        var scope = Services.ConnectorScope.Current;
        ConfigurationTitle = ConfigurationBaseTitle + (scope is null ? string.Empty : " · " + scope.ToLowerInvariant());
        BuildConfiguration();
    }

    /// <summary>
    /// Reads the effective policy when it is due (or <paramref name="force"/>d: Refresh, or a scope that moved). Never throws; one read at a time. The same
    /// account and connector are not asked again within <see cref="EffectivePolicyRefreshInterval"/>.
    /// </summary>
    internal async Task RefreshEffectivePolicyAsync(bool force, CancellationToken cancellationToken)
    {
        if (!Services.Runtime.Check(RuntimeCapability.EffectivePolicy).IsAvailable)
        {
            ApplyEffectivePolicy(EffectivePolicyRead.Unsupported, string.Empty);
            return;
        }

        var user = EffectivePolicyUser;
        var connector = EffectivePolicyConnector;
        var key = connector + "\n" + user;
        if (!force && string.Equals(key, _effectivePolicyKey, StringComparison.Ordinal) && !_effectivePolicyStamp.HasElapsed(EffectivePolicyRefreshInterval))
        {
            return;
        }

        if (Interlocked.Exchange(ref _effectivePolicyReading, 1) == 1)
        {
            return;
        }

        try
        {
            _effectivePolicyKey = key;
            _effectivePolicyStamp = MonotonicStamp.Now();
            var fetch = EffectivePolicyFetch ?? ((path, max, ct) => Services.Gateway.GetBoundedJsonAsync(path, max, true, ct));
            var resolve = await fetch(EffectivePolicyReader.Path(user, connector), EffectivePolicyReader.MaxBytes, cancellationToken).ConfigureAwait(true);
            GatewayResult<JsonDocument>? health = null;
            if (resolve.IsOk)
            {
                health = await fetch(EffectivePolicyReader.HealthRoute, EffectivePolicyReader.MaxBytes, cancellationToken).ConfigureAwait(true);
            }

            EffectivePolicyRead read;
            try
            {
                read = EffectivePolicyReader.FromGateway(resolve, health);
            }
            finally
            {
                resolve.Value?.Dispose();
                health?.Value?.Dispose();
            }

            // The scope may have moved while the answer was on its way: it is the answer to a question nobody is asking now.
            if (string.Equals(connector, EffectivePolicyConnector, StringComparison.Ordinal))
            {
                ApplyEffectivePolicy(read, connector);
            }
        }
        catch (OperationCanceledException)
        {
            // The panel left the screen; the next activation reads again.
            _effectivePolicyStamp = MonotonicStamp.Never;
        }
        finally
        {
            _ = Interlocked.Exchange(ref _effectivePolicyReading, 0);
        }
    }

    /// <summary>Draws a read: the rows of a policy, the sentence of an error, or nothing for an unsupported or silent gateway.</summary>
    internal void ApplyEffectivePolicy(EffectivePolicyRead read, string connector)
    {
        ArgumentNullException.ThrowIfNull(read);

        EffectivePolicyTitle = connector.Length > 0 ? "Effective policy · " + connector.ToLowerInvariant() : "Effective policy";
        switch (read.Status)
        {
            case EffectivePolicyStatus.Ok when read.Policy is { } policy:
                SyncByEquality(EffectivePolicyRows, EffectivePolicyRowsOf(policy, connector), static row => row.Label);
                EffectivePolicyNote = string.Join(" ", policy.Warnings);
                EffectivePolicyState = EffectivePolicyCardState.Shown;
                break;

            case EffectivePolicyStatus.Error:
                SyncByEquality(EffectivePolicyRows, Array.Empty<ConfigRow>(), static row => row.Label);
                EffectivePolicyNote = "Effective policy unavailable: " + read.Message;
                EffectivePolicyState = EffectivePolicyCardState.Error;
                break;

            default:
                // Unsupported (a runtime without the route), or nothing answered: the card does not exist, and what was on it is not kept.
                SyncByEquality(EffectivePolicyRows, Array.Empty<ConfigRow>(), static row => row.Label);
                EffectivePolicyNote = string.Empty;
                EffectivePolicyState = EffectivePolicyCardState.Hidden;
                break;
        }
    }

    private static List<ConfigRow> EffectivePolicyRowsOf(EffectivePolicy policy, string connector)
    {
        var rows = new List<(string Label, string Value)>
        {
            ("Applies to", (EffectivePolicyUser.Length > 0 ? EffectivePolicyUser : "this account") + (connector.Length > 0 ? " on " + connector : string.Empty)),
            ("Profile", policy.ProfilesConfigured ? policy.Profile : "none configured (the base configuration applies)"),
        };
        Add(rows, "Matched by", policy.Match);
        Add(rows, "Mode", policy.Mode);
        Add(rows, "Block at", policy.BlockAt);
        Add(rows, "Alert at", policy.AlertAt);
        Add(rows, "Rule pack", policy.RulePack);
        Add(rows, "Policy digest", policy.Digest);
        Add(rows, "Generation", policy.Generation);

        return rows.Select((row, index) => new ConfigRow { Label = row.Label, Value = row.Value, Alternate = index % 2 == 1 }).ToList();

        static void Add(List<(string Label, string Value)> list, string label, string value)
        {
            if (value.Length > 0)
            {
                list.Add((label, value));
            }
        }
    }

    /// <summary>After a scope change: ask again for the new connector (a comparison unless the key moved). Quiet before the panel is first on screen.</summary>
    private void NoteScopeForEffectivePolicy()
    {
        if (_activation is null || !Services.Runtime.Check(RuntimeCapability.EffectivePolicy).IsAvailable)
        {
            return;
        }

        var key = EffectivePolicyConnector + "\n" + EffectivePolicyUser;
        if (!string.Equals(key, _effectivePolicyKey, StringComparison.Ordinal))
        {
            _ = RefreshEffectivePolicyAsync(force: true, ActiveToken);
        }
    }
}
