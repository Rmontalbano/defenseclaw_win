namespace DefenseClaw.App.Services;

/// <summary>
/// The one connector filter every screen shares — the Mac's <c>AppState.connectorFilter</c>: "All", or one connector
/// (<c>claudecode</c>, <c>codex</c>, …), applied to alerts, audit rows, logs, activity and the catalogs alike, so the operator
/// narrows the whole app to one agent at once and the sidebar badge agrees with the list it opens.
/// <para>
/// <b>Use.</b> <see cref="Allows"/> is the predicate each screen filters its rows with (<c>rows.Where(r =&gt;
/// scope.Allows(r.Connector))</c>; <see cref="DefenseClaw.Core.Audit.AlertCounts.TallyFor"/> takes it directly). <see cref="Set"/>
/// and <see cref="Cycle"/> change the scope (a chip menu and a shortcut); <see cref="Connectors"/> is the roster the chip offers;
/// <see cref="Changed"/> says to re-filter. The roster is the gateway snapshot's <c>ActiveConnectors</c> — configured connectors
/// first, then live ones — and follows it.
/// </para>
/// <para>
/// <b>Rules, all the Mac's (<c>normalize_filter</c>, <c>filter_allows</c>).</b> Scope <c>null</c> means All and allows everything.
/// An explicit scope allows only an exact match (ignoring case and padding), and <i>hides</i> rows with no connector
/// (<c>Allows(null)</c> is false): platform-wide rows belong to no agent, so one agent's view does not show them. A scope that cannot
/// be trapped in: when the scoped connector leaves the roster, or the roster shrinks to one or none (a filter with nothing to
/// choose between, and no chip to clear it), the scope resets to All by itself. <see cref="Cycle"/> steps All → first → second → …
/// → All, and is All when there is nothing to choose between.
/// </para>
/// <para>
/// <b>Threading.</b> State is locked and every member is safe from any thread. <see cref="Changed"/> is raised on the thread
/// that made the change — the UI thread, for <see cref="Set"/>/<see cref="Cycle"/> from a click and for the roster, which follows
/// the monitor's UI-thread <c>StateChanged</c> — after the lock is released. No UI in this type; the chip is a later issue.
/// </para>
/// </summary>
internal sealed class ConnectorScope : IDisposable
{
    private readonly object _gate = new();
    private readonly IGatewaySnapshotSource? _source;
    private IReadOnlyList<string> _connectors = Array.Empty<string>();
    private string? _current;
    private bool _disposed;

    /// <param name="source">
    /// The monitor whose <c>ActiveConnectors</c> is the roster; read now and on every <c>StateChanged</c>. Null for a scope fed by
    /// hand with <see cref="UpdateRoster"/> (tests).
    /// </param>
    public ConnectorScope(IGatewaySnapshotSource? source = null)
    {
        _source = source;
        if (source is not null)
        {
            _connectors = Normalize(source.Current.ActiveConnectors);
            source.StateChanged += OnStateChanged;
        }
    }

    /// <summary>
    /// Raised after the scope or the roster changed: re-filter, and re-read <see cref="Connectors"/> if you show them. Not raised
    /// for a call that changed nothing.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The connector everything is narrowed to, spelled as the roster spells it; null for All.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>True when narrowed to one connector.</summary>
    public bool IsScoped => Current is not null;

    /// <summary>The connectors that can be chosen, in the order the chip lists them (configured first, then live). A snapshot: safe to hold.</summary>
    public IReadOnlyList<string> Connectors
    {
        get
        {
            lock (_gate)
            {
                return _connectors;
            }
        }
    }

    /// <summary>True when there is more than one connector, i.e. a scope is worth offering: what shows or hides the chip.</summary>
    public bool CanScope => Connectors.Count > 1;

