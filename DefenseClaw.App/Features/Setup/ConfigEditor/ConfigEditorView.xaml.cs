using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels.ConfigEditor;
using ICSharpCode.AvalonEdit.Search;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// The config.yaml editor as a control (CUST-385): the setting search, where the sections come from and Reload, the section list beside the typed form
/// (or RAW), and Revert / the reviewed Save. The Setup panel shows it inline and <see cref="ConfigEditorWindow"/> shows it detached; both hand it the
/// app's one <see cref="ConfigEditorSession"/>, so the two are the same draft.
/// <para>
/// <b>What this control does that the view-model cannot.</b> AvalonEdit's <c>TextEditor.Text</c> is a plain CLR property, so RAW is kept in step with
/// <see cref="ConfigEditorWindowViewModel.RawText"/> here, in both directions, without an update loop - one editor per view, over the same text. A FORM box
/// commits on <c>LostFocus</c>, so <see cref="CommitPendingFormEdit"/> moves focus off and back when something needs the value now (a save, a close, an exit).
/// And when RAW changed in the other view, a FORM on screen here is rebuilt once the typing pauses, so the draft reads the same in both.
/// </para>
/// </summary>
public partial class ConfigEditorView : UserControl
{
    /// <summary>How long RAW typing in the other view has to pause before a FORM on screen here is rebuilt from it.</summary>
    private static readonly TimeSpan FormCatchUpDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The height the tabs get inline (the Setup page scrolls, so the editor needs a height of its own; the Mac gives its 520).</summary>
    private const double InlineBodyHeight = 560;

