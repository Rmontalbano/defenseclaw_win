using System.Globalization;

namespace DefenseClaw.Core.Redaction;

/// <summary>One <c>--field CLASS=MODE</c> of a custom profile. <see cref="Mode"/> may be <see cref="RedactionVocabulary.InheritMode"/>.</summary>
public sealed record RedactionFieldMode(string Class, string Mode);

/// <summary>
/// Everything an operation can ask for, as the editor's form holds it. An operation reads the members it names
/// (<see cref="RedactionOperationInfo.Fields"/>) and ignores the rest, so one record serves all 21. Empty text and an empty list mean
/// "not set". Nothing here is a secret: every member is a word from <see cref="RedactionVocabulary"/> or a name.
/// </summary>
public sealed record RedactionInputs
{
    /// <summary>A profile name; empty when none is chosen.</summary>
    public string Profile { get; init; } = string.Empty;

    /// <summary>Bucket set: drop the bucket's own profile (<c>--inherit-profile</c>).</summary>
    public bool InheritProfile { get; init; }

    public string Bucket { get; init; } = string.Empty;

    public string Destination { get; init; } = string.Empty;

    public string RouteName { get; init; } = string.Empty;

    /// <summary>One-based; null for "last" (<c>route add</c>).</summary>
    public int? Position { get; init; }

    public IReadOnlyList<string> Signals { get; init; } = [];

    /// <summary>The buckets of a send policy or a route, or just <see cref="RedactionVocabulary.AllBuckets"/>.</summary>
    public IReadOnlyList<string> Buckets { get; init; } = [];

    /// <summary>Collection of logs: null leaves it alone, true is <c>--logs</c>, false <c>--no-logs</c>.</summary>
    public bool? CollectLogs { get; init; }

    public bool? CollectTraces { get; init; }

    public bool? CollectMetrics { get; init; }

    /// <summary>The custom profile <c>profile show|set|remove</c> acts on.</summary>
    public string ProfileName { get; init; } = string.Empty;

    /// <summary>True when <see cref="ProfileName"/> is not in the profile list yet: creating one needs <see cref="Extends"/>.</summary>
    public bool IsNewProfile { get; init; }

    public string Extends { get; init; } = string.Empty;

    public IReadOnlyList<string> Detectors { get; init; } = [];

    public IReadOnlyList<RedactionFieldMode> FieldModes { get; init; } = [];

    public string ReplaceWith { get; init; } = string.Empty;

    public IReadOnlyList<string> Sources { get; init; } = [];

    public IReadOnlyList<string> Connectors { get; init; } = [];

    public IReadOnlyList<string> ProducerActions { get; init; } = [];

    public IReadOnlyList<string> EventNames { get; init; } = [];

    /// <summary>One of <see cref="RedactionVocabulary.Severities"/>, or empty for any severity.</summary>
    public string MinSeverity { get; init; } = string.Empty;

    /// <summary><c>send</c> or <c>drop</c>.</summary>
    public string RouteAction { get; init; } = "send";
}

