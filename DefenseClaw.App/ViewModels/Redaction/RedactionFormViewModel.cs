using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels.Redaction;

/// <summary>
/// The form of the advanced editor: the controls of the chosen <see cref="RedactionOperation"/> and what is typed in them, as a
/// <see cref="RedactionInputs"/>. It shows exactly the controls the operation names (<see cref="RedactionOperationInfo.Fields"/>), offers only
/// words from <see cref="RedactionVocabulary"/> and from what the runtime reported (its profiles, its destinations, a destination's routes), and
/// says what is missing before any command line is built. It runs nothing; <see cref="RedactionViewModel"/> does.
/// <para>
/// Every change raises <see cref="Changed"/>, so the command line under the form, the message and a preview that no longer matches follow it.
/// </para>
/// </summary>
public sealed partial class RedactionFormViewModel : ObservableObject
{
    private static readonly IReadOnlyList<RedactionChoice> CollectChoices =
    [
        new(string.Empty, "Leave as is"),
        new("on", "Collect"),
        new("off", "Do not collect"),
    ];

    private readonly Func<string, IReadOnlyList<RedactionRoute>> _routesOf;
    private int _suspended;
    private RedactionOperationInfo _operation = RedactionOperations.All[0];

    private string _profile = string.Empty;
    private bool _inheritProfile;
    private string _bucket = string.Empty;
    private string _destination = string.Empty;
    private string _routeName = string.Empty;
    private string _positionText = string.Empty;
    private bool _allBuckets;
    private string _collectLogs = string.Empty;
    private string _collectTraces = string.Empty;
    private string _collectMetrics = string.Empty;
    private string _profileName = string.Empty;
    private string _extends = string.Empty;
    private string _replaceWith = string.Empty;
    private string _sourcesText = string.Empty;
    private string _connectorsText = string.Empty;
    private string _producerActionsText = string.Empty;
    private string _eventNamesText = string.Empty;
    private string _minSeverity = string.Empty;
    private string _routeAction = "send";
    private IReadOnlyList<string> _knownProfiles = RedactionVocabulary.BuiltInProfiles;
    private bool _profilesKnown;
    private IReadOnlyList<RedactionDestination> _destinations = [];
    private bool _destinationsKnown;

    public RedactionFormViewModel(Func<string, IReadOnlyList<RedactionRoute>>? routesOf = null)
    {
        _routesOf = routesOf ?? (static _ => []);
        SignalChecks = RedactionVocabulary.Signals.Select(s => new RedactionCheck(s, OnInputChanged)).ToArray();
        BucketChecks = RedactionVocabulary.Buckets.Select(b => new RedactionCheck(b, OnInputChanged)).ToArray();
        DetectorChecks = RedactionVocabulary.DetectorGroups.Select(d => new RedactionCheck(d, OnInputChanged)).ToArray();
        FieldRows = RedactionVocabulary.FieldClasses.Select(c => new RedactionFieldRow(c, OnInputChanged)).ToArray();
        ApplyProfiles(RedactionVocabulary.BuiltInProfiles, known: false);
        Select(RedactionOperation.Status);
    }

    /// <summary>Raised after any input changed (or the operation did).</summary>
    public event EventHandler? Changed;

    // ------------------------------------------------------------------ the operation

    public RedactionOperationInfo Operation
    {
        get => _operation;
        private set => SetProperty(ref _operation, value);
    }

    public IReadOnlyList<RedactionChoice> Collects => CollectChoices;

    public IReadOnlyList<RedactionChoice> Severities { get; } =
        [new(string.Empty, "Any severity"), .. RedactionVocabulary.Severities.Select(static s => new RedactionChoice(s, s + " and above"))];

    public IReadOnlyList<RedactionChoice> RouteActions { get; } =
        [new("send", "Send what matches"), new("drop", "Drop what matches")];

    public IReadOnlyList<RedactionChoice> BucketOptions { get; } =
        RedactionVocabulary.Buckets.Select(static b => new RedactionChoice(b, b)).ToArray();

    public IReadOnlyList<RedactionChoice> ExtendsChoices { get; } =
        [new(string.Empty, "Keep its current base"), .. RedactionVocabulary.CustomProfileBases.Select(static b => new RedactionChoice(b, b))];

    public IReadOnlyList<RedactionCheck> SignalChecks { get; }

    public IReadOnlyList<RedactionCheck> BucketChecks { get; }

