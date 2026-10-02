using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>One card on a guide page. A choice card is bound to the wizard's own switch, so picking it is just answering that field.</summary>
public sealed partial class WizardGuideCardViewModel : ObservableObject
{
    private readonly WizardFieldViewModel? _field;
    private readonly Action<string> _open;

    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>Why the card cannot be chosen right now (Docker missing, engine down, still checking), or empty.</summary>
    [ObservableProperty]
    private string _unavailableReason = string.Empty;

    /// <summary>A caution shown while the card is available: what choosing it starts, and anything the CLI's own checks would refuse.</summary>
    [ObservableProperty]
    private string _caution = string.Empty;

    [ObservableProperty]
    private bool _isChecking;

    public WizardGuideCardViewModel(WizardGuideCard card, WizardFieldViewModel? field, Action<string>? open = null)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
        _field = field;
        _open = open ?? OpenInBrowser;
        LinkItems = card.Links.Select(l => new WizardGuideLinkItem(l.Label, l.Url, OpenLinkCommand)).ToArray();

        if (field is not null)
        {
            field.PropertyChanged += OnFieldChanged;
        }
    }

    public WizardGuideCard Card { get; }

    public string Title => Card.Title;

    public string Summary => Card.Summary;

    public string CliSays => Card.CliSays;

    public bool HasCliSays => Card.CliSays.Length > 0;

    public IReadOnlyList<string> Needs => Card.Needs;

    public bool HasNeeds => Card.Needs.Count > 0;

    /// <summary>The card's links, each carrying the command that opens it.</summary>
    public IReadOnlyList<WizardGuideLinkItem> LinkItems { get; }

    public bool HasLinks => LinkItems.Count > 0;

    public bool CanCheckAgain => !IsChecking;

    public string WillDo => Card.WillDo;

    public bool HasWillDo => Card.WillDo.Length > 0;

    /// <summary>True for a card that picks a pipeline; false for an information card.</summary>
    public bool IsChoice => _field is not null;

    /// <summary>The flag picking this card adds to the command, shown as a chip.</summary>
    public string FlagDisplay => _field?.FlagDisplay ?? string.Empty;

    public bool RequiresDocker => Card.Requires == WizardGuideRequirement.Docker;

    public bool IsUnavailable => !IsAvailable;

    public bool HasUnavailableReason => UnavailableReason.Length > 0;

    public bool HasCaution => Caution.Length > 0;

    /// <summary>Raised by the card's "Check again"; the wizard re-runs its Docker probe.</summary>
    public Action? Recheck { get; set; }

    public string AutomationName => IsChoice ? "Send to: " + Title : Title;

    /// <summary>Two-way target for the card's switch. An unavailable card cannot be turned on.</summary>
    public bool IsSelected
    {
        get => _field?.IsOn == true;
        set
        {
            if (_field is null)
            {
                return;
            }

            if (value && !IsAvailable)
            {
                // The switch is disabled, so this is only a stray write; say the answer is still off.
                OnPropertyChanged(nameof(IsSelected));
                return;
            }

            _field.IsOn = value;
        }
    }

    /// <summary>Turns a card the machine cannot support off, whatever it was answered before.</summary>
    internal void ForceOff()
    {
        if (_field?.IsOn == true)
        {
            _field.IsOn = false;
        }
    }

    /// <summary>The probe is running: the card is shown but cannot be chosen yet.</summary>
    internal void BeginChecking()
    {
        IsChecking = true;
        IsAvailable = false;
        UnavailableReason = "Checking for Docker…";
        Caution = string.Empty;
    }

    /// <summary>Applies what the read-only Docker probe found.</summary>
    internal void ApplyDocker(DockerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        IsChecking = false;
        if (!status.AllowsLocalSplunk)
        {
            IsAvailable = false;
            UnavailableReason = status.Summary + " Local Splunk runs in Docker: install or start Docker Desktop, then check again.";
            Caution = string.Empty;
            ForceOff();
            return;
        }

        IsAvailable = true;
        UnavailableReason = string.Empty;
        Caution = "Choosing this starts containers on this machine (Splunk in Free mode, with its own storage volumes) through Docker Desktop. " +
                  "Docker Desktop's Linux-container engine needs Hyper-V virtual-machine resources — memory and disk — on this PC. " +
                  (status.Warnings.Count > 0
                      ? "The CLI checks Docker itself before it changes anything, and from here it looks like it would refuse: " + string.Join(" ", status.Warnings)
                      : string.Empty);
        Caution = Caution.Trim();
    }

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(CanCheckAgain));

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(IsUnavailable));

    partial void OnUnavailableReasonChanged(string value) => OnPropertyChanged(nameof(HasUnavailableReason));

    partial void OnCautionChanged(string value) => OnPropertyChanged(nameof(HasCaution));

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(WizardFieldViewModel.Value), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            _open(uri.AbsoluteUri);
        }
    }

    [RelayCommand]
    private void CheckAgain() => Recheck?.Invoke();

    private static void OpenInBrowser(string url)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No default browser; the address is on screen in the link's tooltip and the operator can copy it.
        }
    }
}

/// <summary>A link on a card with the command that opens it, so the template needs no ancestor lookups.</summary>
public sealed record WizardGuideLinkItem(string Label, string Url, System.Windows.Input.ICommand Open);

/// <summary>The cards of one guide page, and the page's introduction.</summary>
public sealed class WizardGuideViewModel
{
    public WizardGuideViewModel(WizardGuide guide, IReadOnlyList<WizardGuideCardViewModel> cards)
    {
        Guide = guide ?? throw new ArgumentNullException(nameof(guide));
        Cards = cards ?? throw new ArgumentNullException(nameof(cards));
    }

    public WizardGuide Guide { get; }

    public string Intro => Guide.Intro;

    public bool HasIntro => Guide.Intro.Length > 0;

    public IReadOnlyList<WizardGuideCardViewModel> Cards { get; }

    /// <summary>True when at least one choice card is on, or the page has no choice to make.</summary>
    public bool HasSelection => !Cards.Any(c => c.IsChoice) || Cards.Any(c => c.IsChoice && c.IsSelected);
}