/// <summary>
/// The command lines of <c>defenseclaw setup redaction</c>, in one place so the editor, its tests and the capture agree on them. Each flag
/// was checked against <c>cmd_setup_redaction.py</c> and the pinned command tree (<c>Fixtures/runtime-95159fd/cli/cli-tree.json</c>).
/// <para>
/// <b>Three shapes.</b> A read is the command with <c>--json</c> where it has one. A <b>preview</b> ends <c>--json --dry-run</c>: the CLI
/// computes the effective plan before and after, prints the difference and writes nothing. An <b>apply</b> ends
/// <c>--yes --json --restart</c> or <c>--no-restart</c>: it asks nothing, writes <c>config.yaml</c> after saving a backup, and checks that the
/// plan it wrote is the plan it showed. A preview never carries <c>--yes</c>, <c>--restart</c> or <c>--no-restart</c> (a preview has no restart
/// to choose, and with no <c>--yes</c> the CLI would ask rather than write if <c>--dry-run</c> were ever ignored); <see cref="IsPreview"/> is the
/// test the view-model applies before it runs one.
/// </para>
/// <para>
/// <b>Why that tail.</b> The classifier (<see cref="Cli.CommandTiers"/>) reads <c>--dry-run</c> as "a preview" only as a standalone flag, and
/// whether it is standalone depends on the token before it: <c>--json</c> is a known switch, an option such as <c>--logs</c> is not. Ending every
/// preview with <c>--json --dry-run</c> makes it read-only whatever the options before it are.
/// </para>
/// <para>
/// <b>Names.</b> A profile, destination or route name is a positional argument, so it must be a <see cref="RedactionVocabulary.IsStableName"/>:
/// it cannot start with <c>-</c> (and so read as an option) or carry a space or a character the CLI re-expands on Windows. Selector values are
/// held to <see cref="RedactionVocabulary.IsSelectorValue"/>.
/// </para>
/// </summary>
public static class RedactionArgv
{
    private const string Setup = "setup";
    private const string Redaction = "redaction";

    /// <summary>The tail of a preview.</summary>
    private static readonly string[] PreviewTail = ["--json", "--dry-run"];

    // ------------------------------------------------------------------ validation

    /// <summary>
    /// Why the inputs cannot make a command for <paramref name="operation"/>, as sentences for the form; empty when they can. Checked again
    /// by <see cref="Read"/>, <see cref="Preview"/> and <see cref="Apply"/>, so a command line is never built from inputs this refuses.
    /// </summary>
    public static IReadOnlyList<string> Problems(RedactionOperation operation, RedactionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var problems = new List<string>();

        switch (operation)
        {
            case RedactionOperation.ApplyEverywhere:
            case RedactionOperation.ApplyDefaults:
                RequireProfile(problems, inputs.Profile, "Choose a profile.");
                break;

            case RedactionOperation.DefaultsSet:
                OptionalProfile(problems, inputs.Profile);
                if (inputs.Profile.Length == 0 && !HasCollect(inputs))
                {
                    problems.Add("Select at least one setting to change.");
                }

                break;

            case RedactionOperation.BucketSet:
                RequireBucket(problems, inputs.Bucket);
                OptionalProfile(problems, inputs.Profile);
                if (inputs.Profile.Length > 0 && inputs.InheritProfile)
                {
                    problems.Add("Choose a profile or give the default back, not both.");
                }
                else if (inputs.Profile.Length == 0 && !inputs.InheritProfile && !HasCollect(inputs))
                {
                    problems.Add("Select at least one setting to change.");
                }

                break;

            case RedactionOperation.BucketReset:
                RequireBucket(problems, inputs.Bucket);
                break;

            case RedactionOperation.ProfileShow:
                RequireProfile(problems, inputs.ProfileName, "Choose a profile.");
                break;

            case RedactionOperation.ProfileSet:
                ProfileSetProblems(problems, inputs);
                break;

            case RedactionOperation.ProfileRemove:
                if (!RedactionVocabulary.IsStableName(inputs.ProfileName))
                {
                    problems.Add("Choose the custom profile to remove.");
                }
                else if (RedactionVocabulary.BuiltInProfiles.Contains(inputs.ProfileName))
                {
                    problems.Add("A built-in profile cannot be removed.");
                }

                if (inputs.ReplaceWith.Length > 0)
                {
                    if (!RedactionVocabulary.IsStableName(inputs.ReplaceWith))
                    {
                        problems.Add("The replacement profile has a name that cannot be passed to the CLI safely.");
                    }
                    else if (string.Equals(inputs.ReplaceWith, inputs.ProfileName, StringComparison.Ordinal))
                    {
                        problems.Add("The replacement must be a different profile.");
                    }
                }

                break;

            case RedactionOperation.DestinationShow:
                RequireDestination(problems, inputs.Destination, configurable: false);
                break;

            case RedactionOperation.DestinationSend:
                RequireDestination(problems, inputs.Destination, configurable: true);
                RequireSignals(problems, inputs.Signals);
                RequireBuckets(problems, inputs.Buckets, "Select at least one bucket or *.");
                OptionalProfile(problems, inputs.Profile);
                break;

            case RedactionOperation.DestinationInherit:
            case RedactionOperation.RouteList:
                RequireDestination(problems, inputs.Destination, configurable: true);
                break;

            case RedactionOperation.RouteAdd:
            case RedactionOperation.RouteSet:
                RequireDestination(problems, inputs.Destination, configurable: true);
                RequireRouteName(problems, inputs.RouteName, creating: operation == RedactionOperation.RouteAdd);
                if (operation == RedactionOperation.RouteAdd && inputs.Position is < 1)
                {
                    problems.Add("Position must be a positive integer.");
                }

                RequireSignals(problems, inputs.Signals);
                if (inputs.Buckets.Count > 0)
                {
                    RequireBuckets(problems, inputs.Buckets, "Select at least one bucket or *.");
                }

                RouteFilterProblems(problems, inputs);
                OptionalProfile(problems, inputs.Profile);
                break;

            case RedactionOperation.RouteMove:
                RequireDestination(problems, inputs.Destination, configurable: true);
                RequireRouteName(problems, inputs.RouteName, creating: false);
                if (inputs.Position is not >= 1)
                {
                    problems.Add("Position must be a positive integer.");
                }

                break;

            case RedactionOperation.RouteRemove:
                RequireDestination(problems, inputs.Destination, configurable: true);
                RequireRouteName(problems, inputs.RouteName, creating: false);
                break;
        }

        return problems;
    }

