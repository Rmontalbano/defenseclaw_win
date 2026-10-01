using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>One entry of the connector chip's menu: "All connectors", or one connector.</summary>
public sealed class ConnectorScopeItem
{
    internal ConnectorScopeItem(string? connector, bool isSelected)
    {
        Connector = connector;
        IsSelected = isSelected;
    }

    /// <summary>The connector this entry scopes to; null for All.</summary>
    public string? Connector { get; }

    public bool IsSelected { get; }

    public string Label => Connector ?? ConnectorScopeViewModel.AllLabel;

    public override string ToString() => Label;
}

/// <summary>
/// What the page toolbar's connector chip (<c>DcConnectorScopeChip</c>) binds to: the shared <see cref="ConnectorScope"/> as a label,
/// a menu of "All connectors" + the roster, and the commands that change it. The scope is the one source of truth; this only shows it.
/// <para>
/// <see cref="IsVisible"/> is true only with more than one connector (<see cref="ConnectorScope.CanScope"/>): with one there is nothing
/// to choose between. Changes arrive on whatever thread made them (the monitor's UI-thread event, a click), so they are marshalled to
/// the UI thread before the bound properties are raised.
/// </para>
/// </summary>
public sealed partial class ConnectorScopeViewModel : ObservableObject
{
    public const string AllLabel = "All connectors";

    private readonly ConnectorScope _scope;

    internal ConnectorScopeViewModel(ConnectorScope scope)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Rebuild();
        _scope.Changed += OnScopeChanged;
    }

    /// <summary>All, then every connector on the roster; the current scope is marked.</summary>
    public ObservableCollection<ConnectorScopeItem> Items { get; } = new();

    /// <summary>True when there is more than one connector, so the chip shows.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>The chip's text: the connector, or "All connectors".</summary>
    [ObservableProperty]
    private string _label = AllLabel;

    /// <summary>True when narrowed to one connector.</summary>
    [ObservableProperty]
    private bool _isScoped;

    /// <summary>The chip's tooltip and accessible name.</summary>
    public string Description => IsScoped
        ? $"Connector scope: {Label}. Every list is narrowed to this connector. Click to change; Ctrl+Shift+M steps to the next."
        : "Connector scope: all connectors. Click to narrow every list to one; Ctrl+Shift+M steps to the next.";

    /// <summary>Scopes the app to the entry's connector (null: All).</summary>
    [RelayCommand]
    private void Select(ConnectorScopeItem? item) => _ = _scope.Set(item?.Connector);

    /// <summary>Steps All, first, second, ... All (what Ctrl+Shift+M does).</summary>
    [RelayCommand]
    private void Cycle() => _ = _scope.Cycle();

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Rebuild();
        }
        else
        {
            _ = dispatcher.BeginInvoke(Rebuild);
        }
    }

    private void Rebuild()
    {
        var current = _scope.Current;
        var items = new List<ConnectorScopeItem> { new(null, current is null) };
        items.AddRange(_scope.Connectors.Select(name => new ConnectorScopeItem(name, string.Equals(name, current, StringComparison.Ordinal))));

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        IsVisible = _scope.CanScope;
        IsScoped = current is not null;
        Label = current ?? AllLabel;
        OnPropertyChanged(nameof(Description));
    }
}
