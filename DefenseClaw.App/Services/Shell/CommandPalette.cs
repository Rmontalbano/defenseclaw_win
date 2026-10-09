using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services.Settings;

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
/// <param name="Run">What Enter does. Only ever a named app action, a navigation, or a curated CLI command (reviewed unless read-only).</param>
/// <param name="Cli">The curated CLI command this row stands for, when it is one: the detail pane shows its argv and Copy / Run.</param>
/// <param name="Copy">Puts the row's command line on the clipboard (a curated CLI row).</param>
/// <param name="RunWith">Runs a CLI row that takes one typed value (<see cref="CuratedCommand.Form"/>) with that value, which the palette has already checked.</param>
/// <param name="CopyWith">Puts the row's command line with that value on the clipboard.</param>
internal sealed record ShellCommand(
    string Id,
    string Title,
    string Category,
    string Description,
    string? Shortcut,
    string Keywords,
    bool IsEnabled,
    string? DisabledReason,
    Action Run,
    CuratedCommand? Cli = null,
    Action? Copy = null,
    Action<string>? RunWith = null,
    Action<string>? CopyWith = null);

/// <summary>
/// A palette row: the command plus what a screen reader is told. <see cref="ToString"/> is the
/// spoken sentence because the list's item container has no other text to announce — without
/// it a screen reader reads the record's type name.
/// <para>
/// A CLI row that takes one value (a skill name, a URL, a path) also holds what the operator typed for it in the detail pane
/// (<see cref="ArgumentText"/>): that is the only state a row has, and a row lives only as long as the list it is in.
/// </para>
/// </summary>
internal sealed partial class PaletteItem : ObservableObject
{
    /// <param name="command">The row.</param>
    /// <param name="isRecent">True when the row is listed first because the operator ran it lately (<see cref="PaletteRecents"/>).</param>
    public PaletteItem(ShellCommand command, bool isRecent = false)
    {
        Command = command;
        IsRecent = isRecent;
        Form = command.Cli?.Form is { } form && command.RunWith is not null ? form : null;
    }

    public ShellCommand Command { get; }

    /// <summary>True for a row shown at the top of an empty search because it was one of the last commands run from the palette; the row says "Recent".</summary>
    public bool IsRecent { get; }

    /// <summary>The form that takes this row's one value, or null when the row takes none (or the value is too much for a form: it is copied to complete).</summary>
    public ArgumentForm? Form { get; }

    public bool HasArgumentForm => Form is not null;

    /// <summary>What the operator has typed for the value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArgvPreview))]
    [NotifyPropertyChangedFor(nameof(ArgumentProblem))]
    private string _argumentText = string.Empty;

    /// <summary>True once the operator tried to run with a value the form refused; the sentence shows until the text changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArgumentProblem))]
    private bool _argumentRefused;

    partial void OnArgumentTextChanged(string value) => ArgumentRefused = false;

    /// <summary>Why the typed value was refused, or empty.</summary>
    public string ArgumentProblem => ArgumentRefused && Form?.Check(ArgumentText, out _) is { } problem ? problem : string.Empty;

    public string ArgumentPlaceholder => Form?.Placeholder ?? string.Empty;

    public string ArgumentAutomationName => Form is null ? string.Empty : $"Value for {Command.Title}: {Form.Label}";

    public string Title => Command.Title;

    public string Category => Command.Category;

    public string? Shortcut => Command.Shortcut;

    public bool HasShortcut => !string.IsNullOrEmpty(Command.Shortcut);

    public bool IsEnabled => Command.IsEnabled;

    /// <summary>True for a curated CLI row; the detail pane shows only for those.</summary>
    public bool IsCli => Command.Cli is not null;

    /// <summary>The command as it will run (argv preview), for a CLI row; with the typed value on it while the row takes one.</summary>
    public string ArgvPreview => Command.Cli is { } cli ? (Form is null ? cli.CommandLineText : cli.CommandLineWith(ArgumentText)) : string.Empty;