    private static bool HasCollect(RedactionInputs i) => i.CollectLogs is not null || i.CollectTraces is not null || i.CollectMetrics is not null;

    private static void RequireProfile(List<string> problems, string profile, string whenMissing)
    {
        if (profile.Length == 0)
        {
            problems.Add(whenMissing);
        }
        else if (!RedactionVocabulary.IsStableName(profile))
        {
            problems.Add("That profile's name cannot be passed to the CLI safely: use lower-case letters, digits, - and _.");
        }
    }

    private static void OptionalProfile(List<string> problems, string profile)
    {
        if (profile.Length > 0 && !RedactionVocabulary.IsStableName(profile))
        {
            problems.Add("That profile's name cannot be passed to the CLI safely: use lower-case letters, digits, - and _.");
        }
    }

    private static void RequireBucket(List<string> problems, string bucket)
    {
        if (!RedactionVocabulary.Buckets.Contains(bucket))
        {
            problems.Add("Choose a bucket.");
        }
    }

    private static void RequireDestination(List<string> problems, string destination, bool configurable)
    {
        if (destination.Length == 0)
        {
            problems.Add("Choose a destination.");
        }
        else if (!RedactionVocabulary.IsStableName(destination))
        {
            problems.Add("That destination's name cannot be passed to the CLI safely.");
        }
        else if (configurable && RedactionVocabulary.IsGeneratedDestination(destination))
        {
            problems.Add($"The built-in destination '{destination}' is generated and read-only: choose a destination you configured.");
        }
    }

    private static void RequireRouteName(List<string> problems, string name, bool creating)
    {
        if (name.Length == 0)
        {
            problems.Add(creating ? "Enter a route name." : "Choose a route.");
        }
        else if (!RedactionVocabulary.IsStableName(name))
        {
            problems.Add(creating
                ? "A route name uses lower-case letters, digits, - and _, and starts with a letter or digit."
                : "That route's name cannot be passed to the CLI safely.");
        }
    }