    /// <summary>The draft this view shows. Setting it binds the view to the session's view-model and, while the view is on screen, attaches it.</summary>
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session),
        typeof(ConfigEditorSession),
        typeof(ConfigEditorView),
        new PropertyMetadata(null, OnSessionChanged));

    /// <summary>True for the Setup panel's copy: the tabs get a height of their own and "Open in window" is offered.</summary>
    public static readonly DependencyProperty IsInlineProperty = DependencyProperty.Register(
        nameof(IsInline),
        typeof(bool),
        typeof(ConfigEditorView),
        new PropertyMetadata(false, OnIsInlineChanged));

    /// <summary>The sentence under the form beside Save: what this copy of the draft is.</summary>
    public static readonly DependencyProperty FooterNoteProperty = DependencyProperty.Register(
        nameof(FooterNote),
        typeof(string),
        typeof(ConfigEditorView),
        new PropertyMetadata(string.Empty, OnFooterNoteChanged));

    /// <summary>The look controls, whose changes re-colour the YAML; null when the app was built without them (a test host).</summary>
    private readonly AppearanceService? _appearance = AppearanceService.Current;

    private readonly DispatcherTimer _formCatchUp;

    private ConfigEditorWindowViewModel? _viewModel;
    private bool _syncingEditorText;
    private bool _attached;

    public ConfigEditorView()
    {
        InitializeComponent();

        RawEditor.ShowLineNumbers = true;
        RawEditor.Options.EnableHyperlinks = false;
        RawEditor.Options.EnableEmailHyperlinks = false;
        RawEditor.Options.ShowTabs = false;
        SearchPanel.Install(RawEditor);
        RawEditor.TextChanged += OnEditorTextChanged;
        ApplyHighlighting();

        _formCatchUp = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = FormCatchUpDelay };
        _formCatchUp.Tick += OnFormCatchUpTick;

        // The draft comes from the session only (Session), never from the host's data context: inline, that is the Setup panel's view-model.
        Root.DataContext = null;
        Root.DataContextChanged += (_, _) => Bind(Root.DataContext as ConfigEditorWindowViewModel);
        RootTabs.SelectionChanged += OnTabSelectionChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, _) => Session?.NoteUsed(this);
        GotKeyboardFocus += (_, _) => Session?.NoteUsed(this);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc cref="SessionProperty"/>
    public ConfigEditorSession? Session
    {
        get => (ConfigEditorSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    /// <inheritdoc cref="IsInlineProperty"/>
    public bool IsInline
    {
        get => (bool)GetValue(IsInlineProperty);
        set => SetValue(IsInlineProperty, value);
    }

    /// <inheritdoc cref="FooterNoteProperty"/>
    public string FooterNote
    {
        get => (string)GetValue(FooterNoteProperty);
        set => SetValue(FooterNoteProperty, value);
    }

    /// <summary>The view-model on screen: the session's draft, or null before a session is set.</summary>
    internal ConfigEditorWindowViewModel? ViewModel => _viewModel;

    private static void OnSessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ConfigEditorView)d;
        if (e.OldValue is ConfigEditorSession old && view._attached)
        {
            old.Detach(view);
            view._attached = false;
        }

        if (e.NewValue is ConfigEditorSession session)
        {
            view.Root.DataContext = session.Editor;
            view.AttachIfShown();
        }
        else
        {
            view.Root.DataContext = null;
        }
    }

    private static void OnIsInlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ConfigEditorView)d;
        var inline = (bool)e.NewValue;
        view.RootTabs.Height = inline ? InlineBodyHeight : double.NaN;
        view.OpenInWindowButton.Visibility = inline ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnFooterNoteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ConfigEditorView)d).FooterNoteText.Text = e.NewValue as string ?? string.Empty;

    /// <summary>
    /// Lets a FORM edit that is still inside its box reach the view-model. FORM boxes commit on <c>LostFocus</c>, and a title-bar click, a tray Exit or a
    /// save from the other view does not move keyboard focus, so without this the typed value would be invisible to the "any unsaved edits?" check. Focus
    /// is put back afterwards so typing can continue. Does nothing when focus is not in this view's form or search results.
    /// </summary>
    internal void CommitPendingFormEdit()
    {
        if (Keyboard.FocusedElement is UIElement focused && (IsInside(FormScroll, focused) || IsInside(SearchScroll, focused)))
        {
            _ = Keyboard.Focus(null);
            _ = focused.Focus();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_appearance is not null)
        {
            _appearance.Changed -= OnAppearanceChanged;
            _appearance.Changed += OnAppearanceChanged;
        }

        ApplyHighlighting();
        AttachIfShown();

        // The first view on screen reads the file, after it has painted (the Setup page's grid comes first): one read-only CLI call, nothing written.
        if (Session is { } session && !session.Editor.HasLoadStarted)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = session.EnsureLoadedAsync()));
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appearance is not null)
        {
            _appearance.Changed -= OnAppearanceChanged;
        }

        _formCatchUp.Stop();
        if (_attached && Session is { } session)
        {
            session.Detach(this);
        }

        _attached = false;
    }

    private void AttachIfShown()
    {
        if (_attached || !IsLoaded || Session is not { } session)
        {
            return;
        }

        session.Attach(this);
        _attached = true;
    }

    /// <summary>Follows a new view-model: RAW is filled from it, and its changes reach RAW and the FORM catch-up.</summary>
    private void Bind(ConfigEditorWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncEditorFromViewModel();
        }
    }

    /// <summary>The YAML palette that reads on the look on screen (see <see cref="YamlHighlighting"/>): the style's own hues, light or dark.</summary>
    private void ApplyHighlighting() =>
        RawEditor.SyntaxHighlighting = YamlHighlighting.ForCurrent();

    private void OnAppearanceChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(new Action(ApplyHighlighting));

    private void OnOpenInWindowClick(object sender, RoutedEventArgs e)
    {
        if (Session is { } session)
        {
            ConfigEditorWindow.Show(session.Services);
        }
    }

    /// <summary>Brings the picked section's card to the top of the form. Nothing else moves: every section stays in the form.</summary>
    private void OnSectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not { } section ||
            SectionItems.ItemContainerGenerator.ContainerFromItem(section) is not FrameworkElement container)
        {
            return;
        }

        try
        {
            var top = container.TransformToAncestor(FormContent).Transform(new Point(0, 0)).Y;
            FormScroll.ScrollToVerticalOffset(Math.Max(0, top));
        }
        catch (InvalidOperationException)
        {
            // Not laid out yet (the tab was just switched to): bringing it into view is the next best thing.
            container.BringIntoView();
        }
    }

    /// <summary>
    /// Ctrl+S runs Save when Save is enabled (the same command as the button, so the same CanExecute): it opens the pre-save review, and pressed again
    /// there saves. Ctrl+F goes to the setting search, except inside RAW, where AvalonEdit's own search panel answers it. Esc leaves the review (back to
    /// editing), the review of a change made on disk, or the on-disk preview, and otherwise clears the setting search; it never closes anything that
    /// could throw away edits.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (vm.IsReviewing)
            {
                vm.CancelReviewCommand.Execute(null);
                e.Handled = true;
            }
            else if (vm.IsReviewingExternalChange)
            {
                vm.CancelExternalReviewCommand.Execute(null);
                e.Handled = true;
            }
            else if (vm.ShowOnDiskPreview)
            {
                vm.DismissOnDiskPreviewCommand.Execute(null);
                e.Handled = true;
            }
            else if (vm.HasFieldSearch && SearchBox.IsKeyboardFocusWithin)
            {
                vm.ClearFieldSearchCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveFromKeyboard(vm);
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !RawEditor.IsKeyboardFocusWithin)
        {
            RootTabs.SelectedItem = FormTabItem;
            _ = SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// FORM boxes commit on <c>LostFocus</c>, so a value typed into the focused box has not reached the view-model when Ctrl+S is pressed - and a Save
    /// would then miss it. <see cref="ConfigEditorWindowViewModel.ReviewAndSaveCommand"/> asks every view to commit first (the session's
    /// <c>CommitPendingEdits</c>), which puts focus back where it was. RAW needs none of this: its text is pushed to the view-model on every change.
    /// </summary>
    private static void SaveFromKeyboard(ConfigEditorWindowViewModel vm)
    {
        // The review of a change made to the file behind the editor covers it: a save would go under it, so Ctrl+S waits for the choice made there.
        if (vm.IsReviewingExternalChange)
        {
            return;
        }

        if (vm.IsReviewing)
        {
            if (vm.ConfirmReviewedSaveCommand.CanExecute(null))
            {
                vm.ConfirmReviewedSaveCommand.Execute(null);
            }

            return;
        }

        if (vm.ReviewAndSaveCommand.CanExecute(null))
        {
            vm.ReviewAndSaveCommand.Execute(null);
        }
    }

    private static bool IsInside(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = node is Visual
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, RootTabs))
        {
            return;
        }

        if (ReferenceEquals(RootTabs.SelectedItem, FormTabItem))
        {
            _viewModel?.NotifyFormTabSelected();
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditorText || _viewModel is null)
        {
            return;
        }

        _viewModel.RawText = RawEditor.Text;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ConfigEditorWindowViewModel.RawText):
                SyncEditorFromViewModel();
                break;

            case nameof(ConfigEditorWindowViewModel.IsFormEditable):
                // RAW changed somewhere (this view's RAW, or the other view's): a FORM on screen here catches up once the typing pauses.
                if (_viewModel is { IsFormEditable: false })
                {
                    _formCatchUp.Stop();
                    _formCatchUp.Start();
                }

                break;
        }
    }

    /// <summary>
    /// The other view's RAW changed the text while this one shows FORM: rebuild FORM from it, exactly as switching to the FORM tab would (a RAW that does
    /// not parse leaves FORM stale and read-only, and says so). Not while the operator is typing in this view's own RAW, which is the tab-switch's job.
    /// </summary>
    private void OnFormCatchUpTick(object? sender, EventArgs e)
    {
        _formCatchUp.Stop();
        if (_viewModel is { IsFormEditable: false } vm &&
            IsVisible &&
            ReferenceEquals(RootTabs.SelectedItem, FormTabItem) &&
            !RawEditor.IsKeyboardFocusWithin)
        {
            vm.NotifyFormTabSelected();
        }
    }

    private void SyncEditorFromViewModel()
    {
        if (_viewModel is not { } vm || RawEditor.Text == vm.RawText)
        {
            return;
        }

        _syncingEditorText = true;
        try
        {
            var caret = Math.Min(RawEditor.CaretOffset, vm.RawText.Length);
            RawEditor.Text = vm.RawText;
            RawEditor.CaretOffset = caret;
        }
        finally
        {
            _syncingEditorText = false;
        }
    }
}
