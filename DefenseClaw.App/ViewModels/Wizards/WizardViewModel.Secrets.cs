using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// The in-app secret route of a wizard: typed in a password box, held only as a <c>SecureString</c> by the field
/// view-model, handed to the run as <see cref="CliRunOptions.EnvironmentOverlay"/> and dropped when the run ends.
/// <para>
/// <b>Where a secret can and cannot be.</b> It is never in <see cref="WizardValues"/>, in
/// <see cref="Definition"/>'s argv (<see cref="Services.Wizards.WizardDefinition.BuildArgv"/> emits nothing for a
/// secret field), in <see cref="CommandText"/>, or in any property of this class — only the <i>names</i> of the
/// variables (<see cref="EnvironmentNotes"/>) are ever shown. It is supplied only to the real command, not to a
/// <c>--dry-run</c> preview, so the value goes to exactly one child process.
/// </para>
/// <para>
/// Kept in its own file so the credential region does not share lines with the review page.
/// </para>
/// </summary>
public sealed partial class WizardViewModel
{
    /// <summary>
    /// One sentence per secret this app will supply in the environment, for the review page:
    /// <c>SPLUNK_ACCESS_TOKEN=••• — …, supplied to the command as an environment variable (value masked …)</c>.
    /// Names only, and only for a visible secret the operator has actually typed. Empty when nothing is supplied.
    /// </summary>
    public ObservableCollection<string> EnvironmentNotes { get; } = new();

    /// <summary>True while <see cref="EnvironmentNotes"/> has an entry.</summary>
    [ObservableProperty]
    private bool _hasEnvironmentNotes;

    /// <summary>What the result line says once a run that was given a secret has ended.</summary>
    internal const string SecretClearedSentence = "The value you entered was supplied to that one command only and has been cleared; enter it again to retry.";

    /// <summary>
    /// The visible secret fields that will actually be supplied: an in-app route, and a usable typed value.
    /// The one place that decides, so the review notes and the run cannot disagree.
    /// </summary>
    private IEnumerable<WizardFieldViewModel> SuppliedSecretFields() =>
        _fields.Where(f => f.IsVisible && f.OffersInAppEntry && f.HasEntry);

    /// <summary>Rebuilds <see cref="EnvironmentNotes"/> from the fields as they stand.</summary>
    private void RefreshEnvironmentNotes()
    {
        EnvironmentNotes.Clear();
        foreach (var field in SuppliedSecretFields())
        {
            EnvironmentNotes.Add(
                $"{field.InAppEnvName}=••• — {field.CredentialPurpose}, supplied to the command as an environment variable " +
                "(value masked; this run only, not on the command line).");
        }

        HasEnvironmentNotes = EnvironmentNotes.Count > 0;
    }

    private void OnSecretEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WizardFieldViewModel.HasEntry) or nameof(WizardFieldViewModel.InAppEnvName)
            or nameof(WizardFieldViewModel.IsVisible))
        {
            RefreshEnvironmentNotes();
        }
    }

    /// <summary>
    /// The options for the real run: the app's defaults plus one environment variable per supplied secret, or
    /// <c>null</c> when nothing is supplied (an ordinary run, exactly as before). Reads each value out of its
    /// <c>SecureString</c> here and nowhere else.
    /// </summary>
    internal CliRunOptions? BuildRunOptions()
    {
        CliRunOptions? options = null;
        foreach (var field in SuppliedSecretFields())
        {
            if (field.MaterializeSecret() is { IsEmpty: false } secret)
            {
                options = (options ?? CliRunOptions.Default).WithEnvironment(field.InAppEnvName, secret);
            }
        }

        return options;
    }

    /// <summary>True when a run started now would carry at least one environment variable.</summary>
    private bool HasSuppliedSecrets => SuppliedSecretFields().Any();

    /// <summary>Drops every typed secret, whichever page it was typed on. Safe to call at any time.</summary>
    private void ClearSecretEntries(string notice = "")
    {
        foreach (var field in _fields)
        {
            if (field.IsSecret)
            {
                field.ClearEntry(notice);
            }
        }
    }
}
