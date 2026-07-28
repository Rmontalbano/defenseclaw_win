using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels.ConfigEditor;
using ICSharpCode.AvalonEdit.Search;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// The config.yaml editor window: RAW (AvalonEdit) and FORM (generated sections) tabs
/// over the same <see cref="ConfigEditorWindowViewModel"/>.
/// <para>
/// <b>Entry point.</b> <see cref="Show"/> is the whole public surface this window
/// exposes to the rest of the app — the orchestrator wires it up from wherever the
/// Configure/Setup panel ends up launching it from. Calling it twice reuses the same
/// window (brought to front) instead of opening a second editor over the same file.
/// </para>
/// <para>
/// AvalonEdit's <c>TextEditor.Text</c> is a plain CLR property, not a dependency
/// property, so it cannot be bound from XAML — this code-behind is the standard,
/// AvalonEdit-recommended way to keep it in sync with the view-model's
/// <see cref="ConfigEditorWindowViewModel.RawText"/> in both directions without an
/// infinite update loop.
/// </para>
/// </summary>
public partial class ConfigEditorWindow : FluentWindow
{
    private static ConfigEditorWindow? _current;

    private readonly ConfigEditorWindowViewModel _viewModel;
    private bool _syncingEditorText;

    public ConfigEditorWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        _viewModel = new ConfigEditorWindowViewModel(services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        RawEditor.SyntaxHighlighting = YamlHighlighting.Instance;
        RawEditor.ShowLineNumbers = true;
        RawEditor.Options.EnableHyperlinks = false;
        RawEditor.Options.ShowTabs = false;
        SearchPanel.Install(RawEditor);
        RawEditor.TextChanged += OnEditorTextChanged;

        RootTabs.SelectionChanged += OnTabSelectionChanged;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
    }

    /// <summary>Opens the config editor, or brings the already-open one to the front.</summary>
    public static void Show(AppServices services)
    {
        if (_current is { } existing)
        {
            existing.ShowAndActivate();
            return;
        }

        _current = new ConfigEditorWindow(services);
        _current.Show();
    }

    private void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        base.Show();
        _ = Activate();
        _ = Focus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, RootTabs))
        {
            return;
        }

        if (RootTabs.SelectedItem is TabItem { Name: "FormTabItem" })
        {
            _viewModel.NotifyFormTabSelected();
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditorText)
        {
            return;
        }

        _viewModel.RawText = RawEditor.Text;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConfigEditorWindowViewModel.RawText))
        {
            return;
        }

        if (RawEditor.Text == _viewModel.RawText)
        {
            return;
        }

        _syncingEditorText = true;
        var caret = Math.Min(RawEditor.CaretOffset, _viewModel.RawText.Length);
        RawEditor.Text = _viewModel.RawText;
        RawEditor.CaretOffset = caret;
        _syncingEditorText = false;
    }
}
