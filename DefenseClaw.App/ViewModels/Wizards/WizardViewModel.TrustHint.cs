using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// "Trust this folder..." (CUST-270): a connector setup that stopped because its program is in a folder that is not trusted printed the folder it
/// wants trusted (<see cref="TrustHint"/>), and the result bar offers to open the trusted-folder editor with it filled in, as the TUI does when it
/// routes such a setup to its editor. Nothing is trusted from here: the editor's Add opens the add wizard, which shows its own command first.
/// </summary>
public sealed partial class WizardViewModel
{
    /// <summary>The folder the failed run on display asked the operator to trust; empty when the run did not ask for one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrustHint))]
    [NotifyCanExecuteChangedFor(nameof(TrustFolderCommand))]
    private string _trustHintDirectory = string.Empty;

    /// <summary>The sentence under the result that says why the button is there.</summary>
    [ObservableProperty]
    private string _trustHintMessage = string.Empty;

    /// <summary>True while the run on display stopped on a folder that is not trusted.</summary>
    public bool HasTrustHint => TrustHintDirectory.Length > 0;

    /// <summary>
    /// How the button opens the trusted-folder editor: the folder to offer, and the sentence that says which setup stopped and why. The launcher
    /// sets it (a view-model never opens a window); null leaves the button with nothing to open, and it says so.
    /// </summary>
    internal Action<string, string>? OpenTrustedPaths { get; set; }

    [RelayCommand(CanExecute = nameof(HasTrustHint))]
    private void TrustFolder()
    {
        if (!HasTrustHint)
        {
            return;
        }

        if (OpenTrustedPaths is not { } open)
        {
            ResultMessage = "The trusted binary locations cannot be opened from here. Open them from the Setup page, and trust " + DisplayNames.Visible(TrustHintDirectory) + ".";
            return;
        }

        open(TrustHintDirectory, $"Setup of {Title} stopped: its program is in {TrustHintDirectory}, a folder that is not trusted. Trust it below, then run the setup again.");
    }

    /// <summary>After a run that ended with <paramref name="exitCode"/>: offers the folder it asked to have trusted, if it asked. A run that exited 0 asked for nothing.</summary>
    private void OfferTrustHint(CliInvocation invocation, int exitCode)
    {
        if (exitCode == 0 || TrustHint.Find(invocation.OutputLines.Select(static l => l.Text)) is not { } hint)
        {
            ClearTrustHint();
            return;
        }

        TrustHintMessage = "This setup stopped because its program is in a folder DefenseClaw does not trust: " + hint.Directory + ". Trust it, then press Execute again.";
        TrustHintDirectory = hint.Directory;
    }

    private void ClearTrustHint()
    {
        TrustHintDirectory = string.Empty;
        TrustHintMessage = string.Empty;
    }
}