    /// <summary>
    /// Whether a row attributed to <paramref name="connector"/> is visible under the current scope: always under All; under a
    /// scope only an exact match (ignoring case and padding) — and never a row with no connector (<c>null</c>, or blank).
    /// </summary>
    public bool Allows(string? connector)
    {
        var scope = Current;
        if (scope is null)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(connector) &&
               string.Equals(connector.Trim(), scope, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Narrows everything to <paramref name="connector"/>, or back to All for null or blank. Only a connector on the roster can be
    /// chosen (its roster spelling is kept), and only while there is more than one: anything else changes nothing and returns
    /// false — the scope would only be reset at the next roster update. Returns true when the scope now is what was asked for.
    /// </summary>
    public bool Set(string? connector)
    {
        bool changed;
        bool accepted;

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(connector))
            {
                changed = _current is not null;
                _current = null;
                accepted = true;
            }
            else if (_connectors.Count > 1 && Find(_connectors, connector) is { } spelled)
            {
                changed = !string.Equals(_current, spelled, StringComparison.Ordinal);
                _current = spelled;
                accepted = true;
            }
            else
            {
                changed = false;
                accepted = false;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }

        return accepted;
    }

    /// <summary>
    /// Steps the scope All → first connector → second → … → All and returns the new scope (null for All). With one connector or none
    /// there is nothing to step between: the scope is All. A scope that is no longer on the roster steps as if it were All.
    /// </summary>
    public string? Cycle()
    {
        string? next;
        bool changed;

        lock (_gate)
        {
            var previous = _current;
            if (_connectors.Count <= 1)
            {
                next = null;
            }
            else
            {
                var index = previous is null ? -1 : IndexOf(_connectors, previous);

                // Positions 0..Count-1 are the connectors, Count is All; from All (or an unknown scope) the first connector is next.
                var position = index < 0 ? 0 : index + 1;
                next = position >= _connectors.Count ? null : _connectors[position];
            }

            changed = !string.Equals(previous, next, StringComparison.Ordinal);
            _current = next;
        }

        if (changed)
        {
            RaiseChanged();
        }

        return next;
    }

    /// <summary>
    /// Replaces the roster (the gateway snapshot's <c>ActiveConnectors</c>) and applies the reset rule: a scope whose connector is gone,
    /// or a roster of one or none, goes back to All. Called from the monitor's <c>StateChanged</c>; public so a test, or a host without
    /// a monitor, can feed it.
    /// </summary>
    public void UpdateRoster(IReadOnlyList<string> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);

        var roster = Normalize(connectors);
        bool changed;

        lock (_gate)
        {
            changed = !roster.SequenceEqual(_connectors, StringComparer.Ordinal);
            _connectors = roster;

            if (_current is not null)
            {
                var stillThere = roster.Count > 1 ? Find(roster, _current) : null;
                if (stillThere is null)
                {
                    _current = null;
                    changed = true;
                }
                else if (!string.Equals(stillThere, _current, StringComparison.Ordinal))
                {
                    // Same connector, respelled by the roster (config says Codex, health says codex): follow it.
                    _current = stillThere;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_source is not null)
        {
            _source.StateChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => UpdateRoster(e.Snapshot.ActiveConnectors);

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A misbehaving subscriber must not silence the others or fail the change.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                System.Diagnostics.Trace.TraceError($"connector scope: a Changed subscriber threw: {ex}");
            }
        }
    }

    /// <summary>Trimmed, blanks dropped, duplicates (ignoring case) dropped keeping the first: the roster as a chip lists it.</summary>
    private static IReadOnlyList<string> Normalize(IReadOnlyList<string> connectors)
    {
        var roster = new List<string>(connectors.Count);
        foreach (var name in connectors)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var trimmed = name.Trim();
            if (!roster.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                roster.Add(trimmed);
            }
        }

        return roster;
    }

    private static int IndexOf(IReadOnlyList<string> roster, string connector)
    {
        for (var i = 0; i < roster.Count; i++)
        {
            if (string.Equals(roster[i], connector.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? Find(IReadOnlyList<string> roster, string connector)
    {
        var index = IndexOf(roster, connector);
        return index < 0 ? null : roster[index];
    }
}
