using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels.SetupResources;

/// <summary>
/// The webhooks editor: <c>setup webhook list --json</c> as a list, and Enable, Disable, Test, Show and Remove on the selected webhook (the TUI's
/// <c>SetupResourceEditorScreen("webhooks")</c>); Add opens the wizard on <c>add</c>. Show is the one verb that only reads - it is what Enter
/// does, as in the TUI - and it runs without a review, with the exact command printed beside what it said.
/// <list type="bullet">
///   <item><b>Test delivers a real message</b> (<c>setup webhook test</c> posts one synthetic event to the webhook's address), so it is reviewed, says
///     where it goes, and is never started by Enter. The CLI prints the webhook's address whole on its first line, and a chat webhook's secret is its
///     path: the runner cuts every address in the output to its host before a line is stored (<see cref="EndpointHost.ScrubLine"/>), so Activity and the
///     review's result show the host only.</item>
///   <item><b>Only global webhooks are listed.</b> <c>setup webhook list</c> without <c>--connector</c> is the top-level <c>webhooks</c> list; the
///     per-connector lists under <c>observability.connectors</c> are not here.</item>
/// </list>
/// </summary>
public sealed class WebhooksViewModel : SetupResourceViewModel
{
    private const string UnsafeNameReason = "This webhook's name has characters that cannot be passed to the CLI safely.";

    private static readonly SetupColumn[] ColumnList =
    [
        new("Name", "Name", 1.4, 110),
        new("Type", "Type", 0.8, 84, SetupColumnKind.Quiet),
        new("State", "State", 1.0, 120, SetupColumnKind.State),
        new("Min severity", "Severity", 0.9, 96, SetupColumnKind.Quiet),
        new("Events", "Events", 1.3, 110, SetupColumnKind.Quiet),
        new("Endpoint", "Endpoint", 1.8, 140, SetupColumnKind.Mono),
    ];

    public WebhooksViewModel(AppServices services)
        : base(services)
    {
    }

    public override SetupResource Resource => SetupResource.Webhooks;

    public override string Title => "Webhooks";

    public override string Subtitle =>
        "Slack, PagerDuty, Webex and signed generic webhooks. Test sends a real message to the webhook.";

    public override string Singular => "webhook";

    public override string Plural => "webhooks";

    public override IReadOnlyList<SetupColumn> Columns => ColumnList;

    public override bool Offers(SetupVerb verb) => true;

    public override string EmptyTitle => "No webhooks are configured";

    public override string EmptyDetail =>
        "defenseclaw setup webhook list returned an empty list, so no notifier is sent anything. Use Add to add one.";

    protected override string TipFor(SetupVerb verb) => verb switch
    {
        SetupVerb.Add => "Opens the setup wizard for Slack, PagerDuty, Webex or a generic webhook. The command is shown first.",
        SetupVerb.Enable => "Turn this webhook on. The command is shown first.",
        SetupVerb.Disable => "Turn this webhook off without deleting it. The command is shown first.",
        SetupVerb.Test => "Sends one synthetic test event to this webhook. It arrives as a real message. The command is shown first.",
        SetupVerb.Show => "Read this webhook with the CLI's show command. It only reads.",
        SetupVerb.Remove => "Delete this webhook from config.yaml. The command is shown first.",
        _ => string.Empty,
    };

    protected override bool TryParse(string stdout, out IReadOnlyList<SetupResourceRow> rows, out string error)
    {
        rows = Array.Empty<SetupResourceRow>();
        if (!NotifierWebhookParser.TryParse(stdout, out var parsed, out error))
        {
            return false;
        }

        rows = parsed.Select(ToRow).ToArray();
        return true;
    }

    protected override IReadOnlyList<string> ShowArgv(SetupResourceRow row) => SetupResourceArgv.ShowWebhook(row.Key);

    protected override bool TryParseShown(string stdout, SetupResourceRow row, out IReadOnlyList<SetupFact> facts, out string error)
    {
        facts = Array.Empty<SetupFact>();
        if (!NotifierWebhookParser.TryParseOne(stdout, out var webhook, out error) || webhook is null)
        {
            return false;
        }

        if (!string.Equals(webhook.Name, row.Key, StringComparison.Ordinal))
        {
            error = "The CLI answered for a different webhook than the one selected.";
            return false;
        }

        facts = FactsOf(webhook);
        return true;
    }

    internal static SetupResourceRow ToRow(NotifierWebhook w)
    {
        var cells = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = w.Name,
            ["Type"] = Dash(w.Type),
            ["State"] = w.State,
            ["Severity"] = Dash(w.MinSeverity),
            ["Events"] = w.EventsText,
            ["Endpoint"] = Dash(w.Endpoint),
        };

