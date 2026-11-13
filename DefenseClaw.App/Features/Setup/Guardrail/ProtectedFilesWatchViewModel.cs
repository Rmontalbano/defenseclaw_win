using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>One choice of the watch's <c>on_change</c> setting.</summary>
public sealed record ProtectedFilesModeChoice(string Value, string Label, string Note)
{
    public override string ToString() => Label;
}

/// <summary>
/// The protected-file watch's setting under the guardrail quick controls (CUST-422): what config.yaml says about <c>guardrail.protected_files</c> and a reviewed
/// change of <c>on_change</c>. 1.0.6's Setup catalog does not describe the key (<c>build_setup_sections</c>, tui/panels/setup.py), so the config editor lists it only
/// once a file sets it, under Other; this is where it can be chosen.
/// <para>
/// <b>Only where it is true.</b> Hidden on a runtime before 1.0.6, which refuses a config that sets the key, and on a managed installation, whose gateway has no watch
/// (<c>newProtectedFileWatch</c>, internal/gateway/protected_files.go). What it lists as watched is the file's <c>paths</c> or the Windows built-in list
/// (<c>DefaultPaths</c>, internal/protectedfiles/watch.go), and it does not say a running watch exists: only that this is what the gateway checks while it runs.
/// </para>
/// <para>
/// <b>Writes.</b> Only through <see cref="DiscoverActionReview"/>: <c>config set guardrail.protected_files.on_change VALUE</c>, or <c>config unset</c> of that key to go back to the
/// default (which leaves nothing in the file for an older release to refuse; cmd_config.py removes the empty block). The key is in the gateway's restart set
/// (configwrite.go:641), and <c>config set</c> does not restart it, so a change that ran joins the restart queue. The app never edits config.yaml itself.
/// </para>
/// </summary>
public sealed partial class ProtectedFilesWatchViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DiscoverActionReview _review;

    private ProtectedFilesSettings _saved = ProtectedFilesSettings.Default;

    /// <summary>The panel's choice, which may be ahead of the file until a review ran.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedNote))]
    [NotifyPropertyChangedFor(nameof(CanReview))]
    [NotifyCanExecuteChangedFor(nameof(ReviewChangeCommand))]
    private string _selectedMode = ProtectedFilesSettings.Alert;

    /// <summary>True while the card is on screen: a runtime with the watch, and a per-user installation.</summary>
    [ObservableProperty]
    private bool _isShown;

    /// <summary>"Alert" / "Restore" / "Off": what config.yaml says now.</summary>
    [ObservableProperty]
    private string _badgeText = "Alert";

    /// <summary>The tone of <see cref="BadgeText"/>.</summary>
    [ObservableProperty]
    private string _badgeKey = "Ok";

    /// <summary>What is checked, in one sentence.</summary>
    [ObservableProperty]
    private string _watchedText = string.Empty;

    /// <summary>A value config.yaml holds that is none of the three; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnrecognized))]
    private string _unrecognizedText = string.Empty;

    /// <summary>Why the change button is off on an installation that cannot be changed; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReview))]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(ReviewChangeCommand))]
    private string? _blockedReason;

    public ProtectedFilesWatchViewModel(AppServices services, DiscoverActionReview review)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _review = review ?? throw new ArgumentNullException(nameof(review));
        Refresh();
    }

    /// <summary>The setting's choices, in the runtime's order.</summary>
    public IReadOnlyList<ProtectedFilesModeChoice> Choices { get; } = new[]
    {
        new ProtectedFilesModeChoice(
            ProtectedFilesSettings.Alert,
            "Alert (default)",
            "A change while an agent tool call runs raises a HIGH alert. A change with no agent call running raises nothing and becomes the accepted content."),
        new ProtectedFilesModeChoice(
            ProtectedFilesSettings.Restore,
            "Restore (strict setups only)",
            "Alerts, and puts back the last accepted content after an agent's change. Off in every pack."),
        new ProtectedFilesModeChoice(
            ProtectedFilesSettings.Off,
            "Off",
            "The gateway does not check these files, and raises no protected-file alert."),
    };

    /// <summary>What the gateway does, in two sentences.</summary>
    public string Summary => "The gateway checks a short list of files every 2 seconds while it runs. " + ProtectedFileWatchFacts.BlocksNothing;

    /// <summary>The attribution limit.</summary>
    public string AttributionText => ProtectedFileWatchFacts.Attribution;

    /// <summary>What a rollback does with the setting.</summary>
    public string RollbackText => ProtectedFileWatchFacts.RollbackWarning;

    /// <summary>What the selected choice does.</summary>
    public string SelectedNote => Choices.FirstOrDefault(c => c.Value == SelectedMode)?.Note ?? string.Empty;

    public bool HasUnrecognized => UnrecognizedText.Length > 0;

    /// <summary>
    /// What 1.0.9 protects beyond the watch (<see cref="ProtectedFileWatchFacts.ShellFloorCoverage"/>); empty on a runtime before it, whose card reads as it
    /// always did.
    /// </summary>
    public string SelfProtectionText => _services.Runtime.Capabilities.Has(RuntimeCapability.ShellProtectedFileFloor) ? ProtectedFileWatchFacts.ShellFloorCoverage : string.Empty;

    public bool HasSelfProtection => SelfProtectionText.Length > 0;

    /// <summary>True when the choice box can be used.</summary>
    public bool CanChange => BlockedReason is null;

    /// <summary>True when there is a change to review.</summary>
    public bool CanReview => BlockedReason is null && !string.Equals(SelectedMode, _saved.OnChange, StringComparison.Ordinal);

    /// <summary>The exact command for a mode: <c>config set</c>, or <c>config unset</c> for the default, which is what unset means.</summary>
    internal static IReadOnlyList<string> ArgvFor(string mode) =>
        string.Equals(mode, ProtectedFilesSettings.Alert, StringComparison.Ordinal)
            ? new[] { "config", "unset", ProtectedFileWatchFacts.OnChangeKey }
            : new[] { "config", "set", ProtectedFileWatchFacts.OnChangeKey, mode };

    /// <summary>The warning bars of the review of <paramref name="mode"/>.</summary>
    internal static IReadOnlyList<CommandReviewWarning> WarningsFor(string mode)
    {
        var warnings = new List<CommandReviewWarning>();
        if (string.Equals(mode, ProtectedFilesSettings.Restore, StringComparison.Ordinal))
        {
            warnings.Add(new CommandReviewWarning("Restore changes these files", ProtectedFileWatchFacts.RestoreWarning));
        }
        else if (string.Equals(mode, ProtectedFilesSettings.Off, StringComparison.Ordinal))
        {
            warnings.Add(new CommandReviewWarning("This turns the watch off", "No change to these files is reported while it is off, whoever makes it."));
        }

        warnings.Add(new CommandReviewWarning("Gateway not restarted", ProtectedFileWatchFacts.RestartNote));
        if (!string.Equals(mode, ProtectedFilesSettings.Alert, StringComparison.Ordinal))
        {
            warnings.Add(new CommandReviewWarning("Rolling back to an older release", ProtectedFileWatchFacts.RollbackWarning));
        }

        return warnings;
    }

    /// <summary>Reads config.yaml's value, the runtime's answer and the installation again. No I/O of its own; cheap, and safe to call whenever one of them may have moved.</summary>
    public void Refresh()
    {
        IsShown = _services.Runtime.Capabilities.Has(RuntimeCapability.ProtectedFileWatch) && !_services.Installation.Context.IsManaged;
        BlockedReason = _services.Installation.BlockedReason;

        var settings = _services.Config.Config.Guardrail.ProtectedFiles;
        var previous = _saved.OnChange;
        _saved = settings;

        // A choice the operator is part-way through stays; one that only showed the file follows the file.
        if (string.Equals(SelectedMode, previous, StringComparison.Ordinal))
        {
            SelectedMode = settings.OnChange;
        }

        BadgeText = settings.OnChange switch
        {
            ProtectedFilesSettings.Restore => "Restore",
            ProtectedFilesSettings.Off => "Off",
            _ => "Alert",
        };
        BadgeKey = settings.OnChange switch
        {
            ProtectedFilesSettings.Restore => "Medium",
            ProtectedFilesSettings.Off => "Medium",
            _ => "Ok",
        };
        WatchedText = WatchedSentence(settings);
        UnrecognizedText = settings.Unrecognized is { } value
            ? $"config.yaml sets on_change to \"{value}\", which is not alert, restore or off: the runtime's validation refuses that value. Choose one and review the change."
            : string.Empty;

        OnPropertyChanged(nameof(SelfProtectionText));
        OnPropertyChanged(nameof(HasSelfProtection));
        OnPropertyChanged(nameof(CanReview));
        ReviewChangeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What these settings have the gateway check, without claiming more than the runtime does.</summary>
    internal static string WatchedSentence(ProtectedFilesSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.Equals(settings.OnChange, ProtectedFilesSettings.Off, StringComparison.Ordinal))
        {
            return "Nothing is checked: on_change is off.";
        }

        var names = string.Join(", ", settings.WatchedPaths);
        return settings.HasConfiguredPaths
            ? $"Checked, in your home folder (guardrail.protected_files.paths, which replaces the built-in list): {names}."
            : $"Checked, in your home folder (the built-in list on Windows): {names}. {ProtectedFileWatchFacts.WindowsCoverage}";
    }

    /// <summary>Opens the review of the selected mode. Nothing runs until it is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private void ReviewChange()
    {
        var mode = SelectedMode;
        var argv = ArgvFor(mode);
        _review.Open(
            "Change the protected-file watch?",
            Explanation(mode),
            new[] { new DiscoverStep(argv, $"Set {ProtectedFileWatchFacts.OnChangeKey} to {mode}.", CommandTier.StateChanging) },
            onFinished: result => OnFinishedAsync(result, mode),
            restartsGateway: false,
            primaryText: "Save the setting",
            extraWarnings: WarningsFor(mode));
    }

    private static string Explanation(string mode) => mode switch
    {
        ProtectedFilesSettings.Restore => "Saves on_change = restore through the CLI. After an agent's change to a protected file the gateway raises the alert and puts the last accepted content back.",
        ProtectedFilesSettings.Off => "Saves on_change = off through the CLI. The gateway stops checking these files.",
        _ => "Removes on_change from config.yaml through the CLI, so the default applies: the gateway raises a HIGH alert for a change while an agent tool call runs.",
    };

    internal Task OnFinishedAsync(DiscoverReviewResult result, string mode)
    {
        if (result.Succeeded)
        {
            // config.yaml has it and the running gateway does not until it starts again (RESTART_KEYS, config_writer.py).
            _ = _services.RestartQueue.Queue($"{ProtectedFileWatchFacts.OnChangeKey} is now {mode}");
            _services.ReloadConfig();
        }

        Refresh();
        return Task.CompletedTask;
    }
}