    public IReadOnlyList<RedactionCheck> DetectorChecks { get; }

    public IReadOnlyList<RedactionFieldRow> FieldRows { get; }

    /// <summary>Every profile on offer where one is required: the built-ins and the custom ones.</summary>
    public ObservableCollection<RedactionChoice> ProfileChoices { get; } = new();

    /// <summary>The same with "Not set" first, where a profile is optional.</summary>
    public ObservableCollection<RedactionChoice> OptionalProfileChoices { get; } = new();

    /// <summary>The profiles <c>profile show</c> can show (all) or <c>profile set|remove</c> can name (the custom ones), for the editable name box.</summary>
    public ObservableCollection<string> ProfileNames { get; } = new();

    /// <summary>The replacement on offer when a profile is removed: "Not set", then every other profile.</summary>
    public ObservableCollection<RedactionChoice> ReplaceChoices { get; } = new();

    public ObservableCollection<RedactionChoice> DestinationChoices { get; } = new();

    /// <summary>The routes of the chosen destination, in order.</summary>
    public ObservableCollection<RedactionChoice> RouteChoices { get; } = new();

    /// <summary>
    /// Switches to <paramref name="operation"/> and clears what was typed: a form that kept the last operation's values would put them on
    /// the next one's command line.
    /// </summary>
    public void Select(RedactionOperation operation)
    {
        _suspended++;
        try
        {
            Operation = RedactionOperations.Info(operation);
            Profile = string.Empty;
            InheritProfile = false;
            Bucket = string.Empty;
            RouteName = string.Empty;
            PositionText = string.Empty;
            AllBuckets = false;
            CollectLogs = CollectTraces = CollectMetrics = string.Empty;
            ProfileName = string.Empty;
            Extends = string.Empty;
            ReplaceWith = string.Empty;
            SourcesText = ConnectorsText = ProducerActionsText = EventNamesText = string.Empty;
            MinSeverity = string.Empty;
            RouteAction = "send";
            foreach (var check in SignalChecks.Concat(BucketChecks).Concat(DetectorChecks))
            {
                check.Set(false);
                check.IsEnabled = true;
            }

            foreach (var row in FieldRows)
            {
                row.Mode = string.Empty;
            }

            // A destination is kept across operations when the new one can still use it.
            RebuildDestinations();
            RebuildRoutes();
            RebuildProfileLists();
        }
        finally
        {
            _suspended--;
        }

        RaiseShape();
        OnInputChanged();
    }

    // ------------------------------------------------------------------ what the runtime reported

    /// <summary>The profiles the runtime has (<c>profile list</c>); until it has said, the four built-ins.</summary>
    public void SetProfiles(IReadOnlyList<string>? names)
    {
        ApplyProfiles(names is { Count: > 0 } ? names : RedactionVocabulary.BuiltInProfiles, known: names is { Count: > 0 });
        OnInputChanged();
    }

    /// <summary>The destinations the runtime reported. A destination that is gone clears the choice.</summary>
    public void SetDestinations(IReadOnlyList<RedactionDestination> destinations)
    {
        _destinations = destinations;
        _destinationsKnown = true;
        RebuildDestinations();
        RebuildRoutes();
        OnInputChanged();
    }

    /// <summary>The routes were read again (after an apply): the route boxes show the new ones.</summary>
    public void RefreshRoutes()
    {
        RebuildRoutes();
        OnInputChanged();
    }

    private void ApplyProfiles(IReadOnlyList<string> names, bool known)
    {
        _knownProfiles = names;
        _profilesKnown = known;
        RebuildProfileLists();
    }

    private void RebuildProfileLists()
    {
        Fill(ProfileChoices, _knownProfiles.Select(static n => new RedactionChoice(n, ProfileLabel(n))));
        // Where a profile is optional, the first entry says what leaving it out means: the setting stays as it is, or the default applies.
        var notSet = new RedactionChoice(
            string.Empty,
            Operation.Operation is RedactionOperation.DefaultsSet or RedactionOperation.BucketSet ? "Leave as is" : "Use the default profile");
        Fill(OptionalProfileChoices, new[] { notSet }.Concat(_knownProfiles.Select(static n => new RedactionChoice(n, ProfileLabel(n)))));
        Fill(ReplaceChoices, new[] { new RedactionChoice(string.Empty, "Nothing (it must be unused)") }.Concat(_knownProfiles.Where(n => !string.Equals(n, ProfileName, StringComparison.Ordinal)).Select(static n => new RedactionChoice(n, ProfileLabel(n)))));

        var forShow = Operation.Operation == RedactionOperation.ProfileShow;
        Fill(ProfileNames, forShow ? _knownProfiles : _knownProfiles.Where(static n => !RedactionVocabulary.BuiltInProfiles.Contains(n)));
    }

