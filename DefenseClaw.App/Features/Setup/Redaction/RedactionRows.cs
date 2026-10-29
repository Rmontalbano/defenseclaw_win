using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels.Redaction;

/// <summary>One entry of a combo box: the value the command line takes and the words the operator reads.</summary>
public sealed record RedactionChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A check box over one word of the vocabulary (a signal, a bucket, a detector group). Tells its form when it is ticked or cleared.</summary>
public sealed class RedactionCheck : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;
    private bool _isEnabled = true;

    public RedactionCheck(string value, Action changed)
    {
        Value = value;
        _changed = changed;
    }

    public string Value { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _changed();
            }
        }
    }

    /// <summary>False while "all buckets" stands for them.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>Sets the tick without telling the form (it is changing several at once and tells it after).</summary>
    internal void Set(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));
}

/// <summary>One field class of a custom profile and the mode chosen for it: empty leaves it as the base profile has it.</summary>
public sealed class RedactionFieldRow : ObservableObject
{
    /// <summary>The entries of every field's mode box: leave it, take the base profile's, or one of the five modes.</summary>
    public static IReadOnlyList<RedactionChoice> Modes { get; } =
    [
        new(string.Empty, "Leave as is"),
        new(RedactionVocabulary.InheritMode, "Base profile's"),
        .. RedactionVocabulary.FieldModes.Select(static m => new RedactionChoice(m, m)),
    ];

    private readonly Action _changed;
    private string _mode = string.Empty;

    public RedactionFieldRow(string fieldClass, Action changed)
    {
        Class = fieldClass;
        _changed = changed;
    }

    public string Class { get; }

    public string Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value ?? string.Empty))
            {
                _changed();
            }
        }
    }

    public string AutomationName => $"Mode for the {Class} field";
}

/// <summary>One bucket of the status card.</summary>
public sealed class RedactionBucketRow
{
    public RedactionBucketRow(RedactionBucket bucket)
    {
        Name = DisplayNames.Visible(bucket.Name);
        SignalsText = bucket.SignalsText;
        Profile = DisplayNames.Visible(bucket.Profile);
        Tone = bucket.ProfileTone;
    }

    public string Name { get; }

    public string SignalsText { get; }

    public string Profile { get; }

    public string Tone { get; }

    public string AutomationName => $"{Name}: {SignalsText}, profile {Profile}";
}

/// <summary>One ordered route of a destination in the status card.</summary>
public sealed class RedactionRouteRow
{
    public RedactionRouteRow(RedactionRoute route)
    {
        Position = route.Position;
        Name = DisplayNames.Visible(route.Name);
        Summary = DisplayNames.Visible(route.Summary);
        IsDrop = route.IsDrop;
    }

    public int Position { get; }

    public string Name { get; }

    public string Summary { get; }

    public bool IsDrop { get; }

    public string Tone => IsDrop ? "Warn" : "Neutral";

    public string AutomationName => $"Route {Position}, {Name}: {Summary}";
}

/// <summary>One destination of the status card, with its routes when it has them.</summary>
public sealed class RedactionDestinationRow
{
    public RedactionDestinationRow(RedactionDestination destination, IReadOnlyList<RedactionRoute>? routes, string routesNote)
    {
        Name = DisplayNames.Visible(destination.Name);
        Kind = DisplayNames.Visible(destination.Kind);
        PolicyText = DisplayNames.Visible(destination.PolicyFormText);
        SignalsText = DisplayNames.Visible(destination.SignalsText);
        Label = destination.Label.Length == 0 ? "not applicable" : DisplayNames.Visible(destination.Label);
        Tone = destination.Tone;
        Lock = destination.Lock;
        IsEnabled = destination.Enabled;
        Routes = (routes ?? []).Select(static r => new RedactionRouteRow(r)).ToArray();
        RoutesNote = DisplayNames.Visible(routesNote);
    }

    public string Name { get; }

    public string Kind { get; }

    public string PolicyText { get; }

    public string SignalsText { get; }

    /// <summary>The CLI's own redaction label: <c>unredacted (none)</c>, <c>redacted: sensitive</c>, <c>mixed: ...</c>.</summary>
    public string Label { get; }

    public string Tone { get; }

    /// <summary>Why the operator cannot change this one here; empty for a destination they configured.</summary>
    public string Lock { get; }

    public bool HasLock => Lock.Length > 0;

    public bool IsEnabled { get; }

    public IReadOnlyList<RedactionRouteRow> Routes { get; }

    public bool HasRoutes => Routes.Count > 0;

    /// <summary>Set when the routes could not be read; empty otherwise.</summary>
    public string RoutesNote { get; }

    public bool HasRoutesNote => RoutesNote.Length > 0;

    /// <summary>"otlp · ordered routes · logs · traces · metrics".</summary>
    public string Detail => $"{Kind} · {PolicyText} · {SignalsText}{(IsEnabled ? string.Empty : " · disabled")}";

    public string AutomationName => $"{Name}: {Detail}, {Label}{(HasLock ? ", " + Lock : string.Empty)}";
}

/// <summary>A warning of the compiled plan.</summary>
public sealed class RedactionWarningRow
{
    public RedactionWarningRow(RedactionWarning warning)
    {
        Summary = DisplayNames.Visible(warning.Summary.Length > 0 ? warning.Summary : warning.Code);
        Path = DisplayNames.Visible(warning.Path);
        Code = DisplayNames.Visible(warning.Code);
    }

    public string Code { get; }

    public string Summary { get; }

    public string Path { get; }

    public string Text => Path.Length > 0 ? $"{Summary} ({Path})" : Summary;
}

/// <summary>One row of the readable diff of a preview.</summary>
public sealed class RedactionDiffRow
{
    public RedactionDiffRow(RedactionDiffGroup group)
    {
        Target = DisplayNames.Visible(group.Target);
        Signal = DisplayNames.Visible(group.Signal);
        Before = DisplayNames.Visible(group.BeforeText);
        After = DisplayNames.Visible(group.AfterText);
        BucketsText = DisplayNames.Visible(group.BucketsText);
        KindText = group.KindText;
        Tone = group.Tone;
        AllBuckets = group.Buckets.Select(DisplayNames.Visible).ToArray();
    }

    public string Target { get; }

    public string Signal { get; }

    public string Before { get; }

    public string After { get; }

    public string BucketsText { get; }

    public string KindText { get; }

    public string Tone { get; }

    public IReadOnlyList<string> AllBuckets { get; }

    /// <summary>Every bucket by name, for the tooltip.</summary>
    public string BucketsDetail => string.Join(", ", AllBuckets);

    public string AutomationName => $"{Target}, {Signal}: {Before} to {After} on {BucketsText}, {KindText}";
}

/// <summary>A label and a value, for the facts a read prints (a profile's detectors, a field's mode).</summary>
public sealed record RedactionFact(string Label, string Value);
