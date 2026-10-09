using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The 0.8.10 TUI's small Logs controls that the panel lacked (CUST-263): the count of what arrived while paused in the status line, <c>E</c> and <c>W</c> for
/// the Errors and Warnings+ presets, and <c>Home</c> / <c>End</c> for the first row and the newest.
/// <para>
/// <b>"+N since pause".</b> The TUI's hint bar reads "Paused. Space resumes. New lines since pause: +N." Pausing here is turning "live" off: the list keeps
/// receiving rows but stops scrolling to them. The count is the rows the source on screen received since then, before the filters, as the TUI's is (it is the
/// difference of the source's line counts; here it counts arrivals, so a full 5,000-line buffer, whose length no longer moves, still counts). A log file counts the
/// lines its tail delivers; Verdicts and Events count the rows a poll found that the one before did not - they are read only while on screen, so what lands while
/// another stream is shown is not counted. The baselines are taken for every stream at the moment of pausing, so switching stream while paused still counts from
/// then; resuming forgets them. It is part of the status line (<see cref="StatusLineCount"/>), where it reads "· +7 since pause" and says "+0" when nothing came.
/// </para>
/// <para>
/// <b>E and W</b> are the TUI's <c>e</c> and <c>w</c>: each toggles its preset against "all" (<c>e</c> on Errors goes back to All, not to No Noise). They are plain
/// keys in the TUI and here; the view takes them only with no modifier and not from a text box or a drop-down list, so they collide with no chord (Ctrl+E is
/// Audit's export, Ctrl+Shift+E the shell's) and cannot be typed away.
/// </para>
/// <para>
/// <b>Home and End</b> are the TUI's <c>g</c> and <c>G</c>: Home selects the first row and pauses, so the list stays where it was put; End selects the newest row and
/// resumes following. Selecting a row opens its inspector, as the arrow keys do.
/// </para>
/// </summary>
public sealed partial class LogsPanelViewModel
{
    private static readonly string[] AllSources = { GatewaySource, VerdictsSource, EventsSource, WatchdogSource };

    /// <summary>What each source had received at the moment the list was paused; empty while it is not.</summary>
    private readonly Dictionary<string, long> _pauseBaseline = new(StringComparer.Ordinal);

    /// <summary>How many rows the source on screen has received since the list was paused; 0 while it follows the newest row.</summary>
    internal int SincePause
    {
        get
        {
            if (AutoScroll || !_pauseBaseline.TryGetValue(ActiveSource, out var baseline))
            {
                return 0;
            }

            return (int)Math.Clamp(ArrivedOn(ActiveSource) - baseline, 0, int.MaxValue);
        }
    }

    /// <summary>" · +7 since pause" on the status line while paused (also "+0"); nothing while following.</summary>
    private string SincePauseSuffix => AutoScroll
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $" · +{SincePause:N0} since pause");

    private long ArrivedOn(string source) => source switch
    {
        GatewaySource => _gateway.Arrived,
        WatchdogSource => _watchdog.Arrived,
        VerdictsSource => _verdicts.Arrived,
        EventsSource => _events.Arrived,
        _ => 0,
    };

    /// <summary>Pausing takes a baseline of every stream; resuming forgets them. Either way the status line is redone.</summary>
    private void NotePause(bool paused)
    {
        _pauseBaseline.Clear();
        if (paused)
        {
            foreach (var source in AllSources)
            {
                _pauseBaseline[source] = ArrivedOn(source);
            }
        }

        UpdateStatusText();
    }

    /// <summary>The TUI's <c>e</c>: the Errors preset, or back to All when it already is.</summary>
    [RelayCommand]
    private void ToggleErrors() => SelectedPreset = SelectedPreset == LogPresets.Errors ? LogPresets.All : LogPresets.Errors;

    /// <summary>The TUI's <c>w</c>: the Warnings+ preset, or back to All when it already is.</summary>
    [RelayCommand]
    private void ToggleWarnings() => SelectedPreset = SelectedPreset == LogPresets.WarningsPlus ? LogPresets.All : LogPresets.WarningsPlus;

    /// <summary>
    /// The TUI's <c>g</c>, on Home: the first row, selected, and the list paused where it stands. Returns the row (null when the list is empty) so the view can bring
    /// it into sight.
    /// </summary>
    public LogEntry? JumpToStart()
    {
        if (DisplayedLines.Count == 0)
        {
            return null;
        }

        AutoScroll = false;
        SelectedEntry = DisplayedLines[0];
        return SelectedEntry;
    }

    /// <summary>
    /// The TUI's <c>G</c>, on End: the newest row, selected, and the list following again. Returns the row (null when the list is empty, which still resumes) so the
    /// view can bring it into sight.
    /// </summary>
    public LogEntry? JumpToEnd()
    {
        AutoScroll = true;
        if (DisplayedLines.Count == 0)
        {
            return null;
        }

        SelectedEntry = DisplayedLines[^1];
        return SelectedEntry;
    }
}
