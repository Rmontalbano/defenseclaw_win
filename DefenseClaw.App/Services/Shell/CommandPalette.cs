using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DefenseClaw.App.Services;

/// <summary>
/// One entry in the command palette. Built fresh every time the palette opens, so the title of a
/// toggle ("Turn off Start with Windows") and the enabled state of the gateway controls are the
/// truth at that moment rather than whatever they were at launch.
/// </summary>
/// <param name="Id">Stable key ("nav.overview", "gateway.restart").</param>
/// <param name="Title">What the row says and what the search matches first.</param>
/// <param name="Category">"Panel", "App" or "Gateway" — shown as a chip, also searchable.</param>
/// <param name="Description">One line under the title; the disabled reason replaces it when unavailable.</param>
/// <param name="Shortcut">Display text of the chord that does the same thing, or null.</param>
/// <param name="Keywords">Extra search terms that are not in the title.</param>
/// <param name="IsEnabled">False greys the row out and makes Enter do nothing.</param>
/// <param name="DisabledReason">Why it is unavailable; shown in place of the description.</param>
/// <param name="Run">What Enter does. Only ever a named app action or a navigation.</param>
internal sealed record ShellCommand(
    string Id,
    string Title,
    string Category,
    string Description,
    string? Shortcut,
    string Keywords,
    bool IsEnabled,
    string? DisabledReason,
    Action Run);

/// <summary>
/// A palette row: the command plus what a screen reader is told. <see cref="ToString"/> is the
/// spoken sentence because the list's item container has no other text to announce — without
/// it a screen reader reads the record's type name.
/// </summary>
internal sealed class PaletteItem
{
    public PaletteItem(ShellCommand command)
    {
        Command = command;
    }

    public ShellCommand Command { get; }

    public string Title => Command.Title;

    public string Category => Command.Category;

    public string? Shortcut => Command.Shortcut;

    public bool HasShortcut => !string.IsNullOrEmpty(Command.Shortcut);

    public bool IsEnabled => Command.IsEnabled;

    /// <summary>The line under the title: the description, or why the command is unavailable.</summary>
    public string DetailLine => Command.IsEnabled
        ? Command.Description
        : Command.DisabledReason ?? "Not available right now.";

    public string AutomationName
    {
        get
        {
            var spoken = $"{Command.Title}, {Command.Category}";
            if (HasShortcut)
            {
                spoken += $", shortcut {Command.Shortcut}";
            }

            return Command.IsEnabled
                ? spoken
                : $"{spoken}, unavailable. {Command.DisabledReason}".TrimEnd();
        }
    }

    public override string ToString() => AutomationName;
}

/// <summary>
/// The palette's state: the search text, the ranked matches, and which row Enter will run.
/// <para>
/// Focus stays in the search box the whole time (that is what makes it keyboard-first), so the
/// selection is moved here by <see cref="MoveSelection"/> rather than by list focus, and disabled
/// rows are skipped so an arrow key never parks on something Enter would refuse.
/// </para>
/// </summary>
internal sealed partial class CommandPaletteViewModel : ObservableObject
{
    private IReadOnlyList<ShellCommand> _all = Array.Empty<ShellCommand>();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private PaletteItem? _selected;

    /// <summary>
    /// "Go to Alerts, 2 of 13" / "No matching commands". Shown in the palette's footer and used as a
    /// polite live region: focus stays in the search box, so this is how a screen reader hears
    /// which row the arrow keys landed on and how many rows the typed text left.
    /// </summary>
    [ObservableProperty]
    private string _statusLine = string.Empty;

    public ObservableCollection<PaletteItem> Results { get; } = new();

    /// <summary>Raised when a command was chosen (Enter or click) and should run after the palette closes.</summary>
    public event EventHandler<ShellCommand>? CommandChosen;

