using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.Core.Policy.Model;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Policies panel on a runtime that has the policy model (CUST-293), paired with <see cref="PolicyModelViewModel"/>. It is
/// hosted by <see cref="PoliciesPanel"/>, which handles Ctrl+F and Esc for it (<see cref="FocusFilter"/>, and the view-model's
/// <c>HandleEscape</c>).
/// <para>
/// <b>One grid, six column sets.</b> The model gives each view's columns (<see cref="PolicyColumn"/>: header, how the cell is drawn,
/// share of the width) and the grid is built from them whenever they change, so the view never repeats what the model says a view is. A
/// grid whose columns are replaced cannot use <see cref="DcGridColumns"/>, which remembers the columns it saw first; the widths are
/// resolved here with the same rule (<c>ColumnSizing</c>: star shares with a floor per column) whenever the room changes.
/// </para>
/// </summary>
public sealed partial class PolicyModelView : UserControl
{
    private PolicyModelViewModel? _model;
    private bool _listening;
    private IReadOnlyList<PolicyColumn> _columns = Array.Empty<PolicyColumn>();

    public PolicyModelView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            Listen();
            BuildColumns();
        };

        // The view-model outlives the view (the panel keeps it while the runtime has the model): do not leave it holding a view that is gone.
        Unloaded += (_, _) => StopListening();
        RowList.SizeChanged += (_, _) => FitColumns();
        RowList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    /// <summary>Moves keyboard focus to the filter box and selects its text.</summary>
    public void FocusFilter() => PageToolbar.FocusSearch();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        StopListening();
        _model = e.NewValue as PolicyModelViewModel;
        if (IsLoaded)
        {
            Listen();
        }

        BuildColumns();
    }

    private void Listen()
    {
        if (_model is not null && !_listening)
        {
            _model.PropertyChanged += OnModelChanged;
            _listening = true;
        }
    }

    private void StopListening()
    {
        if (_model is not null && _listening)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _listening = false;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PolicyModelViewModel.Columns))
        {
            BuildColumns();
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0)
        {
            FitColumns();
        }
    }

    // ---- columns -------------------------------------------------------------------------------------------------------------

    private void BuildColumns()
    {
        var wanted = _model?.Columns ?? Array.Empty<PolicyColumn>();

        // The model hands over a new list every time it rebuilds the rows (a keystroke in the filter, a refresh): replace the columns only
        // when they differ, so the grid keeps its scroll position and its column state.
        if (wanted.SequenceEqual(_columns) && RowList.Columns.Count == wanted.Count)
        {
            FitColumns();
            return;
        }

        _columns = wanted.ToArray();
        RowList.Columns.Clear();
        for (var i = 0; i < _columns.Count; i++)
        {
            RowList.Columns.Add(Create(_columns[i], i));
        }

        FitColumns();
    }

    private DataGridColumn Create(PolicyColumn column, int index)
    {
        var cell = $"Cells[{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
        switch (column.Kind)
        {
            case PolicyCellKind.Status:
                return TemplateColumn(column, () =>
                {
                    var factory = new FrameworkElementFactory(typeof(DcStatusLabel));
                    factory.SetBinding(DcStatusLabel.TextProperty, OneWay($"{cell}.Shown"));
                    factory.SetBinding(DcStatusLabel.ToneProperty, OneWay($"{cell}.Tone"));
                    return factory;
                });

            case PolicyCellKind.Pill:
                return TemplateColumn(column, () =>
                {
                    var factory = new FrameworkElementFactory(typeof(DcStatePill));
                    factory.SetBinding(DcStatePill.TextProperty, OneWay($"{cell}.Shown"));
                    factory.SetBinding(DcStatePill.ToneProperty, OneWay($"{cell}.Tone"));
                    factory.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
                    return factory;
                });

            case PolicyCellKind.Severity:
                return TemplateColumn(column, () =>
                {
                    var factory = new FrameworkElementFactory(typeof(DcSeverityBadge));
                    factory.SetBinding(DcSeverityBadge.TextProperty, OneWay($"{cell}.Shown"));
                    factory.SetBinding(DcSeverityBadge.ToneProperty, OneWay($"{cell}.Tone"));
                    factory.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
                    return factory;
                });

            default:
                return new DataGridTextColumn
                {
                    Header = column.Header,
                    Binding = OneWay($"{cell}.Shown"),
                    ElementStyle = (Style)FindResource(column.Kind == PolicyCellKind.Mono ? "DcCellTextMono" : "DcCellText"),
                    CanUserSort = false,
                };
        }
    }

    private static DataGridTemplateColumn TemplateColumn(PolicyColumn column, Func<FrameworkElementFactory> visual) =>
        new()
        {
            Header = column.Header,
            CanUserSort = false,
            CellTemplate = new DataTemplate { VisualTree = visual() },
        };

    private static Binding OneWay(string path) => new(path) { Mode = BindingMode.OneWay };

    /// <summary>
    /// The narrowest a column may be: what its longest cell (or its header) needs at 12 px, so a level such as <c>MEDIUM+</c> or a mode such as
    /// <c>observe (own)</c> is never cut. A column that holds prose (the model gives it a large share) has a floor of its own and takes the rest of
    /// the room, its cells trimmed with an ellipsis and the whole text a tooltip away.
    /// </summary>
    internal static double MinWidthOf(PolicyColumn column, int index, IReadOnlyList<PolicyTableRow> rows)
    {
        var chars = column.Header.Length + 1;
        foreach (var row in rows)
        {
            if (index < row.Cells.Count)
            {
                chars = Math.Max(chars, row.Cells[index].Shown.Length);
            }
        }

        var estimate = column.Kind switch
        {
            PolicyCellKind.Status => (chars * 6.6) + 40,
            PolicyCellKind.Pill => (chars * 7.2) + 40,
            PolicyCellKind.Severity => Math.Max(92, (chars * 8.5) + 36),
            PolicyCellKind.Mono => (chars * 7.3) + 24,
            _ => (chars * 6.6) + 24,
        };

        return Math.Clamp(estimate, 56, column.Weight >= 2 ? 150 : 260);
    }

    /// <summary>Resolves the star shares to plain widths against the room the columns have now.</summary>
    private void FitColumns()
    {
        if (RowList.Columns.Count == 0 || RowList.Columns.Count != _columns.Count)
        {
            return;
        }

        var scroll = FindScrollViewer(RowList);
        var room = scroll is { ViewportWidth: > 0 } ? scroll.ViewportWidth : RowList.ActualWidth;
        if (room <= 0)
        {
            return;
        }

        // One DIP short of the room, so rounding cannot leave a one-pixel sideways scroll bar.
        var available = Math.Floor(room) - 1;
        var rows = _model?.AllRows ?? Array.Empty<PolicyTableRow>();
        var widths = ColumnSizing.Distribute(
            available,
            _columns.Select(c => c.Weight).ToArray(),
            _columns.Select((c, i) => MinWidthOf(c, i, rows)).ToArray());
        for (var i = 0; i < widths.Length; i++)
        {
            var current = RowList.Columns[i].Width;
            if (!current.IsAbsolute || Math.Abs(current.Value - widths[i]) > 0.5)
            {
                RowList.Columns[i].Width = new DataGridLength(widths[i]);
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer self)
        {
            return self;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
