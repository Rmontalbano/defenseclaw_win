using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Policy;

namespace DefenseClaw.App.ViewModels;

/// <summary>What the Policies form is for.</summary>
public enum PolicyFormMode
{
    Closed,
    Create,
    Edit,
}

/// <summary>An entry of a form's drop-down: the value that goes on the command line (empty: the flag is not passed) and what it is called.</summary>
public sealed record PolicyChoice(string Value, string Label);

/// <summary>
/// The form behind <c>policy create</c> and <c>policy edit</c>. It only collects values and builds the request
/// (<see cref="PolicyCreate"/> / <see cref="PolicyEdit"/>, which validate them); the panel puts the result through the review
/// dialog like every other change, so nothing here runs a command. Every field is text or a choice, and an empty one is a flag the
/// command is not given.
/// </summary>
public sealed partial class PolicyFormViewModel : ObservableObject
{
    public const string SectionActions = "actions";
    public const string SectionScanner = "scanner";
    public const string SectionGuardrail = "guardrail";
    public const string SectionFirewall = "firewall";

    private static readonly PolicyChoice Unchanged = new(string.Empty, "(unchanged)");

    public static IReadOnlyList<PolicyChoice> SeverityChoices { get; } = PolicyEdit.Severities.Select(s => new PolicyChoice(s, s.ToUpperInvariant())).ToArray();

    public static IReadOnlyList<PolicyChoice> ScannerChoices { get; } = PolicyEdit.ScannerTypes.Select(s => new PolicyChoice(s, s)).ToArray();

    public static IReadOnlyList<PolicyChoice> RuntimeChoices { get; } = Unchanged.Then(PolicyEdit.RuntimeChoices);

    public static IReadOnlyList<PolicyChoice> FileChoices { get; } = Unchanged.Then(PolicyEdit.FileChoices);

    public static IReadOnlyList<PolicyChoice> InstallChoices { get; } = Unchanged.Then(PolicyEdit.InstallChoices);

    public static IReadOnlyList<PolicyChoice> TrustChoices { get; } = Unchanged.Then(PolicyEdit.TrustLevels);

    public static IReadOnlyList<PolicyChoice> FirewallActionChoices { get; } = Unchanged.Then(PolicyEdit.FirewallActions);

    public static IReadOnlyList<PolicyChoice> ThresholdChoices { get; } = new[]
    {
        Unchanged,
        new PolicyChoice("1", "1 - LOW and above"),
        new PolicyChoice("2", "2 - MEDIUM and above"),
        new PolicyChoice("3", "3 - HIGH and above"),
        new PolicyChoice("4", "4 - CRITICAL only"),
    };

    public static IReadOnlyList<PolicyChoice> PresetChoices { get; } =
        new[] { new PolicyChoice(string.Empty, "(none: start from the defaults)") }.Concat(PolicyCreate.Presets.Select(p => new PolicyChoice(p, p))).ToArray();

    public static IReadOnlyList<PolicyChoice> SwitchChoices { get; } = new[]
    {
        new PolicyChoice(string.Empty, "(keep the default or the preset's value)"),
        new PolicyChoice("on", "On"),
        new PolicyChoice("off", "Off"),
    };

