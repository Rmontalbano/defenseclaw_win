using System.Diagnostics;
using System.Windows;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Services;

/// <summary>One navigable panel: its label, its group, its icon, and how to build it.</summary>
/// <param name="Id">Stable key, used for deep links and for restoring the last panel.</param>
/// <param name="Title">Sidebar label and panel header. Keep in sync with the view's header.</param>
/// <param name="Group">One of <see cref="PanelCatalog.Groups"/>.</param>
/// <param name="Icon">Sidebar glyph.</param>
/// <param name="ViewType">The <see cref="FrameworkElement"/> the navigation frame hosts.</param>
/// <param name="ViewModelFactory">Builds the view-model bound to <paramref name="ViewType"/>.</param>
public sealed record PanelDescriptor(
    string Id,
    string Title,
    string Group,
    SymbolRegular Icon,
    Type ViewType,
    Func<AppServices, PanelViewModelBase> ViewModelFactory);

/// <summary>
/// The single registry of panels, and the bridge between WPF-UI's navigation frame and
/// this app's view/view-model pairs.
/// <para>
/// <b>This file is shell-owned and should not need editing when panel content lands.</b>
/// It names each panel's view type and view-model factory and nothing else — no layout,
/// no data, no per-panel behaviour — so replacing a placeholder
/// <c>{Name}Panel.xaml</c> / <c>{Name}PanelViewModel.cs</c> pair leaves this untouched.
/// Only adding or removing a panel outright is a reason to come back here.
/// </para>
/// </summary>
public sealed class PanelCatalog : INavigationViewPageProvider
{
    /// <summary>Sidebar groups, in display order.</summary>
    public static readonly IReadOnlyList<string> Groups = new[]
    {
        "Monitor",
        "Govern",
        "Discover",
        "Configure",
    };

    private readonly AppServices _services;
    private readonly Dictionary<Type, FrameworkElement> _views = new();

    public PanelCatalog(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Panels = new PanelDescriptor[]
        {
            // Monitor
            new("overview", "Overview", "Monitor", SymbolRegular.AppsListDetail24,
                typeof(OverviewPanel), s => new OverviewPanelViewModel(s)),
            new("alerts", "Alerts", "Monitor", SymbolRegular.AlertUrgent24,
                typeof(AlertsPanel), s => new AlertsPanelViewModel(s)),
            new("logs", "Logs", "Monitor", SymbolRegular.DocumentBulletList24,
                typeof(LogsPanel), s => new LogsPanelViewModel(s)),
            new("audit", "Audit", "Monitor", SymbolRegular.DatabaseSearch24,
                typeof(AuditPanel), s => new AuditPanelViewModel(s)),
            new("activity", "Activity", "Monitor", SymbolRegular.History32,
                typeof(ActivityPanel), s => new ActivityPanelViewModel(s)),

            // Govern
            new("skills", "Skills", "Govern", SymbolRegular.PuzzlePiece24,
                typeof(SkillsPanel), s => new SkillsPanelViewModel(s)),
            new("mcps", "MCPs", "Govern", SymbolRegular.PlugConnected24,
                typeof(McpsPanel), s => new McpsPanelViewModel(s)),
            new("plugins", "Plugins", "Govern", SymbolRegular.PuzzleCube24,
                typeof(PluginsPanel), s => new PluginsPanelViewModel(s)),
            new("tools", "Tools", "Govern", SymbolRegular.WrenchScrewdriver24,
                typeof(ToolsPanel), s => new ToolsPanelViewModel(s)),

            // Discover
            new("inventory", "Inventory", "Discover", SymbolRegular.BookDatabase24,
                typeof(InventoryPanel), s => new InventoryPanelViewModel(s)),
            new("ai-discovery", "AI Discovery", "Discover", SymbolRegular.Bot24,
                typeof(AiDiscoveryPanel), s => new AiDiscoveryPanelViewModel(s)),
            new("registries", "Registries", "Discover", SymbolRegular.Library24,
                typeof(RegistriesPanel), s => new RegistriesPanelViewModel(s)),

            // Configure
            new("setup", "Setup", "Configure", SymbolRegular.ShieldSettings24,
                typeof(SetupPanel), s => new SetupPanelViewModel(s)),
        };
    }

    /// <summary>Every panel, in sidebar order.</summary>
    public IReadOnlyList<PanelDescriptor> Panels { get; }

    /// <summary>The panel the shell opens on first launch.</summary>
    public PanelDescriptor Default => Panels[0];

    /// <summary>Raised when a panel's <c>InitializeAsync</c> throws, so the shell can say so.</summary>
    public event EventHandler<PanelFaultEventArgs>? PanelFaulted;

    public IEnumerable<PanelDescriptor> InGroup(string group) =>
        Panels.Where(p => string.Equals(p.Group, group, StringComparison.Ordinal));

    public PanelDescriptor? ByViewType(Type viewType) =>
        Panels.FirstOrDefault(p => p.ViewType == viewType);

    public PanelDescriptor? ById(string id) =>
        Panels.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Called by WPF-UI's navigation frame. Builds the view, binds a freshly created
    /// view-model, and kicks off the panel's one-shot initialization. Instances are
    /// cached, so navigating away and back keeps panel state.
    /// </summary>
    public object? GetPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);

        if (_views.TryGetValue(pageType, out var cached))
        {
            return cached;
        }

        var descriptor = ByViewType(pageType);
        if (descriptor is null)
        {
            return null;
        }

        if (Activator.CreateInstance(pageType) is not FrameworkElement view)
        {
            return null;
        }

        var viewModel = descriptor.ViewModelFactory(_services);
        view.DataContext = viewModel;
        _views[pageType] = view;

        _ = InitializeAsync(descriptor, viewModel);
        return view;
    }

    private async Task InitializeAsync(PanelDescriptor descriptor, PanelViewModelBase viewModel)
    {
        try
        {
            await viewModel.EnsureInitializedAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // One panel failing to load must not take down the shell.
        catch (Exception ex)
        {
            Trace.TraceError($"Panel '{descriptor.Id}' failed to initialize: {ex}");
            PanelFaulted?.Invoke(this, new PanelFaultEventArgs(descriptor, ex));
        }
#pragma warning restore CA1031
    }
}

public sealed class PanelFaultEventArgs : EventArgs
{
    public PanelFaultEventArgs(PanelDescriptor panel, Exception exception)
    {
        Panel = panel;
        Exception = exception;
    }

    public PanelDescriptor Panel { get; }

    public Exception Exception { get; }
}