    private static string ProfileLabel(string name) => RedactionVocabulary.BuiltInProfiles.Contains(name) ? name : DisplayNames.Visible(name) + " (custom)";

    private void RebuildDestinations()
    {
        var offered = Operation.NeedsConfigurableDestination ? _destinations.Where(static d => d.IsConfigurable) : _destinations;
        Fill(DestinationChoices, offered.Select(static d => new RedactionChoice(d.Name, DisplayNames.Visible(d.Name))));
        if (_destination.Length > 0 && DestinationChoices.All(c => c.Value != _destination))
        {
            Destination = string.Empty;
        }

        if (_destination.Length == 0 && DestinationChoices.Count == 1)
        {
            Destination = DestinationChoices[0].Value;
        }

        OnPropertyChanged(nameof(DestinationHint));
        OnPropertyChanged(nameof(HasDestinationChoices));
    }

    private void RebuildRoutes()
    {
        var routes = _destination.Length > 0 ? _routesOf(_destination) : [];
        Fill(RouteChoices, routes.Select(static r => new RedactionChoice(r.Name, DisplayNames.Visible($"{r.Position.ToString(CultureInfo.InvariantCulture)}. {r.Name}  ({r.Summary})"))));
        if (_routeName.Length > 0 && Operation.Operation != RedactionOperation.RouteAdd && RouteChoices.All(c => c.Value != _routeName))
        {
            RouteName = string.Empty;
        }

        OnPropertyChanged(nameof(HasRouteChoices));
        OnPropertyChanged(nameof(RouteHint));
    }