    /// <summary>Raised when the palette should close (Esc, or after a command was chosen).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Swaps in a fresh command list and clears the search.</summary>
    public void Load(IReadOnlyList<ShellCommand> commands)
    {
        _all = commands ?? throw new ArgumentNullException(nameof(commands));

        if (Query.Length == 0)
        {
            Refilter();
        }
        else
        {
            Query = string.Empty; // OnQueryChanged refilters.
        }
    }

    partial void OnQueryChanged(string value) => Refilter();

    partial void OnSelectedChanged(PaletteItem? value) => UpdateStatusLine();

    /// <summary>Moves the selection by <paramref name="delta"/> rows, skipping unavailable ones; clamps at the ends.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        var step = delta < 0 ? -1 : 1;
        var remaining = Math.Abs(delta);
        var candidate = index;

        while (remaining > 0)
        {
            var next = candidate + step;
            while (next >= 0 && next < Results.Count && !Results[next].IsEnabled)
            {
                next += step;
            }

            if (next < 0 || next >= Results.Count)
            {
                break;
            }

            candidate = next;
            remaining--;
        }

        if (candidate >= 0 && candidate < Results.Count)
        {
            Selected = Results[candidate];
        }
    }

    /// <summary>Runs the selected row if it is available; returns whether anything was chosen.</summary>
    public bool ChooseSelected() => Choose(Selected);

    /// <summary>Runs <paramref name="item"/> if it is available (the mouse path).</summary>
    public bool Choose(PaletteItem? item)
    {
        if (item is null || !item.IsEnabled)
        {
            return false;
        }

        CommandChosen?.Invoke(this, item.Command);
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Refilter()
    {
        var ranked = Rank(_all, Query);

        Results.Clear();
        foreach (var command in ranked)
        {
            Results.Add(new PaletteItem(command));
        }

        Selected = Results.FirstOrDefault(item => item.IsEnabled);

        // OnSelectedChanged does not fire when the selection did not change (same first row), and the
        // count may have.
        UpdateStatusLine();
    }

    private void UpdateStatusLine()
    {
        if (Results.Count == 0)
        {
            StatusLine = "No matching commands";
            return;
        }

        var position = Selected is null ? 0 : Results.IndexOf(Selected) + 1;
        StatusLine = Selected is null
            ? $"{Results.Count} commands, none available"
            : $"{Selected.Title}, {position} of {Results.Count}";
    }

    /// <summary>
    /// Ranks <paramref name="commands"/> against <paramref name="query"/>: every whitespace-separated
    /// term has to match somewhere (title, category or keywords, case-insensitive), and a term scores
    /// higher the closer to the front of the title it lands. Ties keep registry order, which is the
    /// order a user would expect with no query typed (panels in sidebar order, then actions).
    /// </summary>
    internal static IReadOnlyList<ShellCommand> Rank(IReadOnlyList<ShellCommand> commands, string? query)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var terms = (query ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (terms.Length == 0)
        {
            return commands;
        }

        return commands
            .Select((command, order) => (command, order, score: Score(command, terms)))
            .Where(entry => entry.score >= 0)
            .OrderByDescending(entry => entry.score)
            .ThenBy(entry => entry.order)
            .Select(entry => entry.command)
            .ToList();
    }

    private static int Score(ShellCommand command, string[] terms)
    {
        var total = 0;

        foreach (var term in terms)
        {
            int score;
            if (command.Title.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
            }
            else if (StartsAWord(command.Title, term))
            {
                score = 80;
            }
            else if (command.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score = 60;
            }
            else if (command.Category.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score = 30;
            }
            else if (command.Keywords.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score = 20;
            }
            else
            {
                return -1;
            }

            total += score;
        }

        return total;
    }

    private static bool StartsAWord(string text, string term)
    {
        var index = 0;
        while (index < text.Length)
        {
            index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (index == 0 || !char.IsLetterOrDigit(text[index - 1]))
            {
                return true;
            }

            index++;
        }

        return false;
    }
}
