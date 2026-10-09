using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels.SetupResources;

/// <summary>
/// The observability destinations editor: <c>setup observability list --json</c> as a list, and Enable, Disable, Test and Remove on the selected
/// destination (the TUI's <c>SetupResourceEditorScreen("observability")</c>); Add opens the wizard on <c>add</c>. 0.8.10 has no
/// <c>setup observability show</c> - the TUI says "destination details are shown in the table" - so the details pane is Show here.
/// <list type="bullet">
///   <item><b>What cannot be changed is said, per row.</b> A destination the compiler adds itself (<c>local-sqlite</c>, <c>generated</c> in the list) is
///     not in config.yaml, so the CLI can neither disable nor remove it, and the buttons say so instead of failing after a review. A destination already in
///     the state asked for, and one that belongs to a local stack this PC cannot run, say that.</item>
///   <item><b>Test is only for a destination that delivers to a remote address.</b> The CLI refuses to connectivity-test local storage, the console and a
///     Prometheus listener; the button says so for those rows. The test is the CLI's default (a TCP and TLS handshake to each endpoint, nothing sent);
///     <c>--write-probe</c>, which sends one marked probe, is not offered.</item>
///   <item><b>Enabling a destination that sends content unredacted says so</b> in the review.</item>
/// </list>
/// </summary>
public sealed class ObservabilityDestinationsViewModel : SetupResourceViewModel
{
    private const string UnsafeNameReason = "This destination's name has characters that cannot be passed to the CLI safely, so the app will not act on it.";

    private static readonly SetupColumn[] ColumnList =
    [
        new("Name", "Name", 1.4, 110),
        new("Kind", "Kind", 0.8, 84, SetupColumnKind.Quiet),
        new("State", "State", 1.0, 128, SetupColumnKind.State),
        new("Signals", "Signals", 1.0, 100, SetupColumnKind.Quiet),
        new("Redaction", "Redaction", 1.3, 120, SetupColumnKind.Quiet),
        new("Endpoint", "Endpoint", 1.8, 140, SetupColumnKind.Mono),
    ];

    public ObservabilityDestinationsViewModel(AppServices services)
        : base(services)
    {
    }

    public override SetupResource Resource => SetupResource.Observability;

    public override string Title => "Observability destinations";

    public override string Subtitle =>
        "Where DefenseClaw sends its telemetry: each row is a destination in config.yaml. Test checks that a remote one answers. Every change is shown as the exact command first, and nothing runs until you confirm it.";

    public override string Singular => "destination";

    public override string Plural => "destinations";

    public override IReadOnlyList<SetupColumn> Columns => ColumnList;

    public override bool Offers(SetupVerb verb) => verb != SetupVerb.Show;

    public override string EmptyTitle => "No destinations are configured";

    public override string EmptyDetail =>
        "defenseclaw setup observability list answered with an empty list. Add a destination with the Add button; the setup wizard shows the exact command before anything is written.";

    protected override string TipFor(SetupVerb verb) => verb switch
    {
        SetupVerb.Add => "Open the setup wizard on add: pick a destination type and fill it in. The wizard shows the exact command before anything runs.",
        SetupVerb.Enable => "Turn this destination on. The command is shown first.",
        SetupVerb.Disable => "Turn this destination off without deleting it. The command is shown first.",
        SetupVerb.Test => "Connect to this destination to check that it answers. The command is shown first and nothing is sent until you confirm it.",
        SetupVerb.Remove => "Delete this destination from config.yaml. The command is shown first.",
        _ => string.Empty,
    };

    protected override bool TryParse(string stdout, out IReadOnlyList<SetupResourceRow> rows, out string error)
    {
        rows = Array.Empty<SetupResourceRow>();
        if (!ObservabilityDestinationParser.TryParse(stdout, out var parsed, out error))
        {
            return false;
        }

        rows = parsed.Select(ToRow).ToArray();
        return true;
    }

    internal static SetupResourceRow ToRow(ObservabilityDestination d)
    {
        var cells = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = d.Name,
            ["Kind"] = Dash(d.Kind),
            ["State"] = d.State,
            ["Signals"] = d.Signals.Count == 0 ? "none" : string.Join(", ", d.Signals),
            ["Redaction"] = Dash(d.Redaction),
            ["Endpoint"] = Dash(d.Endpoint),
        };