    private static void RequireSignals(List<string> problems, IReadOnlyList<string> signals)
    {
        if (signals.Count == 0)
        {
            problems.Add("Select at least one signal.");
        }
        else if (signals.Any(s => !RedactionVocabulary.Signals.Contains(s)))
        {
            problems.Add("A signal is logs, traces or metrics.");
        }
    }

    private static void RequireBuckets(List<string> problems, IReadOnlyList<string> buckets, string whenMissing)
    {
        if (buckets.Count == 0)
        {
            problems.Add(whenMissing);
        }
        else if (buckets.Contains(RedactionVocabulary.AllBuckets) && buckets.Count != 1)
        {
            problems.Add("All buckets (*) cannot be combined with a named bucket.");
        }
        else if (buckets.Any(b => b != RedactionVocabulary.AllBuckets && !RedactionVocabulary.Buckets.Contains(b)))
        {
            problems.Add("A bucket is one of the catalog's fourteen, or * for all of them.");
        }
    }

    private static void RouteFilterProblems(List<string> problems, RedactionInputs inputs)
    {
        if (!RedactionVocabulary.RouteActions.Contains(inputs.RouteAction))
        {
            problems.Add("A route sends or drops what it matches.");
        }

        if (inputs.MinSeverity.Length > 0 && !RedactionVocabulary.Severities.Contains(inputs.MinSeverity))
        {
            problems.Add("The minimum severity is INFO, LOW, MEDIUM, HIGH or CRITICAL.");
        }

        var lists = new[] { inputs.Sources, inputs.Connectors, inputs.ProducerActions, inputs.EventNames };
        if (lists.SelectMany(static l => l).Any(v => !RedactionVocabulary.IsSelectorValue(v)))
        {
            problems.Add("Selector values have no spaces, do not start with -, and avoid * ? [ % $ ~ (the CLI expands those on Windows).");
        }
    }

    private static void ProfileSetProblems(List<string> problems, RedactionInputs inputs)
    {
        if (inputs.ProfileName.Length == 0)
        {
            problems.Add("Enter a profile name.");
        }
        else if (RedactionVocabulary.BuiltInProfiles.Contains(inputs.ProfileName))
        {
            problems.Add("A built-in profile cannot be edited: give the custom profile a name of its own.");
        }
        else if (!RedactionVocabulary.IsStableName(inputs.ProfileName))
        {
            problems.Add("A profile name uses lower-case letters, digits, - and _, and starts with a letter or digit.");
        }

        if (inputs.Extends.Length > 0 && !RedactionVocabulary.CustomProfileBases.Contains(inputs.Extends))
        {
            problems.Add("A custom profile starts from sensitive, content or strict.");
        }
        else if (inputs.Extends.Length == 0 && inputs.IsNewProfile)
        {
            problems.Add("Choose the built-in profile the new profile starts from.");
        }

        if (inputs.Detectors.Any(d => !RedactionVocabulary.DetectorGroups.Contains(d)))
        {
            problems.Add("A detector group is pii, credentials or secrets.");
        }

        foreach (var field in inputs.FieldModes)
        {
            if (!RedactionVocabulary.FieldClasses.Contains(field.Class))
            {
                problems.Add($"'{field.Class}' is not a field class.");
            }
            else if (field.Mode != RedactionVocabulary.InheritMode && !RedactionVocabulary.FieldModes.Contains(field.Mode))
            {
                problems.Add($"'{field.Mode}' is not a mode for the {field.Class} field.");
            }
        }

        if (inputs.Extends.Length == 0 && inputs.Detectors.Count == 0 && inputs.FieldModes.Count == 0)
        {
            problems.Add("Choose what to set: the profile it starts from, detector groups or a field mode.");
        }
    }

    // ------------------------------------------------------------------ building