    /// <summary>
    /// Brings <paramref name="target"/> to <paramref name="items"/> by the smallest edit. A combo box bound to it keeps its selection when the
    /// chosen entry is still there: clearing and refilling the list would drop the selection (the box writes null back) even when the same
    /// entry comes straight back.
    /// </summary>
    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
        where T : notnull
    {
        var wanted = items.ToArray();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Length; i++)
        {
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], wanted[i]))
            {
                continue;
            }

            var found = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (EqualityComparer<T>.Default.Equals(target[j], wanted[i]))
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                target.Move(found, i);
            }
            else
            {
                target.Insert(i, wanted[i]);
            }
        }
    }

    // ------------------------------------------------------------------ the controls the operation shows

    private bool Has(RedactionFields field) => Operation.Fields.HasFlag(field);

    private bool IsApply => Operation.Operation is RedactionOperation.ApplyEverywhere or RedactionOperation.ApplyDefaults;

    /// <summary>A profile is required (the two <c>apply</c> operations).</summary>
    public bool ShowRequiredProfile => IsApply;

    /// <summary>A profile may be set. On a route it means nothing unless the route sends logs or traces.</summary>
    public bool ShowOptionalProfile => Has(RedactionFields.Profile) && !IsApply && (!Has(RedactionFields.RouteFilters) || RouteProfileApplies);

    /// <summary>True when a route sends, and sends logs or traces: the only route a profile means anything on.</summary>
    public bool RouteProfileApplies =>
        string.Equals(RouteAction, "send", StringComparison.Ordinal) && SignalChecks.Any(c => c.IsChecked && RedactionVocabulary.ContentSignals.Contains(c.Value));

    public bool ShowRouteProfileNote => Has(RedactionFields.RouteFilters) && Has(RedactionFields.Profile) && !RouteProfileApplies;

    public bool ShowInheritProfile => Has(RedactionFields.InheritProfile);

    public bool ShowBucket => Has(RedactionFields.Bucket);

    public bool ShowDestination => Has(RedactionFields.Destination);

    public bool ShowNewRouteName => Operation.Operation == RedactionOperation.RouteAdd;

    public bool ShowExistingRoute => Has(RedactionFields.RouteName) && Operation.Operation != RedactionOperation.RouteAdd;

    public bool ShowPosition => Has(RedactionFields.Position);

    public string PositionLabel => Operation.Operation == RedactionOperation.RouteAdd ? "Position (blank: last)" : "New position (1 is first)";

    public bool ShowSignals => Has(RedactionFields.Signals);

    public bool ShowBuckets => Has(RedactionFields.Buckets);

    /// <summary>"All buckets" is on offer where a send policy names buckets; a route that names none already matches every bucket.</summary>
    public bool ShowAllBuckets => Operation.Operation == RedactionOperation.DestinationSend;

    public string BucketsLabel => Operation.Operation == RedactionOperation.DestinationSend ? "Buckets to send" : "Buckets it matches (none ticked: any bucket)";

    public bool ShowCollect => Has(RedactionFields.Collect);

    public bool ShowProfileName => Has(RedactionFields.ProfileName);

    public string ProfileNameLabel => Operation.Operation switch
    {
        RedactionOperation.ProfileSet => "Profile name (pick one to edit, or type a new one)",
        RedactionOperation.ProfileRemove => "Custom profile to remove",
        _ => "Profile",
    };

    public bool ShowExtends => Has(RedactionFields.Extends);

    public bool ShowDetectors => Has(RedactionFields.Detectors);

    public bool ShowFieldModes => Has(RedactionFields.FieldModes);

    public bool ShowReplaceWith => Has(RedactionFields.ReplaceWith);

    public bool ShowRouteFilters => Has(RedactionFields.RouteFilters);

    /// <summary>A line of caution under the summary for the operations whose effect is wider than their name.</summary>
    public string Caution => Operation.Operation switch
    {
        RedactionOperation.RemoveAll => "Raw governed content is then kept in local SQLite and sent to every destination you configured. The managed enterprise destination, if there is one, stays locked.",
        RedactionOperation.ApplyEverywhere => "Also removes the more specific profiles: every bucket, send policy and route follows this one. The managed enterprise destination, if there is one, stays locked.",
        RedactionOperation.ApplyDefaults => "Only the global default changes. A bucket, send policy or route that names its own profile keeps it.",
        RedactionOperation.DefaultsReset => "The default profile becomes none (no redaction) again, and every signal is collected, unless a bucket says otherwise.",
        RedactionOperation.BucketReset => "The bucket follows the global defaults again, whatever they are.",
        RedactionOperation.ProfileSet => "A custom profile starts from sensitive, content or strict; a control you leave alone keeps what the profile has. The CLI refuses preserve for anything but metadata and identifiers, and a credential must be removed or replaced whole.",
        RedactionOperation.ProfileRemove => "Fails while something still uses the profile, unless you choose another profile to take over.",
        RedactionOperation.DestinationSend => "Replaces the destination's routes and any earlier send policy with this one.",
        RedactionOperation.DestinationInherit => "Removes the destination's own send policy and routes. It then takes everything it can, with the default profile.",
        RedactionOperation.RouteAdd => "Routes are tried in order and the first match wins. A destination with a send policy switches to routes: the send policy is removed.",
        RedactionOperation.RouteSet => "Rewrites the whole route and keeps its place. Pick a route and its current values are filled in; whatever you clear is dropped from it.",
        RedactionOperation.RouteMove => "Only the order changes: the first route that matches an event still wins.",
        RedactionOperation.RouteRemove => "Events the route used to send or drop then fall to the next route that matches, or to the destination's default.",
        _ => string.Empty,
    };

    public bool HasCaution => Caution.Length > 0;

    /// <summary>Under the destination box: why the list is short, or what is not on it.</summary>
    public string DestinationHint
    {
        get
        {
            if (!Operation.NeedsConfigurableDestination || !_destinationsKnown)
            {
                return string.Empty;
            }

            return _destinations.Any(static d => d.IsConfigurable)
                ? "Built-in destinations (local-sqlite, the managed enterprise one) are generated and read-only here, so they are not on the list."
                : "You have no destination of your own yet. The built-in local-sqlite destination is generated and read-only here, so there is nothing to choose. Add a destination in Setup, then refresh.";
        }
    }

    public bool HasDestinationChoices => DestinationChoices.Count > 0;

    public bool HasRouteChoices => RouteChoices.Count > 0;

    public string RouteHint => _destination.Length == 0
        ? "Choose a destination first."
        : RouteChoices.Count == 0 ? "This destination has no ordered routes." : string.Empty;

    private void RaiseShape()
    {
        foreach (var name in new[]
                 {
                     nameof(ShowRequiredProfile), nameof(ShowOptionalProfile), nameof(RouteProfileApplies), nameof(ShowRouteProfileNote), nameof(ShowInheritProfile), nameof(ShowBucket),
                     nameof(ShowDestination), nameof(ShowNewRouteName), nameof(ShowExistingRoute), nameof(ShowPosition), nameof(PositionLabel), nameof(ShowSignals), nameof(ShowBuckets),
                     nameof(ShowAllBuckets), nameof(BucketsLabel), nameof(ShowCollect), nameof(ShowProfileName), nameof(ProfileNameLabel), nameof(ShowExtends), nameof(ShowDetectors),
                     nameof(ShowFieldModes), nameof(ShowReplaceWith), nameof(ShowRouteFilters), nameof(Caution), nameof(HasCaution), nameof(DestinationHint), nameof(RouteHint),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ------------------------------------------------------------------ the values

    public string Profile
    {
        get => _profile;
        set => Set(ref _profile, value);
    }

    public bool InheritProfile
    {
        get => _inheritProfile;
        set => Set(ref _inheritProfile, value);
    }

    public string Bucket
    {
        get => _bucket;
        set => Set(ref _bucket, value);
    }

    public string Destination
    {
        get => _destination;
        set
        {
            if (Set(ref _destination, value))
            {
                RebuildRoutes();
            }
        }
    }

    /// <summary>The new route's name (add), or the existing route chosen (set, move, remove).</summary>
    public string RouteName
    {
        get => _routeName;
        set
        {
            if (Set(ref _routeName, value) && Operation.Operation == RedactionOperation.RouteSet && _suspended == 0)
            {
                Prefill(_routesOf(_destination).FirstOrDefault(r => r.Name == _routeName));
            }
        }
    }

    public string PositionText
    {
        get => _positionText;
        set => Set(ref _positionText, value);
    }

    /// <summary>"All buckets" (<c>--bucket *</c>): the named ones are then off.</summary>
    public bool AllBuckets
    {
        get => _allBuckets;
        set
        {
            if (!Set(ref _allBuckets, value))
            {
                return;
            }

            _suspended++;
            try
            {
                foreach (var check in BucketChecks)
                {
                    check.Set(false);
                    check.IsEnabled = !value;
                }
            }
            finally
            {
                _suspended--;
            }

            OnInputChanged();
        }
    }

    public string CollectLogs
    {
        get => _collectLogs;
        set => Set(ref _collectLogs, value);
    }

    public string CollectTraces
    {
        get => _collectTraces;
        set => Set(ref _collectTraces, value);
    }

    public string CollectMetrics
    {
        get => _collectMetrics;
        set => Set(ref _collectMetrics, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (Set(ref _profileName, value))
            {
                RebuildProfileLists();
            }
        }
    }

    public string Extends
    {
        get => _extends;
        set => Set(ref _extends, value);
    }

    public string ReplaceWith
    {
        get => _replaceWith;
        set => Set(ref _replaceWith, value);
    }

    public string SourcesText
    {
        get => _sourcesText;
        set => Set(ref _sourcesText, value);
    }

    public string ConnectorsText
    {
        get => _connectorsText;
        set => Set(ref _connectorsText, value);
    }

    public string ProducerActionsText
    {
        get => _producerActionsText;
        set => Set(ref _producerActionsText, value);
    }

    public string EventNamesText
    {
        get => _eventNamesText;
        set => Set(ref _eventNamesText, value);
    }

    public string MinSeverity
    {
        get => _minSeverity;
        set => Set(ref _minSeverity, value);
    }

    public string RouteAction
    {
        get => _routeAction;
        set
        {
            if (Set(ref _routeAction, string.IsNullOrEmpty(value) ? "send" : value))
            {
                OnPropertyChanged(nameof(ShowOptionalProfile));
                OnPropertyChanged(nameof(RouteProfileApplies));
                OnPropertyChanged(nameof(ShowRouteProfileNote));
            }
        }
    }

    private bool Set(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var next = value ?? string.Empty;
        if (!SetProperty(ref field, next, name))
        {
            return false;
        }

        OnInputChanged();
        return true;
    }

    private bool Set(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }

        OnInputChanged();
        return true;
    }

    private void OnInputChanged()
    {
        if (_suspended > 0)
        {
            return;
        }

        // A route's profile box appears and disappears with the signals ticked and the action.
        OnPropertyChanged(nameof(ShowOptionalProfile));
        OnPropertyChanged(nameof(RouteProfileApplies));
        OnPropertyChanged(nameof(ShowRouteProfileNote));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fills the route filters from a route that is about to be replaced, so replacing it does not silently drop what it matched.</summary>
    private void Prefill(RedactionRoute? route)
    {
        if (route is null)
        {
            return;
        }

        _suspended++;
        try
        {
            foreach (var check in SignalChecks)
            {
                check.Set(route.Signals.Contains(check.Value));
            }

            var wildcard = route.Selector.Buckets.Contains(RedactionVocabulary.AllBuckets);
            foreach (var check in BucketChecks)
            {
                check.Set(!wildcard && route.Selector.Buckets.Contains(check.Value));
            }

            SourcesText = string.Join(", ", route.Selector.Sources);
            ConnectorsText = string.Join(", ", route.Selector.Connectors);
            ProducerActionsText = string.Join(", ", route.Selector.Actions);
            EventNamesText = string.Join(", ", route.Selector.EventNames);
            MinSeverity = route.Selector.MinSeverity;
            RouteAction = route.IsDrop ? "drop" : "send";
            Profile = route.Profile;
        }
        finally
        {
            _suspended--;
        }

        OnInputChanged();
    }

    // ------------------------------------------------------------------ the result

    /// <summary>What the form holds, as the command builder takes it.</summary>
    public RedactionInputs ToInputs()
    {
        var signals = SignalChecks.Where(static c => c.IsChecked).Select(static c => c.Value).ToArray();
        var buckets = AllBuckets && ShowAllBuckets
            ? new[] { RedactionVocabulary.AllBuckets }
            : BucketChecks.Where(static c => c.IsChecked).Select(static c => c.Value).ToArray();

        return new RedactionInputs
        {
            Profile = _profile,
            InheritProfile = _inheritProfile && ShowInheritProfile,
            Bucket = _bucket,
            Destination = _destination,
            RouteName = _routeName.Trim(),
            Position = ParsePosition(),
            Signals = signals,
            Buckets = buckets,
            CollectLogs = Collect(_collectLogs),
            CollectTraces = Collect(_collectTraces),
            CollectMetrics = Collect(_collectMetrics),
            ProfileName = _profileName.Trim(),
            IsNewProfile = _profileName.Trim().Length > 0 && _profilesKnown && !_knownProfiles.Contains(_profileName.Trim(), StringComparer.Ordinal),
            Extends = _extends,
            Detectors = DetectorChecks.Where(static c => c.IsChecked).Select(static c => c.Value).ToArray(),
            FieldModes = FieldRows.Where(static r => r.Mode.Length > 0).Select(static r => new RedactionFieldMode(r.Class, r.Mode)).ToArray(),
            ReplaceWith = _replaceWith,
            Sources = Words(_sourcesText),
            Connectors = Words(_connectorsText),
            ProducerActions = Words(_producerActionsText),
            EventNames = Words(_eventNamesText),
            MinSeverity = _minSeverity,
            RouteAction = _routeAction,
        };
    }

    private static bool? Collect(string choice) => choice switch
    {
        "on" => true,
        "off" => false,
        _ => null,
    };

    private int? ParsePosition()
    {
        var text = _positionText.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        // Anything that is not a plain positive integer is not a position; 0 stays 0 so the builder's message names it.
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string[] Words(string text) =>
        text.Split([',', ' ', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Why the form cannot make a command yet, as a sentence; empty when it can.</summary>
    public string Problem
    {
        get
        {
            var inputs = ToInputs();

            // What the form knows and the command builder cannot: a profile the runtime does not have, a route that is not there.
            if (_positionText.Trim().Length > 0 && inputs.Position is not >= 1)
            {
                return "Position must be a positive integer.";
            }

            if (_profilesKnown && Operation.Operation is RedactionOperation.ProfileShow or RedactionOperation.ProfileRemove
                && inputs.ProfileName.Length > 0 && !_knownProfiles.Contains(inputs.ProfileName, StringComparer.Ordinal))
            {
                return $"There is no profile named '{inputs.ProfileName}'.";
            }

            return RedactionArgv.Problems(Operation.Operation, inputs) is { Count: > 0 } problems ? problems[0] : string.Empty;
        }
    }

    public bool IsValid => Problem.Length == 0;
}