        var facts = new List<SetupFact>
        {
            new("Name", d.Name),
            new("Kind", Dash(d.Kind)),
            new("State", d.State),
            new("Sends", d.Signals.Count == 0 ? "no signals" : string.Join(", ", d.Signals)),
            new("Could send", d.Capabilities.Count == 0 ? "nothing" : string.Join(", ", d.Capabilities)),
            new("Routing", Dash(d.Policy)),
            new("Event buckets", d.BucketCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Redaction", Dash(d.Redaction)),
            new(d.IsLocal ? "Location" : "Endpoint", Dash(d.Endpoint), Mono: true),
        };
        if (d.Generated)
        {
            facts.Add(new SetupFact("Built in", d.IsMandatory ? "yes: mandatory, always on" : "yes: generated by DefenseClaw, not written in config.yaml"));
        }

        if (d.UnsupportedHere)
        {
            facts.Add(new SetupFact("This PC", "belongs to a local stack this operating system cannot run"));
        }

        var blocked = new Dictionary<SetupVerb, string>();
        if (!SetupResourceArgv.IsSafeName(d.Name))
        {
            blocked[SetupVerb.Enable] = UnsafeNameReason;
            blocked[SetupVerb.Disable] = UnsafeNameReason;
            blocked[SetupVerb.Test] = UnsafeNameReason;
            blocked[SetupVerb.Remove] = UnsafeNameReason;
        }
        else
        {
            if (d.Generated)
            {
                var generated = d.IsMandatory
                    ? $"{ObservabilityDestination.MandatoryName} is mandatory and always on: it cannot be disabled or removed."
                    : "DefenseClaw generates this destination; it is not a setting in config.yaml, so it cannot be enabled, disabled or removed here.";
                blocked[SetupVerb.Enable] = generated;
                blocked[SetupVerb.Disable] = generated;
                blocked[SetupVerb.Remove] = generated;
            }
            else if (d.UnsupportedHere)
            {
                blocked[SetupVerb.Enable] = "This destination belongs to a local stack this operating system cannot run, so it cannot be enabled.";
            }
            else if (d.Enabled)
            {
                blocked[SetupVerb.Enable] = $"“{d.Name}” is already enabled.";
            }
            else
            {
                blocked[SetupVerb.Disable] = $"“{d.Name}” is already disabled.";
            }

            if (d.IsLocal)
            {
                blocked[SetupVerb.Test] = $"A {d.Kind} destination stores or serves events on this PC. The CLI only tests destinations that deliver to a remote address, so there is nothing to connect to.";
            }
        }

        return new SetupResourceRow(
            d.Name,
            cells,
            d.State,
            d.UnsupportedHere ? "Warn" : d.Enabled ? "Ok" : "Neutral",
            facts,
            d,
            blocked);
    }

    internal override SetupChangePlan Plan(SetupVerb verb, SetupResourceRow row)
    {
        var d = (ObservabilityDestination)row.Source;
        var name = row.Title;
        var where = d.Endpoint.Length > 0 ? d.Endpoint : "its endpoint";
        var warnings = new List<CommandReviewWarning>();

        switch (verb)
        {
            case SetupVerb.Enable:
                if (d.SendsUnredacted)
                {
                    warnings.Add(new CommandReviewWarning(
                        "Content is not redacted",
                        $"Its redaction is “{d.Redaction}”: once enabled it receives content as recorded, which can include prompts, tool input and output, file paths and secrets."));
                }

                var argv = SetupResourceArgv.Enable(Resource, row.Key);
                return new SetupChangePlan(
                    verb,
                    row,
                    argv,
                    $"Enable destination “{name}”?",
                    $"Sets enabled: true for this {d.Kind} destination in config.yaml. From then on the events its routes select ({(d.Signals.Count == 0 ? "no signals yet" : string.Join(", ", d.Signals))}) are sent to {where}.",
                    "Enable destination",
                    CommandTier.StateChanging,
                    ChangeTimeout,
                    CommandReview.RestartsGatewayFor(argv),
                    warnings,
                    $"Enabled “{name}”.");

            case SetupVerb.Disable:
                var off = SetupResourceArgv.Disable(Resource, row.Key);
                return new SetupChangePlan(
                    verb,
                    row,
                    off,
                    $"Disable destination “{name}”?",
                    $"Sets enabled: false for this destination in config.yaml. Nothing is deleted and Enable turns it back on; events stop being sent to {where}.",
                    "Disable destination",
                    CommandTier.StateChanging,
                    ChangeTimeout,
                    CommandReview.RestartsGatewayFor(off),
                    warnings,
                    $"Disabled “{name}”.");

            case SetupVerb.Test:
                warnings.Add(new CommandReviewWarning(
                    "Contacts a live endpoint",
                    $"This opens a network connection to {where} now. The app runs the CLI's default test and never passes --write-probe, which would send one marked probe."));
                var test = SetupResourceArgv.Test(Resource, row.Key);
                return new SetupChangePlan(
                    verb,
                    row,
                    test,
                    $"Test destination “{name}”?",
                    "Checks that the destination answers (a TCP and TLS handshake to each of its endpoints) and that its credentials can be found. No event is sent, so it cannot tell whether the credentials are accepted. The attempt and its result are recorded locally as compliance activity; the configuration is not changed.",
                    "Run test",
                    CommandTier.StateChanging,
                    TestTimeout,
                    CommandReview.RestartsGatewayFor(test),
                    warnings,
                    $"“{name}” answered.");

            case SetupVerb.Remove:
                var remove = SetupResourceArgv.Remove(Resource, row.Key);
                return new SetupChangePlan(
                    verb,
                    row,
                    remove,
                    $"Remove destination “{name}”?",
                    $"Deletes this destination from config.yaml (observability.destinations). Anything stored for it in .env, such as its token, is left alone. The app cannot bring it back: add it again with Add.",
                    "Remove destination",
                    CommandTier.Destructive,
                    ChangeTimeout,
                    CommandReview.RestartsGatewayFor(remove),
                    warnings,
                    $"Removed “{name}”.");

            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, "The destinations editor has no such change.");
        }
    }

    private static string Dash(string text) => text.Length == 0 ? "—" : text;
}
