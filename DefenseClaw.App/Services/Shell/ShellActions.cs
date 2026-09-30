using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace DefenseClaw.App.Services;

/// <summary>
/// The app-level actions the shell can trigger from the keyboard or the command palette, each
/// routed through the path the rest of the app already uses: the tray service for gateway control
/// and autostart (so the review step, the Activity record and the toasts are identical), the
/// windows' own <c>Show</c> entry points for the config editor and the update check, and the
/// panel's own <c>RefreshCommand</c> for F5.
/// <para>
/// Nothing here runs arbitrary text. The palette is a fixed list of these named actions plus
/// navigation; there is no free-form command entry.
/// </para>
/// </summary>
internal sealed class ShellActions
{
    private readonly AppServices _services;
    private readonly PanelCatalog _catalog;
    private readonly TrayIconService _tray;
    private readonly Func<Window?> _owner;

    /// <param name="owner">The dashboard window, as the owner of review dialogs (null-safe).</param>
    public ShellActions(AppServices services, PanelCatalog catalog, TrayIconService tray, Func<Window?> owner)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>The gateway snapshot the availability rules are evaluated against.</summary>
    public GatewaySnapshot Snapshot => _services.Monitor.Current;

    /// <summary>
    /// The current panel's <c>RefreshCommand</c> (by convention every panel view-model exposes one),
    /// or null when the panel on screen has none. Found by name so a panel needs no shell-specific
    /// interface: GA's F5 binds to whatever the panel already calls its refresh.
    /// </summary>
    private ICommand? CurrentRefreshCommand =>
        _catalog.ActiveViewModel is { } viewModel
            ? viewModel.GetType().GetProperty("RefreshCommand", BindingFlags.Instance | BindingFlags.Public)?.GetValue(viewModel) as ICommand
            : null;

    /// <summary>True when F5 / "Refresh current panel" would do something right now.</summary>
    public bool CanRefreshCurrentPanel => CurrentRefreshCommand is { } command && command.CanExecute(null);

    /// <summary>Runs the current panel's refresh. False when there is none or it is already running.</summary>
    public bool TryRefreshCurrentPanel()
    {
        if (CurrentRefreshCommand is not { } command || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    /// <summary>
    /// Shows <paramref name="panelId"/>, telling it <paramref name="payload"/> if there is one: the deep link behind the
    /// status strip's chips and the palette's "open Alerts on the critical ones". The window is brought up if it is in the
    /// tray. See <see cref="ShellNavigation"/>.
    /// </summary>
    public void OpenPanel(string panelId, object? payload = null) => _services.Navigation.Request(panelId, payload);

    /// <summary>One gateway poll now, the same as the status strip's Refresh button.</summary>
    public void RefreshGatewayStatus() => _ = RefreshGatewayStatusAsync();

    private async Task RefreshGatewayStatusAsync()
    {
        try
        {
            _ = await _services.Monitor.RefreshAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed poll is reported by the strip itself; this is fire-and-forget.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Gateway refresh from the shell failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    public void OpenConfigEditor() => Views.ConfigEditor.ConfigEditorWindow.Show(_services);

    /// <summary>Opens the Updates window, which runs the release check as it opens.</summary>
    public void CheckForUpdates() => _ = Views.Updates.UpdatesWindow.Show(_services);

    /// <summary>Start / stop / restart through the tray's path: review, run via the CLI runner, toast.</summary>
    public Task RunGatewayActionAsync(GatewayAction action) => _tray.RunGatewayActionAsync(action, _owner());

    public bool IsAutostartEnabled => AutostartManager.IsEnabled;

    public void ToggleAutostart() => _ = _tray.ToggleAutostart();
}
