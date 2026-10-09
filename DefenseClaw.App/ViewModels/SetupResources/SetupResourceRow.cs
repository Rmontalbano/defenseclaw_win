using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels.SetupResources;

/// <summary>What an editor's buttons do. <see cref="Show"/> is the only one that only reads.</summary>
public enum SetupVerb
{
    /// <summary>Opens the setup wizard on <c>add</c>; the wizard reviews its own command.</summary>
    Add,

    Enable,

    Disable,

    /// <summary>Contacts the destination or delivers a message: always reviewed first.</summary>
    Test,

    /// <summary>Reads one row again (<c>setup webhook show</c>); runs without a review because it changes nothing.</summary>
    Show,

    Remove,
}

/// <summary>Where a Setup editor's list is in its life.</summary>
public enum SetupListState
{
    /// <summary>The first read is running.</summary>
    Loading,

    /// <summary>Rows are on screen.</summary>
    Loaded,

    /// <summary>The read worked and the list has nothing in it. Not the same as <see cref="Failed"/>.</summary>
    Empty,

    /// <summary>The read did not work and there are no older rows to show: the list is <b>unknown</b>.</summary>
    Failed,

    /// <summary>The CLI itself was not found.</summary>
    CliUnavailable,
}

/// <summary>How a column of an editor's list is drawn.</summary>
public enum SetupColumnKind
{
    /// <summary>Ordinary text.</summary>
    Text,

    /// <summary>Text in the quiet colour (secondary information).</summary>
    Quiet,

    /// <summary>Monospace (a path, an address).</summary>
    Mono,

    /// <summary>The row's state as a pill (<see cref="SetupResourceRow.StateText"/> in <see cref="SetupResourceRow.StateTone"/>).</summary>
    State,
}

/// <summary>One column of an editor's list.</summary>
/// <param name="Header">The column heading.</param>
/// <param name="Key">Which of <see cref="SetupResourceRow.Cells"/> it shows (ignored for <see cref="SetupColumnKind.State"/>).</param>
/// <param name="Weight">Its share of the free width (star sizing).</param>
/// <param name="MinWidth">The least width it is given.</param>
/// <param name="Kind">How it is drawn.</param>
public sealed record SetupColumn(string Header, string Key, double Weight, double MinWidth, SetupColumnKind Kind = SetupColumnKind.Text);

/// <summary>One line of the details pane: a label and what it says.</summary>
public sealed record SetupFact(string Label, string Value, bool Mono = false);

/// <summary>
/// One row of an editor's list. Immutable: a read makes new rows, and the list keeps the selection by <see cref="Key"/>. It holds only what is
/// shown - texts, a state and the reasons a verb is off for this row - and the parsed record it came from; never an address or a secret
/// the parser did not already cut (the records have no member for one).
/// </summary>
public sealed class SetupResourceRow
{
    private readonly IReadOnlyDictionary<SetupVerb, string> _blocked;

    /// <param name="key">What a command is given to act on this row: a name, or a directory. Exactly as the CLI printed it.</param>
    /// <param name="cells">The text of each column, by <see cref="SetupColumn.Key"/>.</param>
    /// <param name="stateText">The state in a word ("enabled", "missing").</param>
    /// <param name="stateTone">Ok, Warn, Bad or Neutral.</param>
    /// <param name="facts">What the details pane lists.</param>
    /// <param name="source">The parsed record this row shows.</param>
    /// <param name="blocked">Why a verb is off for this row regardless of anything else ("built-in defaults are protected"); a verb that is not here is on.</param>
    public SetupResourceRow(
        string key,
        IReadOnlyDictionary<string, string> cells,
        string stateText,
        string stateTone,
        IReadOnlyList<SetupFact> facts,
        object source,
        IReadOnlyDictionary<SetupVerb, string>? blocked = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Cells = cells ?? throw new ArgumentNullException(nameof(cells));
        StateText = stateText;
        StateTone = stateTone;
        Facts = facts ?? throw new ArgumentNullException(nameof(facts));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _blocked = blocked ?? new Dictionary<SetupVerb, string>();
        Title = DisplayNames.Visible(key);
    }

    /// <summary>What a command is given to act on this row, exactly as the CLI printed it.</summary>
    public string Key { get; }

    /// <summary>The key on one line, with control characters written out.</summary>
    public string Title { get; }

    public IReadOnlyDictionary<string, string> Cells { get; }

    public string StateText { get; }

    public string StateTone { get; }

    public IReadOnlyList<SetupFact> Facts { get; }

    /// <summary>The record the row shows: an <c>ObservabilityDestination</c>, a <c>NotifierWebhook</c> or a <c>TrustedPath</c>.</summary>
    public object Source { get; }

    /// <summary>Why <paramref name="verb"/> is off for this row on its own account, or null when the row has no objection.</summary>
    public string? BlockedReason(SetupVerb verb) => _blocked.TryGetValue(verb, out var reason) ? reason : null;

    /// <summary>What a screen reader says for the row: the name, its state and the main cells.</summary>
    public string AutomationName => $"{Title}, {StateText}" + string.Concat(Cells.Where(c => c.Value.Length > 0).Take(3).Select(c => $", {c.Key} {c.Value}"));

    public override string ToString() => AutomationName;
}
