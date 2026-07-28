using System.Windows;
using DefenseClaw.App.ViewModels.Wizards;
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
    public static async Task ShowAsync(
        AppServices services,
        string target,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(target);

        var catalog = WizardCatalog.Shared(services);
        var definition = await catalog.EnsureDetailAsync(target, cancellationToken).ConfigureAwait(true);

        var window = new WizardWindow(new WizardViewModel(services, definition))
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };

        window.ShowDialog();
    }
}
