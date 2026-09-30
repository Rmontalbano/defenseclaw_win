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
                "(value masked; this run only, not on the command line)." +
                (field.PersistSentence.Length > 0 ? " " + field.PersistSentence : string.Empty));
        }

        HasEnvironmentNotes = EnvironmentNotes.Count > 0;
    }

    private void OnSecretEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WizardFieldViewModel.HasEntry) or nameof(WizardFieldViewModel.InAppEnvName)
            or nameof(WizardFieldViewModel.IsVisible))
        {
            // A secret whose CLI keeps a supplied value only when told to (galileo): typing one is the moment the
            // operator's intent is clear, so the choice is switched on for them — unless they have made it themselves.
            if (e.PropertyName == nameof(WizardFieldViewModel.HasEntry) && sender is WizardFieldViewModel { HasEntry: true } typed)
            {
                SwitchOnPersistChoiceFor(typed);
            }

            SyncPersistState();
            RefreshEnvironmentNotes();
        }
        else if (e.PropertyName == nameof(WizardFieldViewModel.Value) && sender is WizardFieldViewModel changed && IsPersistChoice(changed))
        {
            // Any change that did not come from SwitchOnPersistChoiceFor is the operator's own decision.
            _persistChosenByOperator |= !_settingPersistFromEntry;
            SyncPersistState();
            RefreshEnvironmentNotes();
        }
    }

    // ------------------------------------------------------------------ "keep the value" choice (galileo)

    /// <summary>
    /// True once the operator has set the "keep the value" switch themselves (a route's
    /// <see cref="SecretRoute.PersistFlag"/>). From then on typing a value no longer changes it.
    /// </summary>
    private bool _persistChosenByOperator;

    /// <summary>True only while <see cref="SwitchOnPersistChoiceFor"/> is writing the switch, to tell it from the operator.</summary>
    private bool _settingPersistFromEntry;

    /// <summary>The switch a secret's route names as "keep the value", or null when its CLI needs no such choice.</summary>
    private WizardFieldViewModel? PersistChoiceOf(WizardFieldViewModel secret) =>
        secret.Field.Credential?.PersistFlag is { Length: > 0 } flag
            ? _fields.FirstOrDefault(f => string.Equals(f.Field.Flag, flag, StringComparison.Ordinal))
            : null;

    private bool IsPersistChoice(WizardFieldViewModel field) =>
        field.Field.Flag is { Length: > 0 } flag &&
        _fields.Any(s => s.IsSecret && string.Equals(s.Field.Credential?.PersistFlag, flag, StringComparison.Ordinal));

    private void SwitchOnPersistChoiceFor(WizardFieldViewModel secret)
    {
        if (_persistChosenByOperator || PersistChoiceOf(secret) is not { IsOn: false } choice)
        {
            return;
        }

        _settingPersistFromEntry = true;
        try
        {
            choice.IsOn = true;
        }
        finally
        {
            _settingPersistFromEntry = false;
        }
    }

    /// <summary>Tells each secret whether the switch that keeps its value is on, so its status line can say what will happen.</summary>
    private void SyncPersistState()
    {
        foreach (var secret in _fields)
        {
            if (secret.IsSecret && PersistChoiceOf(secret) is { } choice)
            {
                secret.SetPersistState(choice.IsOn);
            }
        }
    }

    /// <summary>
    /// The options for one run. A <c>--dry-run</c> preview never gets the typed value — it is for the real command
    /// alone, so it goes to exactly one child — which is the single place that is decided.
    /// </summary>
    internal CliRunOptions? RunOptionsFor(bool preview) => preview ? null : BuildRunOptions();

    /// <summary>What the supplied secrets' routes say a preview will do without them; leading space, or empty.</summary>
    private string PreviewSecretNotes()
    {
        var notes = SuppliedSecretFields()
            .Select(f => f.Field.Credential?.PreviewNote)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return notes.Length == 0 ? string.Empty : " " + string.Join(' ', notes);
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
