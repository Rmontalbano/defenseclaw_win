using System.Windows;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Cowork;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The curated Claude Cowork wizard (runtime 1.0.8, CUST-445). It replaces the generic card built from <c>setup cowork --help</c>, which reported
/// "completed" on exit 0 even though Cowork is protected only after the plugin is uploaded in Claude Desktop by hand.
/// </summary>
public static class CoworkWizard
{
    /// <summary>The <c>setup</c> noun this wizard takes over.</summary>
    public const string Target = "cowork";

    /// <summary>True when <paramref name="target"/> is <c>cowork</c> and the runtime has Cowork protection: open this wizard instead of the generic one.</summary>
    public static bool Handles(AppServices services, string? target)
    {
        ArgumentNullException.ThrowIfNull(services);
        return string.Equals(target, Target, StringComparison.OrdinalIgnoreCase) &&
               services.Runtime.Capabilities.Has(RuntimeCapability.CoworkProtection);
    }

    /// <summary>
    /// Shows the wizard modally over <paramref name="owner"/> (the main window when null); returns once it closes. The window reads the Cowork state
    /// itself when it loads.
    /// </summary>
    public static Task ShowAsync(AppServices services, Window? owner = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var viewModel = new CoworkSetupViewModel(services, new DiscoverActionReview(services));
        var window = new CoworkSetupWindow(viewModel)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };

        window.ShowDialog();
        return Task.CompletedTask;
    }
}
