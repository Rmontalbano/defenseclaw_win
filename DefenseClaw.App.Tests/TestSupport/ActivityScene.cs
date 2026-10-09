using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// The shell stand-in (<see cref="PanelShell"/>) with an Activity panel in it, built over a scratch data directory, and the
/// handles the layout tests need on it: the card list, its scroll viewer, the cards that are actually built, and the output
/// list of a card. All members are UI-thread only (<see cref="UiThread"/>) except <see cref="Open"/> and <see cref="Dispose"/>.
/// </summary>
internal sealed class ActivityScene : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    private ActivityScene(int width, int height, Func<string, InstallationContext>? installation)
    {
        _services = TestServices.Create(_temp, installation: installation?.Invoke(_temp.Path));
        Name = $"{width}x{height}";
        Shell = UiThread.Run(() =>
        {
            var shell = new PanelShell(_services, width, height);
            _ = shell.Show<ActivityPanel>();
            return shell;
        });
        ViewModel = UiThread.Run(() => (ActivityPanelViewModel)Shell.ViewModel);
    }

    public string Name { get; }

    public PanelShell Shell { get; }

    public ActivityPanelViewModel ViewModel { get; }

    public OffscreenHost Host => Shell.Host;

    public CliRunner Cli => _services.Cli;

    /// <summary>The composition the panel runs on: a test turns the installation read-only through its <c>Installation</c>.</summary>
    public AppServices Services => _services;

    /// <summary>What the entries' actions have reported (copied, cancel refused...), in order.</summary>
    public List<string> Notices { get; } = new();

    /// <param name="width">The host's width.</param>
    /// <param name="height">The host's height.</param>
    /// <param name="installation">
    /// Makes the installation the composition starts with from its scratch folder (CUST-308: <see cref="TestInstallations.ManagedAt"/>); null is the
    /// usual writable one.
    /// </param>
    public static ActivityScene Open(int width, int height, Func<string, InstallationContext>? installation = null) => new(width, height, installation);

    /// <summary>The list of cards (the panel's <c>Cards</c>).</summary>
    public ItemsControl Cards =>
        (ItemsControl)Shell.Page!.FindName("Cards") ?? throw new InvalidOperationException("The panel has no card list.");

    /// <summary>The card list's own scroll viewer - the panel's page scroll.</summary>
    public ScrollViewer CardScroll
    {
        get
        {
            var cards = Cards;
            _ = cards.ApplyTemplate();
            return (ScrollViewer)cards.Template.FindName("CardScroll", cards);
        }
    }

    /// <summary>The panel of the card list, whose children are exactly the cards that have been built.</summary>
    public VirtualizingStackPanel CardPanel =>
        VisualTree.Find<VirtualizingStackPanel>(CardScroll) ?? throw new InvalidOperationException("The card list has no panel.");

    /// <summary>The cards that are built right now (containers in the visual tree), top to bottom.</summary>
    public IReadOnlyList<FrameworkElement> RealizedCards() => CardPanel.Children.OfType<FrameworkElement>().ToList();

    /// <summary>The entries whose cards are built right now, top to bottom.</summary>
    public IReadOnlyList<ActivityRow> RealizedRows() => RealizedCards().Select(card => (ActivityRow)card.DataContext).ToList();

    /// <summary>The card built for <paramref name="row"/>, or null when it is not in view (or near it).</summary>
    public FrameworkElement? CardOf(ActivityRow row) => RealizedCards().FirstOrDefault(card => ReferenceEquals(card.DataContext, row));

    /// <summary>Where a card's top edge is, relative to the top of the card list's viewport (negative: scrolled partly out of view).</summary>
    public double TopOf(FrameworkElement card) => card.TranslatePoint(default, CardScroll).Y;

    /// <summary>
    /// Adds an entry for <paramref name="invocation"/> at the top of the list and, by default, opens its output. UI thread only.
    /// <paramref name="rerun"/> is what the entry's Rerun does (null: the row has none, as before).
    /// </summary>
    public ActivityRow AddRow(CliInvocation invocation, bool expand = true, Func<CliInvocation, CommandTier, Task<string>>? rerun = null)
    {
        var row = new ActivityRow(invocation, _services.Cli, Notices.Add, rerun: rerun);
        ViewModel.Rows.Insert(0, row);
        ViewModel.IsEmpty = false;
        row.IsExpanded = expand;
        Host.Relayout();
        return row;
    }

    /// <summary>
    /// Fills the list with <paramref name="count"/> finished entries, newest first (<c>Rows[0]</c> is <c>--n0</c>), each with a
    /// few lines of output; <paramref name="configure"/> may change any of them before it is shown. One layout at the end.
    /// </summary>
    public IReadOnlyList<ActivityRow> AddInvocations(int count, Action<int, CliInvocation>? configure = null)
    {
        var rows = new List<ActivityRow>(count);
        for (var i = 0; i < count; i++)
        {
            var invocation = InvocationFactory.Create(false, "skill", "list", $"--n{i}");
            InvocationFactory.AppendNumbered(invocation, 4, prefix: $"n{i} line");
            InvocationFactory.Finish(invocation);
            configure?.Invoke(i, invocation);

            var row = new ActivityRow(invocation, _services.Cli, Notices.Add);
            ViewModel.Rows.Add(row);
            rows.Add(row);
        }

        ViewModel.IsEmpty = ViewModel.Rows.Count == 0;
        Host.Relayout();
        return rows;
    }

    /// <summary>The output list of <paramref name="row"/>'s card. The card must be built and open.</summary>
    public ListBox ListFor(ActivityRow row) =>
        VisualTree.Find<ListBox>(Shell.Page!, list => ReferenceEquals(list.DataContext, row))
        ?? throw new InvalidOperationException("The entry's output list was not built.");

    /// <summary>Scrolls the card list to a pixel offset and lays it out.</summary>
    public void ScrollCardsTo(double offset)
    {
        CardScroll.ScrollToVerticalOffset(offset);
        Host.Relayout();
    }

    /// <summary>How far the page itself is scrolled (the panel's outer scroll viewer).</summary>
    public double PageOffset() => CardScroll.VerticalOffset;

    public void Render(string name) => RenderTo.Png(Host, $"{name}");

    public void Dispose()
    {
        UiThread.Run(Shell.Dispose);
        _services.Dispose();
        _temp.Dispose();
    }
}
