using CommunityToolkit.Mvvm.Input;

namespace DefenseClaw.App.ViewModels;

public sealed partial class AiDiscoveryPanelViewModel
{
    /// <summary>
    /// The tuning dialog (CUST-271): mode, cadence, scope and sources over <c>agent discovery enable</c>, reviewed and run through its own
    /// <see cref="DiscoverActionReview"/>. After a run the panel reads its state again, as it does after any other action here.
    /// </summary>
    public AiDiscoveryTuningViewModel Tuning { get; }

    /// <summary>"Tune…": opens the dialog on what config.yaml says now. Not available while another command of this panel is open or running.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTuning))]
    private void OpenTuning() => Tuning.Open();

    private bool CanOpenTuning() => !Review.IsOpen && !Review.IsRunning;
}