    /// <summary>"Read-only" / "Changes state" / "Destructive", for a CLI row.</summary>
    public string TierLabel => Command.Cli is { } cli ? CommandReview.LabelFor(cli.Tier) : string.Empty;

    /// <summary>What the detail pane says about pressing Run.</summary>
    public string RunNote => Command.Cli switch
    {
        null => string.Empty,
        { TypesInApp: true } => Form is null
            ? "Needs the variable's NAME: Run copies the command for you to complete."
            : "Type the variable's NAME above, then Run. The Setup page opens a masked box for the value, you review the command with the value hidden, and the app " +
              "types it at the CLI's own prompt - never on a command line. If the app cannot do that here, a console window opens instead.",
        { NeedsTerminal: true } cli => "Needs a terminal: it asks questions or reads a hidden prompt. Run copies the command for you to paste into one" +
            (cli.NeedsArguments ? $", then add {string.Join(", ", cli.RequiredArguments)}." : "."),
        { NeedsArguments: true } cli when Form is null => $"Needs {string.Join(", ", cli.RequiredArguments)}: Run copies the command for you to complete.",
        { NeedsArguments: true } => "Type the value above, then Run. You review the exact command before it runs.",
        { Tier: DefenseClaw.Core.Cli.CommandTier.ReadOnly } => "Read-only: runs straight away and shows in Activity.",
        _ => "You review the exact command before it runs.",
    };

    public string RunLabel => Command.Cli switch
    {
        { TypesInApp: true } => "Enter value…",
        { NeedsTerminal: true } => "Copy to run in a terminal",
        { NeedsArguments: true } when Form is null => "Copy to complete",
        _ => "Run",
    };

    /// <summary>The line under the title: the description, or why the command is unavailable.</summary>
    public string DetailLine => Command.IsEnabled
        ? Command.Description
        : Command.DisabledReason ?? "Not available right now.";