    public static IReadOnlyList<PolicyChoice> LevelChoices { get; } =
        new[] { new PolicyChoice(string.Empty, "(default)") }.Concat(PolicyCreate.ActionLevels.Select(l => new PolicyChoice(l, l))).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsCreate), nameof(IsEdit), nameof(Heading), nameof(SubmitLabel))]
    private PolicyFormMode _mode;

    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>The policy an edit is for.</summary>
    [ObservableProperty]
    private string _targetName = string.Empty;

    [ObservableProperty]
    private string _targetNote = string.Empty;

    // create
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _preset = string.Empty;
    [ObservableProperty] private string _scanOnInstall = string.Empty;
    [ObservableProperty] private string _allowListBypass = string.Empty;
    [ObservableProperty] private string _criticalAction = string.Empty;
    [ObservableProperty] private string _highAction = string.Empty;
    [ObservableProperty] private string _mediumAction = string.Empty;
    [ObservableProperty] private string _lowAction = string.Empty;

    // edit
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActionsSection), nameof(IsScannerSection), nameof(IsGuardrailSection), nameof(IsFirewallSection), nameof(ShowSeverity), nameof(ShowActionFields))]
    private string _section = SectionActions;

    [ObservableProperty] private string _severity = "critical";
    [ObservableProperty] private string _scannerType = "skill";
    [ObservableProperty] private string _runtime = string.Empty;
    [ObservableProperty] private string _file = string.Empty;
    [ObservableProperty] private string _install = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActionFields))]
    private bool _removeOverride;

    [ObservableProperty] private string _blockThreshold = string.Empty;
    [ObservableProperty] private string _alertThreshold = string.Empty;
    [ObservableProperty] private string _trustLevel = string.Empty;
    [ObservableProperty] private string _addPatternCategory = string.Empty;
    [ObservableProperty] private string _addPattern = string.Empty;
    [ObservableProperty] private string _removePatternCategory = string.Empty;
    [ObservableProperty] private string _removePattern = string.Empty;
    [ObservableProperty] private string _mappingCategory = string.Empty;
    [ObservableProperty] private string _mappingSeverity = string.Empty;

    [ObservableProperty] private string _defaultAction = string.Empty;
    [ObservableProperty] private string _addDomain = string.Empty;
    [ObservableProperty] private string _removeDomain = string.Empty;
    [ObservableProperty] private string _addBlocked = string.Empty;
    [ObservableProperty] private string _removeBlocked = string.Empty;
    [ObservableProperty] private string _addPort = string.Empty;
    [ObservableProperty] private string _removePort = string.Empty;

    public bool IsOpen => Mode != PolicyFormMode.Closed;

    public bool IsCreate => Mode == PolicyFormMode.Create;

    public bool IsEdit => Mode == PolicyFormMode.Edit;

    public bool IsActionsSection => Section == SectionActions;

    public bool IsScannerSection => Section == SectionScanner;

    public bool IsGuardrailSection => Section == SectionGuardrail;

    public bool IsFirewallSection => Section == SectionFirewall;

    /// <summary>The severity row is shared by the two sections that act on one severity.</summary>
    public bool ShowSeverity => IsActionsSection || IsScannerSection;

    /// <summary>The runtime / file / install drop-downs: not when the scanner override is being removed.</summary>
    public bool ShowActionFields => IsActionsSection || (IsScannerSection && !RemoveOverride);

    public string Heading => Mode switch
    {
        PolicyFormMode.Create => "Create a policy",
        PolicyFormMode.Edit => $"Edit policy '{TargetName}'",
        _ => string.Empty,
    };

    public string SubmitLabel => Mode == PolicyFormMode.Edit ? "Review edit" : "Review create";

    /// <summary>Opens an empty create form.</summary>
    public void OpenCreate()
    {
        Reset();
        Mode = PolicyFormMode.Create;
    }

    /// <summary>Opens an edit form for <paramref name="policy"/>.</summary>
    /// <param name="isBuiltIn">Editing a built-in saves a custom copy that shadows it (copy-on-write in the CLI); the form says so.</param>
    /// <param name="isActive">An edit of the active policy is synced to the live OPA data at once; any other is saved as a draft.</param>
    public void OpenEdit(string policy, bool isBuiltIn, bool isActive)
    {
        Reset();
        TargetName = policy;
        TargetNote = (isBuiltIn ? "A built-in policy is not edited in place: the CLI saves the result as a custom copy named " + policy + " in your policy folder, which then shadows the built-in. " : string.Empty)
                     + (isActive
                         ? "This is the active policy, so the change is applied to the live policy data immediately."
                         : "This is not the active policy, so the change is saved as a draft until you activate it.");
        Mode = PolicyFormMode.Edit;
    }

    public void Close()
    {
        Mode = PolicyFormMode.Closed;
        Error = string.Empty;
    }

    private void Reset()
    {
        Error = string.Empty;
        TargetName = string.Empty;
        TargetNote = string.Empty;
        Name = Description = Preset = ScanOnInstall = AllowListBypass = string.Empty;
        CriticalAction = HighAction = MediumAction = LowAction = string.Empty;
        Section = SectionActions;
        Severity = "critical";
        ScannerType = "skill";
        Runtime = File = Install = string.Empty;
        RemoveOverride = false;
        BlockThreshold = AlertThreshold = TrustLevel = string.Empty;
        AddPatternCategory = AddPattern = RemovePatternCategory = RemovePattern = MappingCategory = MappingSeverity = string.Empty;
        DefaultAction = AddDomain = RemoveDomain = AddBlocked = RemoveBlocked = AddPort = RemovePort = string.Empty;
    }

    /// <summary>Builds the create request from the fields; false (with <see cref="Error"/> set) when it is not valid.</summary>
    public bool TryBuildCreate(IEnumerable<string> existingNames, out PolicyCreate? create)
    {
        var built = new PolicyCreate
        {
            Name = Name.Trim(),
            Description = Description.Trim(),
            FromPreset = Preset,
            ScanOnInstall = Tri(ScanOnInstall),
            AllowListBypass = Tri(AllowListBypass),
            CriticalAction = CriticalAction,
            HighAction = HighAction,
            MediumAction = MediumAction,
            LowAction = LowAction,
        };

        Error = built.Validate(existingNames) ?? string.Empty;
        create = Error.Length == 0 ? built : null;
        return create is not null;
    }

    /// <summary>Builds the edit request from the fields; false (with <see cref="Error"/> set) when it is not valid.</summary>
    public bool TryBuildEdit(out PolicyEdit? edit)
    {
        edit = null;
        if (!TryPort(AddPort, "port to allow", out var addPort) || !TryPort(RemovePort, "port to remove", out var removePort))
        {
            return false;
        }

        var built = new PolicyEdit
        {
            Section = Section switch
            {
                SectionScanner => PolicyEditSection.Scanner,
                SectionGuardrail => PolicyEditSection.Guardrail,
                SectionFirewall => PolicyEditSection.Firewall,
                _ => PolicyEditSection.Actions,
            },
            Severity = Severity,
            ScannerType = ScannerType,
            Runtime = Runtime,
            File = File,
            Install = Install,
            RemoveOverride = RemoveOverride && Section == SectionScanner,
            BlockThreshold = Rank(BlockThreshold),
            AlertThreshold = Rank(AlertThreshold),
            CiscoTrustLevel = TrustLevel,
            AddPatternCategory = AddPatternCategory.Trim(),
            AddPattern = AddPattern.Trim(),
            RemovePatternCategory = RemovePatternCategory.Trim(),
            RemovePattern = RemovePattern.Trim(),
            MappingCategory = MappingCategory.Trim(),
            MappingSeverity = MappingSeverity,
            DefaultAction = DefaultAction,
            AddDomain = AddDomain.Trim(),
            RemoveDomain = RemoveDomain.Trim(),
            AddBlocked = AddBlocked.Trim(),
            RemoveBlocked = RemoveBlocked.Trim(),
            AddPort = addPort,
            RemovePort = removePort,
        };

        Error = built.Validate() ?? string.Empty;
        edit = Error.Length == 0 ? built : null;
        return edit is not null;
    }

    private bool TryPort(string text, string what, out int? port)
    {
        port = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            port = value;
            return true;
        }

        Error = $"The {what} must be a number.";
        return false;
    }

    private static bool? Tri(string value) => value switch
    {
        "on" => true,
        "off" => false,
        _ => null,
    };

    private static int? Rank(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var rank) ? rank : null;
}

internal static class PolicyChoiceExtensions
{
    /// <summary>The choice, then one choice per value (label = value).</summary>
    public static IReadOnlyList<PolicyChoice> Then(this PolicyChoice first, IEnumerable<string> values) =>
        new[] { first }.Concat(values.Select(v => new PolicyChoice(v, v))).ToArray();
}
