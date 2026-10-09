using CommunityToolkit.Mvvm.ComponentModel;

namespace DefenseClaw.App.ViewModels;

/// <summary>The chips of the status strip, in the order they are drawn (the TUI's: subsystem health, then what needs attention, then context).</summary>
public enum StripChipKey
{
    /// <summary>The gateway's detail sentence beside the state pill. Not a chip: plain text that gives way first.</summary>
    Detail,

    Watchdog,

    Guardrail,

    /// <summary>Required credentials that are not set: the first two names and a count of the rest.</summary>
    Keys,

    Alerts,

    /// <summary>The connector: its name, <c>All connectors (N)</c> or <c>X (filtered)</c>.</summary>
    Connector,

    Redaction,

    /// <summary>The policy posture, <c>observe</c> or <c>action</c>.</summary>
    Policy,

    /// <summary>A command this app started is running.</summary>
    Running,

    /// <summary>The last good gateway poll is older than three intervals.</summary>
    Stale,

    Version,

    /// <summary><c>+N</c>: the chips that do not fit at this width, listed in its tooltip. Always present; the panel shows it only when something is hidden.</summary>
    Overflow,
}

/// <summary>How a chip is drawn.</summary>
public enum StripChipKind
{
    /// <summary>Plain caption text (the detail sentence).</summary>
    Sentence,

    /// <summary>A pill tinted by its tone, with the words in it.</summary>
    Badge,

    /// <summary>A pill tinted by its tone, led by the state mark of the look (a dot, a status circle, a flat shape).</summary>
    State,

    /// <summary>A neutral tag: a fact about the install, not a reading.</summary>
    Chip,
}

/// <summary>
/// One chip of the status strip, as the strip's view-model holds it: whether it shows, what it says, its tone, its tooltip and the sentence a
/// screen reader says for it. The view-model changes these in place, so a chip that did not change keeps its visuals across a poll.
/// <para>
/// <b>Priority</b> is the chip's place in the collapse order: lower stays longer, 0 never collapses (nothing has it today - the state pill and the
/// buttons are outside the strip). A narrow window hides the highest numbers first (see <c>ChipStripPanel</c>).
/// </para>
/// </summary>
public sealed partial class StripChip : ObservableObject
{
    internal StripChip(StripChipKey key, StripChipKind kind, int priority)
    {
        Key = key;
        Kind = kind;
        Priority = priority;
    }

    public StripChipKey Key { get; }

    public StripChipKind Kind { get; }

    /// <summary>Where the chip stands in the collapse order: lower stays longer; 0 never collapses.</summary>
    public int Priority { get; }

    /// <summary>The detail sentence is the one element that shrinks (it trims with an ellipsis) before any chip collapses.</summary>
    public bool IsFlexible => Key == StripChipKey.Detail;

    /// <summary>The <c>+N</c> chip: the panel reserves room for it when, and only when, a chip is hidden.</summary>
    public bool IsOverflowSlot => Key == StripChipKey.Overflow;

    /// <summary>False when there is nothing to say (no missing keys, not stale, no version yet): the chip takes no room.</summary>
    [ObservableProperty]
    private bool _isShown;

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Ok / Warn / Bad / Critical / Low / Neutral: the design system's tone keys, bound to <c>Tag</c>.</summary>
    [ObservableProperty]
    private string _tone = "Neutral";

    [ObservableProperty]
    private string _toolTip = string.Empty;

    /// <summary>What a screen reader says: the whole fact as a sentence, which the visible text may only abbreviate.</summary>
    [ObservableProperty]
    private string _automationName = string.Empty;

    /// <summary>
    /// The chip as one short line, for a list of what is hidden (<c>Guardrail: running</c>, <c>Redaction: per-route · unredacted</c>): the
    /// visible text, unless that leaves out the state a colour would otherwise carry.
    /// </summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Shows the chip with all of what it says. <paramref name="summary"/> is the visible text when not given.</summary>
    internal void Show(string text, string tone, string toolTip, string automationName, string? summary = null)
    {
        Text = text;
        Tone = tone;
        ToolTip = toolTip;
        AutomationName = automationName;
        Summary = summary ?? text;
        IsShown = true;
    }

    /// <summary>Takes the chip out of the strip. Its words stay as they were, so a chip that comes back and says the same does not redraw.</summary>
    internal void Hide() => IsShown = false;

    /// <summary>What UI Automation falls back to for an item with no name of its own: the sentence, not the type.</summary>
    public override string ToString() => AutomationName.Length > 0 ? AutomationName : Text;
}
