namespace DefenseClaw.App.Services;

/// <summary>
/// What the always-alive status strip shows that no poll of its own brings: facts a panel has already read for itself and hands over here,
/// so the strip repeats them without reading anything again.
/// <para>
/// <b>Two facts.</b> The names of the required credentials that are not set (the Setup panel's Credentials card, <c>keys list --json</c>,
/// CUST-266) and the aggregate redaction label (the Overview's Observability card, <c>observability plan</c>, CUST-272). Each is read by one
/// panel, on its own schedule and into its own view-model, and each panel is built on its first visit and idle while hidden - so the strip
/// cannot ask the panel, and must not ask the CLI. A fact nobody has handed over yet is <c>null</c>, not "none": the strip draws nothing for
/// it rather than a reassuring blank.
/// </para>
/// <para>
/// <b>Cost.</b> Two fields and an event. Nothing here polls, reads or runs anything. <see cref="Changed"/> is raised only when a fact differs from
/// what is held, on the thread that published it (the UI thread, for the panels); a subscriber marshals if it needs to.
/// </para>
/// </summary>
internal sealed class StatusFacts
{
    private readonly object _lock = new();
    private IReadOnlyList<string>? _missingKeys;
    private string? _redaction;

    /// <summary>Raised after a fact changed. Not raised for a publication that says what is already held.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The required credentials that were not set when the Credentials card last read <c>keys list</c>, in the card's order; empty when all are
    /// set. Null until a read has landed (opening Setup makes one). Names only - the card has no value to hand over.
    /// </summary>
    public IReadOnlyList<string>? MissingKeys
    {
        get
        {
            lock (_lock)
            {
                return _missingKeys;
            }
        }
    }

    /// <summary>
    /// The Overview's aggregate redaction label (<c>per-route · unredacted</c>, <c>per-route (loading)</c>, <c>per-route (unavailable)</c>), exactly
    /// as <c>ObservabilityPlan.RedactionSummary</c> words it; null until the Overview has built its Observability card.
    /// </summary>
    public string? Redaction
    {
        get
        {
            lock (_lock)
            {
                return _redaction;
            }
        }
    }

    /// <summary>The Credentials card read the list: these required credentials are not set (none: every required credential is).</summary>
    public void PublishMissingKeys(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var list = names.Where(static n => !string.IsNullOrWhiteSpace(n)).Select(static n => n.Trim()).ToArray();
        bool changed;
        lock (_lock)
        {
            changed = _missingKeys is null || !_missingKeys.SequenceEqual(list, StringComparer.Ordinal);
            _missingKeys = list;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>The Overview worked out its aggregate redaction label.</summary>
    public void PublishRedaction(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        bool changed;
        lock (_lock)
        {
            changed = !string.Equals(_redaction, label, StringComparison.Ordinal);
            _redaction = label;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

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
#pragma warning disable CA1031 // A misbehaving subscriber must not fail the panel that published, or silence the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                System.Diagnostics.Trace.TraceError($"status facts: a Changed subscriber threw: {ex}");
            }
        }
    }
}
