using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.ViewModels.SetupResources;
using DefenseClaw.Core.Setup;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.SetupResources;

/// <summary>
/// A Setup list editor in a window of its own (CUST-270): the Setup hub's observability, webhook and trusted-paths tiles open it, and so can a
/// connector setup that stopped on a folder that is not trusted (<see cref="OpenTrustedPaths"/>). One window per list, brought forward when it is
/// already open and read again each time it is opened, so it never shows an older list than the one just asked for. Add opens the setup wizard on
/// <c>add</c> over this window and reads the list again when it closes.
/// </summary>
public sealed partial class SetupResourceWindow : FluentWindow
{
    private static readonly Dictionary<SetupResource, SetupResourceWindow> Current = new();

    private readonly SetupResourceViewModel _viewModel;

    private SetupResourceWindow(AppServices services, SetupResource resource, string prefill, string context)
    {
        InitializeComponent();
        Icon = ShieldIconFactory.CreateWindowIcon();

        _viewModel = SetupResourceViewModel.Create(services, resource, prefill, context);
        _viewModel.OpenWizard = preset => WizardLauncher.ShowAsync(services, SetupResourceArgv.Noun(resource), this, preset: preset);
        Editor.DataContext = _viewModel;
        Title = _viewModel.Title;
        Bar.Title = _viewModel.Title;
        AutomationProperties.SetName(this, _viewModel.Title);

        // Backdrop, title bar and colours follow the app's look like the dashboard's do.
        AppearanceService.Current?.Attach(this);

        Loaded += (_, _) => _ = _viewModel.RefreshCommand.ExecuteAsync(null);
        Closed += (_, _) =>
        {
            _viewModel.Dispose();
            if (Current.TryGetValue(resource, out var open) && ReferenceEquals(open, this))
            {
                _ = Current.Remove(resource);
            }
        };

        // Esc closes the window, unless it is there to dismiss the review, a notice or the details (the view handles those first).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !Editor.HandlesEscape && !e.Handled)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>The window of <paramref name="resource"/> on screen, if any.</summary>
    internal static SetupResourceWindow? For(SetupResource resource) => Current.GetValueOrDefault(resource);

    internal SetupResourceViewModel ViewModel => _viewModel;

    /// <summary>
    /// Opens the editor of <paramref name="resource"/>, or brings the open window forward and reads its list again. UI thread only.
    /// </summary>
    /// <param name="owner">The dashboard when it is showing; null or hidden centres the window on screen.</param>
    /// <param name="prefill">For the trusted-folder editor: a folder a failed setup named, which its banner offers to trust.</param>
    /// <param name="context">For the trusted-folder editor: why the operator is here, in a sentence.</param>
    public static SetupResourceWindow Open(AppServices services, SetupResource resource, Window? owner = null, string prefill = "", string context = "")
    {
        ArgumentNullException.ThrowIfNull(services);

        if (Current.TryGetValue(resource, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            if (existing._viewModel is TrustedPathsViewModel trusted && (prefill.Length > 0 || context.Length > 0))
            {
                trusted.SetContext(prefill, context);
            }

            _ = existing.Activate();
            _ = existing._viewModel.RefreshCommand.ExecuteAsync(null);
            return existing;
        }

        var window = new SetupResourceWindow(services, resource, prefill, context);
        Current[resource] = window;

        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Show();
        return window;
    }

    /// <summary>
    /// Opens the trusted-folder editor for a connector setup that stopped on a folder that is not trusted: the folder is offered on a banner, and
    /// Trust opens the setup wizard on <c>add</c> with it filled in (the TUI opens the same editor with the folder in its box).
    /// </summary>
    /// <param name="directory">The folder the setup named.</param>
    /// <param name="context">The sentence that says which setup stopped and why.</param>
    public static SetupResourceWindow OpenTrustedPaths(AppServices services, Window? owner, string directory, string context) =>
        Open(services, SetupResource.TrustedPaths, owner, directory, context);
}
