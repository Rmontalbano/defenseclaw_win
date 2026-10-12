using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The "Run gateway as administrator" switch and what sits under it, shared by the Overview's Quick Actions and Settings -> Connection so the two
/// cannot disagree: the Mac's wording adapted for Windows, the status line from <see cref="GatewayAdminService"/>, and (when they apply) the reviewed
/// "Repair ownership" and "Prepare administrator copy" buttons. Flipping the switch saves the setting and starts and stops nothing. A panel
/// calls <see cref="Attach"/> when it comes on screen and <see cref="Detach"/> when it goes, so nothing is followed while nobody looks.
/// </summary>
public sealed partial class GatewayAdminToggleViewModel : ObservableObject
{
    /// <summary>The row's heading.</summary>
    public const string Heading = "Run gateway as administrator";

    /// <summary>The Mac's sentence, for Windows: one Windows (UAC) prompt per start, and what it is for.</summary>
    public const string Caption =
        "Runs your installed gateway with administrator rights (one Windows prompt per start). Needed only for runtime monitoring's agent-actions plane.";

    private readonly AppServices _services;
    private int _syncing;
    private bool _attached;

    internal GatewayAdminToggleViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Apply(_services.GatewayAdmin.Status);
    }

    /// <summary>The switch. Setting it saves the mode; a refusal (the runtime or the account cannot do it) puts it back.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }

            if (_syncing == 0 && !_services.GatewayAdmin.TrySetMode(value))
            {
                OnPropertyChanged(nameof(IsOn));
                return;
            }

            _ = SetProperty(ref _isOn, value);
        }
    }

    private bool _isOn;

    /// <summary>The switch can be used: always to turn it off, to turn it on only where the runtime and the account allow it.</summary>
    [ObservableProperty]
    private bool _canToggle;

    /// <summary>Where the setup stands, in a sentence (<see cref="GatewayAdminStatus.Headline"/>), or why the switch is off.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>What is wrong with the setup, when something is: each reason on its own line. Empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string _detailText = string.Empty;

    public bool HasDetail => DetailText.Length > 0;

    /// <summary>"Repair ownership" is worth showing: files under .defenseclaw are owned by Administrators.</summary>
    [ObservableProperty]
    private bool _showRepair;

    /// <summary>"Prepare administrator copy" is worth showing: the mode is on and the copy is missing or differs.</summary>
    [ObservableProperty]
    private bool _showPrepare;

    /// <summary>Why the buttons are off (the installation's sentence first), or what they do.</summary>
    [ObservableProperty]
    private string _actionsTip = string.Empty;

    private bool _actionsAllowed;

    /// <summary>The toggle's own tooltip.</summary>
    [ObservableProperty]
    private string _toggleTip = Caption;

    /// <summary>Follows the service while the panel is on screen, and asks it to look again now.</summary>
    internal void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _services.GatewayAdmin.Changed += OnChanged;
        _services.Installation.Changed += OnInstallationChanged;
        Apply(_services.GatewayAdmin.Status);
        _services.GatewayAdmin.RequestRefresh();
    }

    internal void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _services.GatewayAdmin.Changed -= OnChanged;
        _services.Installation.Changed -= OnInstallationChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => UiPost.ToDispatcher(() => Apply(_services.GatewayAdmin.Status));

    private void OnInstallationChanged(object? sender, EventArgs e) => Apply(_services.GatewayAdmin.Status);

    internal void Apply(GatewayAdminStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        _syncing++;
        try
        {
            IsOn = status.ModeOn;
        }
        finally
        {
            _syncing--;
        }

        CanToggle = status.CanToggle || status.ModeOn;
        StatusText = status.Headline;
        ToggleTip = status.Supported && status.Eligibility == AdminEligibility.Eligible ? Caption : status.Headline;

        var detail = new List<string>();
        if (status.Supported && status.Eligibility == AdminEligibility.Eligible && (status.ModeOn || status.OwnershipProblem))
        {
            detail.AddRange(status.Problems.Take(3));
            if (status.OwnershipProblem)
            {
                detail.Add("Owned by Administrators: " + string.Join(", ", status.OwnershipFiles) + ".");
            }
        }

        DetailText = string.Join('\n', detail);

        ShowRepair = status.CanRepairOwnership;
        ShowPrepare = status.CanToggle && status.ModeOn && !status.OwnershipProblem && (!status.HelperTrusted || !status.CopyTrusted || status.CopyMatchesInstalled == false);

        var blocked = _services.Installation.BlockedReason;
        _actionsAllowed = blocked is null;
        ActionsTip = blocked ?? "Windows asks you to approve it. Asks for confirmation first.";
        RepairOwnershipCommand.NotifyCanExecuteChanged();
        PrepareCopyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunActions))]
    private Task RepairOwnership() => _services.GatewayLifecycle.RepairOwnershipAsync();

    [RelayCommand(CanExecute = nameof(CanRunActions))]
    private Task PrepareCopy() => _services.GatewayLifecycle.PrepareAdministratorCopyAsync();

    private bool CanRunActions() => _actionsAllowed;
}
