using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The Appearance flyout's content: a Style group and a Mode group of radio buttons over an
/// <see cref="IAppearanceControl"/>. Selecting a radio applies it immediately; the radios follow the service, so the
/// keyboard shortcut or a palette command changing the look while this is open is reflected.
/// </summary>
public sealed partial class AppearanceFlyout : UserControl
{
    private readonly Dictionary<AppearanceStyle, RadioButton> _styleButtons = new();
    private readonly Dictionary<AppearanceMode, RadioButton> _modeButtons = new();
    private IAppearanceControl? _appearance;
    private bool _syncing;

    public AppearanceFlyout()
    {
        InitializeComponent();
        HintText.Text = $"Applies immediately and is remembered. {ShellShortcuts.ToggleThemeText} switches between light and dark.";
    }

    /// <summary>Raised when the operator presses Esc inside the flyout.</summary>
    public event EventHandler? CloseRequested;

    internal void Bind(IAppearanceControl appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        if (ReferenceEquals(_appearance, appearance))
        {
            return;
        }

        if (_appearance is not null)
        {
            _appearance.Changed -= OnAppearanceChanged;
        }

        _appearance = appearance;
        _appearance.Changed += OnAppearanceChanged;

        Build();
        Refresh();
    }

    /// <summary>Puts the radios and swatches in step with the service. Called when the flyout opens.</summary>
    internal void Refresh()
    {
        if (_appearance is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            foreach (var (style, button) in _styleButtons)
            {
                button.IsChecked = style == _appearance.Style;
                if (button.Tag is Border swatch)
                {
                    PaintSwatch(swatch, style);
                }
            }

            foreach (var (mode, button) in _modeButtons)
            {
                button.IsChecked = mode == _appearance.Mode;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Moves keyboard focus to the chosen style (the first stop when the flyout opens from the keyboard).</summary>
    internal void FocusSelected()
    {
        if (_appearance is not null && _styleButtons.TryGetValue(_appearance.Style, out var button))
        {
            _ = button.Focus();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void Build()
    {
        StyleGroup.Children.Clear();
        ModeGroup.Children.Clear();
        _styleButtons.Clear();
        _modeButtons.Clear();

        foreach (var style in AppearanceCatalog.Styles)
        {
            var swatch = new Border
            {
                Width = 44,
                Height = 28,
                Margin = new Thickness(0, 0, 10, 0),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = BuildSwatchGlyph(),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, AppearanceTokens.Border);
            swatch.SetResourceReference(Border.CornerRadiusProperty, AppearanceTokens.RadiusS);

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = AppearanceCatalog.Name(style), FontWeight = FontWeights.SemiBold });
            var description = new TextBlock { Text = AppearanceCatalog.Description(style), TextWrapping = TextWrapping.Wrap };
            description.SetResourceReference(StyleProperty, "DcCaption");
            label.Children.Add(description);

            // A grid, not a horizontal stack: the description has to wrap inside the flyout's width rather than run off it.
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(label, 1);
            row.Children.Add(swatch);
            row.Children.Add(label);

            var button = NewRadio(StyleGroupName, row, $"{AppearanceCatalog.Name(style)} style", AppearanceCatalog.Description(style));
            button.Tag = swatch;
            var captured = style;
            button.Checked += (_, _) =>
            {
                if (!_syncing)
                {
                    _appearance?.SetStyle(captured);
                }
            };

            _styleButtons[style] = button;
            StyleGroup.Children.Add(button);
        }

        foreach (var mode in AppearanceCatalog.Modes)
        {
            var name = new TextBlock { Text = AppearanceCatalog.Name(mode), FontWeight = FontWeights.SemiBold, Width = 56, VerticalAlignment = VerticalAlignment.Center };
            var description = new TextBlock { Text = AppearanceCatalog.Description(mode), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            description.SetResourceReference(StyleProperty, "DcCaption");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(description, 1);
            row.Children.Add(name);
            row.Children.Add(description);

            var button = NewRadio(ModeGroupName, row, $"{AppearanceCatalog.Name(mode)} mode", AppearanceCatalog.Description(mode));
            var captured = mode;
            button.Checked += (_, _) =>
            {
                if (!_syncing)
                {
                    _appearance?.SetMode(captured);
                }
            };

            _modeButtons[mode] = button;
            ModeGroup.Children.Add(button);
        }
    }

    // Per instance: WPF scopes a GroupName to the visual root, and elements with no root yet (a flyout before it opens) all share
    // one scope, so two flyouts with the same names would uncheck each other's radios.
    private readonly string _groupSuffix = Guid.NewGuid().ToString("N");

    /// <summary>The style radios' group: "AppearanceStyle" plus this flyout's own suffix.</summary>
    internal string StyleGroupName => "AppearanceStyle-" + _groupSuffix;

    /// <summary>The mode radios' group: "AppearanceMode" plus this flyout's own suffix.</summary>
    internal string ModeGroupName => "AppearanceMode-" + _groupSuffix;

    private static RadioButton NewRadio(string group, object content, string name, string help)
    {
        var button = new RadioButton
        {
            GroupName = group,
            Content = content,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, help);
        return button;
    }

    /// <summary>A tiny page: a card on the window, an accent stripe, two lines of text.</summary>
    private static UIElement BuildSwatchGlyph()
    {
        var grid = new Grid { Margin = new Thickness(4) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var accent = new Border { Name = "Accent", Margin = new Thickness(0, 1, 3, 1) };
        Grid.SetRowSpan(accent, 2);
        var line1 = new Border { Name = "Line1", Height = 3, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var line2 = new Border { Name = "Line2", Height = 3, Width = 12, Margin = new Thickness(0, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(line1, 1);
        Grid.SetColumn(line2, 1);
        Grid.SetRow(line2, 1);
        grid.Children.Add(accent);
        grid.Children.Add(line1);
        grid.Children.Add(line2);
        return grid;
    }

    private void PaintSwatch(Border swatch, AppearanceStyle style)
    {
        if (_appearance is null)
        {
            return;
        }

        var (window, surface, accent, text) = _appearance.Swatch(style);
        swatch.Background = Frozen(window);

        var glyph = (Grid)swatch.Child;
        glyph.Background = Frozen(surface);
        foreach (var child in glyph.Children.OfType<Border>())
        {
            child.Background = Frozen(child.Name == "Accent" ? accent : text);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(new Action(Refresh));
}