    /// <summary>The command of a read operation (<c>status --json</c>, <c>route list DEST --json</c>, ...).</summary>
    /// <exception cref="InvalidOperationException"><paramref name="operation"/> changes configuration: it has a <see cref="Preview"/> and an <see cref="Apply"/>.</exception>
    /// <exception cref="ArgumentException">The inputs have a <see cref="Problems"/>.</exception>
    public static string[] Read(RedactionOperation operation, RedactionInputs inputs)
    {
        var info = RedactionOperations.Info(operation);
        if (info.IsMutation)
        {
            throw new InvalidOperationException($"'{info.Title}' changes configuration: it has a preview and a reviewed apply, not a read.");
        }

        var body = Body(operation, inputs);
        if (info.Json)
        {
            body.Add("--json");
        }

        return body.ToArray();
    }

    /// <summary>
    /// The preview of a mutating operation: the same command with <c>--json --dry-run</c> at the end. Never <c>--yes</c>, <c>--restart</c> or
    /// <c>--no-restart</c>.
    /// </summary>
    public static string[] Preview(RedactionOperation operation, RedactionInputs inputs)
    {
        RequireMutation(operation);
        var body = Body(operation, inputs);
        body.AddRange(PreviewTail);
        return body.ToArray();
    }

    /// <summary>
    /// The apply of a mutating operation: <c>--yes --json</c> and an explicit <c>--restart</c> or <c>--no-restart</c>. Only a reviewed apply
    /// is ever run; the CLI's own default (no restart) is not left to decide.
    /// </summary>
    public static string[] Apply(RedactionOperation operation, RedactionInputs inputs, bool restart)
    {
        RequireMutation(operation);
        var body = Body(operation, inputs);
        body.Add("--yes");
        body.Add("--json");
        body.Add(restart ? "--restart" : "--no-restart");
        return body.ToArray();
    }

    private static void RequireMutation(RedactionOperation operation)
    {
        var info = RedactionOperations.Info(operation);
        if (!info.IsMutation)
        {
            throw new InvalidOperationException($"'{info.Title}' only reads: it has no preview and nothing to apply.");
        }
    }

    private static List<string> Body(RedactionOperation operation, RedactionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (Problems(operation, inputs) is { Count: > 0 } problems)
        {
            throw new ArgumentException(problems[0], nameof(inputs));
        }

        var argv = new List<string> { Setup, Redaction };
        argv.AddRange(RedactionOperations.Info(operation).Path);

        switch (operation)
        {
            case RedactionOperation.ApplyEverywhere:
                argv.AddRange(["--scope", "all-configurable", "--profile", inputs.Profile]);
                break;

            case RedactionOperation.ApplyDefaults:
                argv.AddRange(["--scope", "defaults", "--profile", inputs.Profile]);
                break;

            case RedactionOperation.DefaultsSet:
                Option(argv, "--profile", inputs.Profile);
                CollectFlags(argv, inputs);
                break;

            case RedactionOperation.BucketSet:
                argv.Add(inputs.Bucket);
                if (inputs.InheritProfile)
                {
                    argv.Add("--inherit-profile");
                }
                else
                {
                    Option(argv, "--profile", inputs.Profile);
                }

                CollectFlags(argv, inputs);
                break;

            case RedactionOperation.BucketReset:
                argv.Add(inputs.Bucket);
                break;

            case RedactionOperation.ProfileShow:
                argv.Add(inputs.ProfileName);
                break;

            case RedactionOperation.ProfileSet:
                argv.Add(inputs.ProfileName);
                Option(argv, "--extends", inputs.Extends);
                Repeated(argv, "--detector", inputs.Detectors);
                foreach (var field in inputs.FieldModes)
                {
                    argv.Add("--field");
                    argv.Add(field.Class + "=" + field.Mode);
                }

                break;

            case RedactionOperation.ProfileRemove:
                argv.Add(inputs.ProfileName);
                Option(argv, "--replace-with", inputs.ReplaceWith);
                break;

            case RedactionOperation.DestinationShow:
            case RedactionOperation.DestinationInherit:
            case RedactionOperation.RouteList:
                argv.Add(inputs.Destination);
                break;

            case RedactionOperation.DestinationSend:
                argv.Add(inputs.Destination);
                Repeated(argv, "--signal", inputs.Signals);
                Repeated(argv, "--bucket", inputs.Buckets);
                Option(argv, "--profile", inputs.Profile);
                break;

            case RedactionOperation.RouteAdd:
                argv.Add(inputs.Destination);
                argv.Add(inputs.RouteName);
                if (inputs.Position is { } position)
                {
                    argv.Add("--position");
                    argv.Add(position.ToString(CultureInfo.InvariantCulture));
                }

                RouteFlags(argv, inputs);
                break;

            case RedactionOperation.RouteSet:
                argv.Add(inputs.Destination);
                argv.Add(inputs.RouteName);
                RouteFlags(argv, inputs);
                break;

            case RedactionOperation.RouteMove:
                argv.Add(inputs.Destination);
                argv.Add(inputs.RouteName);
                argv.Add("--position");
                argv.Add(inputs.Position!.Value.ToString(CultureInfo.InvariantCulture));
                break;

            case RedactionOperation.RouteRemove:
                argv.Add(inputs.Destination);
                argv.Add(inputs.RouteName);
                break;
        }

        return argv;
    }