        var blocked = new Dictionary<SetupVerb, string>();
        if (!SetupResourceArgv.IsSafeName(w.Name))
        {
            blocked[SetupVerb.Show] = UnsafeNameReason;
            blocked[SetupVerb.Enable] = UnsafeNameReason;
            blocked[SetupVerb.Disable] = UnsafeNameReason;
            blocked[SetupVerb.Test] = UnsafeNameReason;
            blocked[SetupVerb.Remove] = UnsafeNameReason;
        }
        else if (w.Enabled)
        {
            blocked[SetupVerb.Enable] = $"“{w.Name}” is already enabled.";
        }
        else
        {
            blocked[SetupVerb.Disable] = $"“{w.Name}” is already disabled.";
        }

        return new SetupResourceRow(w.Name, cells, w.State, w.Enabled ? "Ok" : "Neutral", FactsOf(w), w, blocked);
    }

    private static IReadOnlyList<SetupFact> FactsOf(NotifierWebhook w)
    {
        var facts = new List<SetupFact>
        {
            new("Name", w.Name),
            new("Type", Dash(w.Type)),
            new("State", w.State),
            new("Endpoint", Dash(w.Endpoint), Mono: true),
            new("Minimum severity", Dash(w.MinSeverity)),
            new("Events", w.EventsText),
            new("Timeout", $"{w.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s"),
            new("Cooldown", w.CooldownText),
        };
        if (w.SecretEnv.Length > 0)
        {
            facts.Add(new SetupFact("Secret variable", w.SecretEnv + " (the value is never shown)", Mono: true));
        }

        if (w.RoomId.Length > 0)
        {
            facts.Add(new SetupFact("Room", w.RoomId, Mono: true));
        }

        return facts;
    }

    internal override SetupChangePlan Plan(SetupVerb verb, SetupResourceRow row)
    {
        var w = (NotifierWebhook)row.Source;
        var name = row.Title;
        var where = w.Endpoint.Length > 0 ? w.Endpoint : "its address";
        var warnings = new List<CommandReviewWarning>();

        switch (verb)
        {
            case SetupVerb.Enable:
                var on = SetupResourceArgv.Enable(Resource, row.Key);
                return new SetupChangePlan(
                    verb,
                    row,
                    on,
                    $"Enable webhook “{name}”?",
                    $"Sets enabled: true for this {w.Type} webhook in config.yaml. Events of severity {w.MinSeverity} and above ({w.EventsText}) are then delivered to {where}.",
                    "Enable webhook",
                    CommandTier.StateChanging,
                    ChangeTimeout,
                    CommandReview.RestartsGatewayFor(on),
                    warnings,
                    $"Enabled “{name}”.");

            case SetupVerb.Disable:
                var off = SetupResourceArgv.Disable(Resource, row.Key);
                return new SetupChangePlan(
                    verb,
                    row,
                    off,
                    $"Disable webhook “{name}”?",
                    $"Sets enabled: false for this webhook in config.yaml. Nothing is deleted. Nothing is delivered to {where} while it is off.",
                    "Disable webhook",
                    CommandTier.StateChanging,
                    ChangeTimeout,
                    CommandReview.RestartsGatewayFor(off),
                    warnings,
                    $"Disabled “{name}”.");

            case SetupVerb.Test:
                warnings.Add(new CommandReviewWarning(
                    "Sends a real message",
                    $"One synthetic test event is delivered to {where}, as a chat message or an incident depending on the type ({w.Type}). Each test has its own event id."));
                warnings.Add(new CommandReviewWarning(
                    "The address is not shown whole",
                    "The CLI prints the full webhook address, which holds the secret for a chat webhook. Only the host is kept, here and in Activity."));
                var test = SetupResourceArgv.Test(Resource, row.Key);
                var secret = w.SecretEnv.Length > 0
                    ? $" The secret comes from the variable {w.SecretEnv}, in the environment or .env. If it is not set, the test stops before it sends."
                    : string.Empty;
                return new SetupChangePlan(
                    verb,
                    row,
                    test,
                    $"Send a test event through webhook “{name}”?",
                    $"Delivers one synthetic event through this {w.Type} webhook, even if it is disabled.{secret}",
                    "Send test event",
                    CommandTier.StateChanging,
                    TestTimeout,
                    CommandReview.RestartsGatewayFor(test),
                    warnings,
                    $"“{name}” accepted the test event.");

            case SetupVerb.Remove:
                var remove = SetupResourceArgv.Remove(Resource, row.Key);
                return new SetupChangePlan(
                    verb,
                    row,
                    remove,
                    $"Remove webhook “{name}”?",
                    "Deletes this webhook from config.yaml. The variable that holds its secret stays. This cannot be undone here. Add it again with Add.",
                    "Remove webhook",
                    CommandTier.Destructive,
                    ChangeTimeout,
                    CommandReview.RestartsGatewayFor(remove),
                    warnings,
                    $"Removed “{name}”.");

            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, "The webhooks editor has no such change.");
        }
    }

    private static string Dash(string text) => text.Length == 0 ? "—" : text;
}