    public string AutomationName
    {
        get
        {
            var spoken = $"{Command.Title}, {Command.Category}";
            if (IsRecent)
            {
                spoken += ", recent";
            }

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

    /// <summary>
    /// Where the palette remembers what it ran (<c>settings.json</c>, see <see cref="PaletteRecents"/>). With a store, an empty search lists the
    /// last few commands first and choosing a command adds it to them; without one (a window with no settings, a test) the palette remembers
    /// nothing and lists the rows in registry order, as before.
    /// </summary>
    public AppSettingsStore? RecentsStore { get; set; }

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private PaletteItem? _selected;

    /// <summary>"12 of 240 commands": rows the search left, of every row there is.</summary>
    [ObservableProperty]
    private string _countLine = string.Empty;

    /// <summary>
    /// "Go to Alerts, 2 of 13" / "No matching commands". Shown in the palette's footer and used as a
    /// polite live region: focus stays in the search box, so this is how a screen reader hears
    /// which row the arrow keys landed on and how many rows the typed text left.
    /// </summary>
    [ObservableProperty]
    private string _statusLine = string.Empty;

    /// <summary>
    /// "21 hidden on Windows": how many entries of the TUI command registry the runtime does not run here, so the count above is not
    /// read as the whole registry. Empty when none is hidden.
    /// </summary>
    [ObservableProperty]
    private string _hiddenNote = string.Empty;

    /// <summary>The note's tooltip: how many entries for each reason, in the runtime's own words.</summary>
    [ObservableProperty]
    private string _hiddenDetail = string.Empty;

    public ObservableCollection<PaletteItem> Results { get; } = new();

    /// <summary>The selected row when it is a curated CLI command (the detail pane's subject); null otherwise.</summary>
    public PaletteItem? Detail => Selected is { IsCli: true } item ? item : null;

    /// <summary>Total number of commands loaded, whatever the search says.</summary>
    public int TotalCount => _all.Count;

    /// <summary>Raised when a command was chosen (Enter or click) and should run after the palette closes.</summary>
    public event EventHandler<ShellCommand>? CommandChosen;

    /// <summary>Raised when the palette should close (Esc, or after a command was chosen).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised when the chosen row needs its value first (nothing typed yet, or a value the form refused): the palette stays open and the view
    /// puts the caret in the box.
    /// </summary>
    public event EventHandler? ArgumentRequested;

    /// <summary>Swaps in a fresh command list and clears the search.</summary>
    /// <param name="commands">Every row.</param>
    /// <param name="hiddenNote">What to say about the registry entries left out (<see cref="HiddenNote"/>).</param>
    /// <param name="hiddenDetail">The note's tooltip.</param>
    public void Load(IReadOnlyList<ShellCommand> commands, string hiddenNote = "", string hiddenDetail = "")
    {
        _all = commands ?? throw new ArgumentNullException(nameof(commands));
        HiddenNote = hiddenNote ?? string.Empty;
        HiddenDetail = hiddenDetail ?? string.Empty;

        if (Query.Length == 0)
        {
            Refilter();
        }
        else
        {
            Query = string.Empty; // OnQueryChanged refilters.
        }
    }

    /// <summary>
    /// Swaps the command list for a newer one without clearing the search (the runtime answered while the palette was already open, and
    /// with it the registry the CLI rows come from). The selection stays on the same command when it is still there.
    /// </summary>
    public void Reload(IReadOnlyList<ShellCommand> commands, string hiddenNote = "", string hiddenDetail = "")
    {
        var keep = Selected?.Command.Id;
        var typed = Selected?.ArgumentText ?? string.Empty;
        _all = commands ?? throw new ArgumentNullException(nameof(commands));
        HiddenNote = hiddenNote ?? string.Empty;
        HiddenDetail = hiddenDetail ?? string.Empty;
        Refilter();
        if (keep is not null && Results.FirstOrDefault(i => i.IsEnabled && i.Command.Id == keep) is { } same)
        {
            Selected = same;

            // A value half typed for the selected row is still wanted when the rows are swapped under it.
            if (same.HasArgumentForm && typed.Length > 0)
            {
                same.ArgumentText = typed;
            }
        }
    }

    /// <summary>
    /// Copies the selected CLI row's command (the detail pane's Copy button) - with the value typed for it when the row takes one and the
    /// form accepts what is there; false when the row has no command.
    /// </summary>
    public bool CopySelected()
    {
        if (Selected is not { Command.Copy: { } copy } item)
        {
            return false;
        }

        if (item.Form is { } form && item.Command.CopyWith is { } copyWith && form.Check(item.ArgumentText, out var value) is null)
        {
            copyWith(value);
            return true;
        }

        copy();
        return true;
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

    /// <summary>
    /// Runs <paramref name="item"/> if it is available (the mouse path). A row that takes one typed value does not run without it: the
    /// first Enter (or click) asks for the value - <see cref="ArgumentRequested"/>, palette still open - and the next, with a value the
    /// form accepts, runs the row with exactly that value. A refused value runs nothing and says why.
    /// </summary>
    public bool Choose(PaletteItem? item)
    {
        if (item is null || !item.IsEnabled)
        {
            return false;
        }

        var command = item.Command;
        if (item.Form is { } form)
        {
            if (string.IsNullOrWhiteSpace(item.ArgumentText))
            {
                ArgumentRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (form.Check(item.ArgumentText, out var value) is not null)
            {
                item.ArgumentRefused = true;
                ArgumentRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }

            var runWith = command.RunWith!;
            command = command with { Run = () => runWith(value) };
        }

        Remember(command);
        CommandChosen?.Invoke(this, command);
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Adds the command that is about to run to the palette's recents (settings.json), newest first. Never fails the choice: a settings file that cannot be written keeps the change in memory.</summary>
    private void Remember(ShellCommand command)
    {
        if (RecentsStore is not { } store || !PaletteRecents.IsWorthRemembering(command))
        {
            return;
        }

        _ = store.Update(settings => settings with
        {
            Palette = settings.Palette with { RecentCommandIds = PaletteRecents.Push(settings.Palette.RecentCommandIds, command.Id) },
        });
    }

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Refilter()
    {
        // An empty search lists what was run last first - a row that cannot be chosen now among them, greyed with its reason; a typed one
        // ranks by what it matches.
        var browsing = string.IsNullOrWhiteSpace(Query);
        var recents = 0;
        var source = browsing && RecentsStore is { } store
            ? PaletteRecents.Promote(_all, store.Current.Palette.RecentCommandIds, out recents)
            : _all;
        var ranked = Rank(source, Query);

        Results.Clear();
        var position = 0;
        foreach (var command in ranked)
        {
            Results.Add(new PaletteItem(command, isRecent: position++ < recents));
        }

        Selected = Results.FirstOrDefault(item => item.IsEnabled);

        // OnSelectedChanged does not fire when the selection did not change (same first row), and the
        // count may have.
        UpdateStatusLine();
        OnPropertyChanged(nameof(TotalCount));
        CountLine = $"{Results.Count} of {_all.Count} commands";
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
    /// higher the closer to the front of the title it lands: the start of the title, the start of a word, the
    /// first letters of the title's words (<c>rg</c> for "Restart gateway", <c>gta</c> for "Go to Alerts"),
    /// anywhere in the title, then the category, then the keywords. Ties keep registry order, which is the
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

    /// <summary>
    /// What a CLI row's match is worth less than the same match on a panel or an app action. The TUI's names for its commands (<c>skill</c>,
    /// <c>setup</c>, <c>alerts</c>, <c>doctor</c>) are also what the panels are called, and typing one should find where it goes before the
    /// command that has the same name: a CLI row that matches from the front of its name is as good as a panel or action that matches
    /// at a word, and a tie keeps the registry order, which has the panels first.
    /// </summary>
    private const int CliPenalty = 20;

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
            else if (term.Length >= 2 && MatchesInitials(command.Title, term))
            {
                score = 70;
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

            total += command.Cli is null ? score : Math.Max(1, score - CliPenalty);
        }

        return total;
    }

    /// <summary>
    /// True when <paramref name="term"/> is the front of the first letters of the title's words, counting a hyphenated word as one word or as
    /// several: <c>rlc</c> and <c>rrlc</c> both find "Re-run last command", <c>slu</c> and <c>slou</c> both find "setup local-observability up".
    /// Which of the two an operator means is not for the palette to guess.
    /// </summary>
    private static bool MatchesInitials(string title, string term) =>
        Initials(title).StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
        SpacedInitials(title).StartsWith(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first letter (or digit) of each word of <paramref name="title"/>, in order: <c>Rgs</c> for "Restart gateway status", <c>ssa</c> for
    /// "scan skill --all", <c>slou</c> for "setup local-observability up". A word is a run of letters and digits, so a hyphen or a dash ends one.
    /// A search of two letters or more that is the front of this (<c>rg</c>, <c>gta</c>) finds the row without its words being typed out.
    /// </summary>
    internal static string Initials(string title)
    {
        var initials = new System.Text.StringBuilder();
        var inWord = false;
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (!inWord)
                {
                    _ = initials.Append(ch);
                }

                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return initials.ToString();
    }

    /// <summary>
    /// The same with a word being whatever is between spaces, so a hyphenated word counts once: <c>Rlc</c> for "Re-run last command", <c>slu</c> for
    /// "setup local-observability up". A word that starts with punctuation (<c>--all</c>) is represented by its first letter or digit.
    /// </summary>
    internal static string SpacedInitials(string title)
    {
        var initials = new System.Text.StringBuilder();
        foreach (var word in title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var first = word.FirstOrDefault(char.IsLetterOrDigit);
            if (first != default)
            {
                _ = initials.Append(first);
            }
        }

        return initials.ToString();
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