    private static void Option(List<string> argv, string name, string value)
    {
        if (value.Length > 0)
        {
            argv.Add(name);
            argv.Add(value);
        }
    }

    private static void Repeated(List<string> argv, string name, IEnumerable<string> values)
    {
        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            argv.Add(name);
            argv.Add(value);
        }
    }

    private static void CollectFlags(List<string> argv, RedactionInputs inputs)
    {
        Flag(argv, "logs", inputs.CollectLogs);
        Flag(argv, "traces", inputs.CollectTraces);
        Flag(argv, "metrics", inputs.CollectMetrics);
    }

    private static void Flag(List<string> argv, string signal, bool? collect)
    {
        if (collect is { } on)
        {
            argv.Add(on ? "--" + signal : "--no-" + signal);
        }
    }

    private static void RouteFlags(List<string> argv, RedactionInputs inputs)
    {
        Repeated(argv, "--signal", inputs.Signals);
        Repeated(argv, "--bucket", inputs.Buckets);
        Repeated(argv, "--source", inputs.Sources);
        Repeated(argv, "--connector", inputs.Connectors);
        Repeated(argv, "--producer-action", inputs.ProducerActions);
        Repeated(argv, "--event-name", inputs.EventNames);
        Option(argv, "--min-severity", inputs.MinSeverity);
        argv.Add("--route-action");
        argv.Add(inputs.RouteAction);

        // A profile redacts what is sent, and only logs and traces carry content: it means nothing on a drop route or a metrics-only one.
        if (inputs.RouteAction == "send" && inputs.Signals.Any(RedactionVocabulary.ContentSignals.Contains))
        {
            Option(argv, "--profile", inputs.Profile);
        }
    }

    // ------------------------------------------------------------------ recognising a command line

    /// <summary>
    /// True when the operation sets the profile <c>none</c> (no redaction) somewhere: <c>remove-all</c>, or a choice of <c>none</c> on
    /// <c>apply</c>, <c>defaults set</c>, <c>bucket set</c>, a send policy, or a route that sends logs or traces (the only route a profile
    /// means anything on). A review asks for a second, explicit confirmation of these even when no leg changes today, because what follows the
    /// profile later (a bucket or a destination added afterwards) will send raw content too.
    /// </summary>
    public static bool SetsNoRedaction(RedactionOperation operation, RedactionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var none = string.Equals(inputs.Profile, RedactionVocabulary.NoRedaction, StringComparison.Ordinal);
        return operation switch
        {
            RedactionOperation.RemoveAll => true,
            RedactionOperation.ApplyEverywhere or RedactionOperation.ApplyDefaults or RedactionOperation.DefaultsSet
                or RedactionOperation.BucketSet or RedactionOperation.DestinationSend => none,
            RedactionOperation.RouteAdd or RedactionOperation.RouteSet =>
                none && string.Equals(inputs.RouteAction, "send", StringComparison.Ordinal) && inputs.Signals.Any(RedactionVocabulary.ContentSignals.Contains),
            _ => false,
        };
    }

    /// <summary>True for a token that restarts the gateway or says it should not: <c>--restart</c> and <c>--no-restart</c>.</summary>
    public static bool IsRestartFlag(string token) =>
        string.Equals(token, "--restart", StringComparison.Ordinal) || string.Equals(token, "--no-restart", StringComparison.Ordinal);

    /// <summary>The operation a <c>setup redaction ...</c> command line is, or null when it is not one of the 21.</summary>
    public static RedactionOperation? Identify(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count < 3 || argv[0] != Setup || argv[1] != Redaction)
        {
            return null;
        }

        foreach (var info in RedactionOperations.All)
        {
            var path = info.Path;
            if (argv.Count < 2 + path.Count)
            {
                continue;
            }

            var matches = true;
            for (var k = 0; k < path.Count; k++)
            {
                matches &= string.Equals(argv[2 + k], path[k], StringComparison.Ordinal);
            }

            if (!matches)
            {
                continue;
            }

            if (info.Operation is RedactionOperation.ApplyEverywhere or RedactionOperation.ApplyDefaults)
            {
                var scope = ValueOf(argv, "--scope");
                if (info.Operation == RedactionOperation.ApplyEverywhere ? scope == "all-configurable" : scope == "defaults")
                {
                    return info.Operation;
                }

                continue;
            }

            return info.Operation;
        }

        return null;
    }

    /// <summary>
    /// True for exactly the command line <see cref="Read"/> builds for a read operation and nothing else. The view-model starts a read
    /// without a review only through this test: a command that changes configuration is never on it.
    /// </summary>
    public static bool IsRead(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (Identify(argv) is not { } operation || RedactionOperations.Info(operation).IsMutation)
        {
            return false;
        }

        return operation switch
        {
            RedactionOperation.Status => argv.Count == 4 && argv[3] == "--json",
            RedactionOperation.BucketList => argv.Count == 4,
            RedactionOperation.ProfileList => argv.Count == 5 && argv[4] == "--json",
            RedactionOperation.ProfileShow => argv.Count == 6 && RedactionVocabulary.IsStableName(argv[4]) && argv[5] == "--json",
            RedactionOperation.DestinationShow => argv.Count == 5 && RedactionVocabulary.IsStableName(argv[4]),
            RedactionOperation.RouteList => argv.Count == 6 && RedactionVocabulary.IsStableName(argv[4]) && argv[5] == "--json",
            _ => false,
        };
    }

    /// <summary>
    /// True for a preview: a mutating operation that ends <c>--json --dry-run</c> and has no <c>--yes</c>, <c>--restart</c> or
    /// <c>--no-restart</c> anywhere in it. The view-model refuses to start anything else as a preview, so a preview cannot write
    /// <c>config.yaml</c> and cannot restart the gateway.
    /// </summary>
    public static bool IsPreview(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (Identify(argv) is not { } operation || !RedactionOperations.Info(operation).IsMutation)
        {
            return false;
        }

        if (argv.Count < 5 || argv[^1] != "--dry-run" || argv[^2] != "--json")
        {
            return false;
        }

        for (var i = 2; i < argv.Count - 2; i++)
        {
            if (argv[i] is "--yes" or "--dry-run" or "--json" || IsRestartFlag(argv[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ValueOf(IReadOnlyList<string> argv, string option)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (argv[i] == option)
            {
                return argv[i + 1];
            }
        }

        return null;
    }
}
