using System.Windows;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.SetupResources;
using DefenseClaw.App.Views.Wizards;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Opens a wizard window for one setup target.
/// <para>
/// The launcher exists so the hub's view-model never touches a <see cref="Window"/>: it asks
/// for a target by name, the launcher makes sure that target's <c>--help</c> has been parsed
/// (a group also pulls its subcommands here, which is why this is async), and only then
/// builds the pages. A wizard opened before its detail landed would show generated fields
/// that do not match the installed CLI, which is exactly the drift this whole catalog exists
/// to avoid.
/// </para>
/// </summary>
public static class WizardLauncher
{
    /// <summary>
    /// Shows the wizard modally over <paramref name="owner"/>. Returns once the window closes.
    /// </summary>
    /// <param name="preset">Where a group wizard opens (the Setup editors' Add: <c>add</c>, with a directory); null opens it on its first page.</param>
    public static async Task ShowAsync(
        AppServices services,
        string target,
        Window? owner = null,
        CancellationToken cancellationToken = default,
        WizardPreset? preset = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(target);

        var catalog = WizardCatalog.Shared(services);
        var definition = await catalog.EnsureDetailAsync(target, cancellationToken).ConfigureAwait(true);

        var viewModel = new WizardViewModel(services, definition);
        _ = preset?.ApplyTo(viewModel);

        var window = new WizardWindow(viewModel)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };

        // A connector setup that stopped on a folder that is not trusted offers to open the trusted-folder editor over this window.
        viewModel.OpenTrustedPaths = (directory, context) => SetupResourceWindow.OpenTrustedPaths(services, window, directory, context);

        window.ShowDialog();
    }
}
