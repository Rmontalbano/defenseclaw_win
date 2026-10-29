using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// The one connector control every page toolbar shares (its <c>ConnectorSlot</c>): a chip that says "All connectors" or the connector
/// everything is narrowed to, and a click opens a menu of All + the connectors (the Mac's toolbar chip). It shows only while there is
/// more than one connector, because with one there is nothing to choose between.
/// <para>
/// <b>Use.</b> <c>&lt;ctl:DcConnectorScopeChip Model="{Binding ConnectorChip}" /&gt;</c> (every panel view-model has <c>ConnectorChip</c>).
/// The model wraps the one shared <c>ConnectorScope</c>, so every chip on every page, Ctrl+Shift+M and the palette entry agree.
/// </para>
/// </summary>
public sealed class DcConnectorScopeChip : Wpf.Ui.Controls.Button
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model),
        typeof(ConnectorScopeViewModel),
        typeof(DcConnectorScopeChip),
        new PropertyMetadata(null, OnModelChanged));

    private ConnectorScopeViewModel? _watched;

    public DcConnectorScopeChip()
    {
        SetResourceReference(StyleProperty, typeof(Wpf.Ui.Controls.Button));
        Padding = new Thickness(10, 4, 10, 4);
        Visibility = Visibility.Collapsed;
        Unloaded += (_, _) => Watch(null);
        Loaded += (_, _) => Watch(Model);
    }

    /// <summary>The shared scope as a label and a menu (a panel's <c>ConnectorChip</c>).</summary>
    public ConnectorScopeViewModel? Model
    {
        get => (ConnectorScopeViewModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>The menu a click opens (All first, then the roster; the current scope checked).</summary>
    internal ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom };
        if (Model is null)
        {
            return menu;
        }

        foreach (var item in Model.Items)
        {
            var captured = item;
            var entry = new MenuItem { Header = item.Label, IsCheckable = true, IsChecked = item.IsSelected };
            AutomationProperties.SetName(entry, item.IsSelected ? $"{item.Label}, current scope" : item.Label);
            entry.Click += (_, _) => Model.SelectCommand.Execute(captured);
            _ = menu.Items.Add(entry);
        }

        return menu;
    }

    protected override void OnClick()
    {
        base.OnClick();
        BuildMenu().IsOpen = true;
    }

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chip = (DcConnectorScopeChip)d;
        chip.Watch((ConnectorScopeViewModel?)e.NewValue);
    }

    private void Watch(ConnectorScopeViewModel? model)
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnModelPropertyChanged;
        }

        _watched = model;
        if (model is not null)
        {
            model.PropertyChanged += OnModelPropertyChanged;
        }

        Refresh();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var model = _watched;
        Visibility = model is { IsVisible: true } ? Visibility.Visible : Visibility.Collapsed;
        if (model is null)
        {
            return;
        }

        Content = model.Label;
        Appearance = model.IsScoped ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        ToolTip = model.Description;
        AutomationProperties.SetName(this, "Connector scope: " + model.Label);
        AutomationProperties.SetHelpText(this, model.Description);
        AutomationProperties.SetAcceleratorKey(this, "Ctrl+Shift+M");
    }
}
