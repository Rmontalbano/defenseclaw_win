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
    private const string UnsafeNameReason = "This webhook's name has characters that cannot be passed to the CLI safely, so the app will not act on it.";

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
        "Chat and incident notifiers: Slack, PagerDuty, Webex and signed generic webhooks. Test delivers a real message to the webhook. Every change is shown as the exact command first, and nothing runs until you confirm it.";

    public override string Singular => "webhook";

    public override string Plural => "webhooks";

    public override IReadOnlyList<SetupColumn> Columns => ColumnList;

    public override bool Offers(SetupVerb verb) => true;

    public override string EmptyTitle => "No webhooks are configured";

    public override string EmptyDetail =>
        "defenseclaw setup webhook list answered with an empty list: no notifier is sent anything. Add one with the Add button; the setup wizard shows the exact command before anything is written.";

    protected override string TipFor(SetupVerb verb) => verb switch
    {
        SetupVerb.Add => "Open the setup wizard on add: pick Slack, PagerDuty, Webex or a generic webhook and fill it in. The wizard shows the exact command before anything runs.",
        SetupVerb.Enable => "Turn this webhook on. The command is shown first.",
        SetupVerb.Disable => "Turn this webhook off without deleting it. The command is shown first.",
        SetupVerb.Test => "Deliver one synthetic test event to this webhook: a real message. The command is shown first and nothing is sent until you confirm it.",
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
            error = "The CLI answered for a different webhook than the one selected, so nothing was shown.";
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
                    $"Sets enabled: true for this {w.Type} webhook in config.yaml. From then on events of severity {w.MinSeverity} and above ({w.EventsText}) are delivered to {where}.",
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
                    $"Sets enabled: false for this webhook in config.yaml. Nothing is deleted and Enable turns it back on; nothing is delivered to {where} meanwhile.",
                    "Disable webhook",
                    CommandTier.StateChanging,
                    ChangeTimeout,
                    CommandReview.RestartsGatewayFor(off),
                    warnings,
                    $"Disabled “{name}”.");

            case SetupVerb.Test:
                warnings.Add(new CommandReviewWarning(
                    "Sends a real message",
                    $"One synthetic test event is delivered to {where}: a message in the chat, or an incident, depending on the type ({w.Type}). Each test has its own event id, so the receiver does not drop a repeat as a duplicate."));
                warnings.Add(new CommandReviewWarning(
                    "The address is not shown whole",
                    "The CLI prints the webhook's full address on its first line, and for a chat webhook the secret is in it. This app keeps only the host of every address in the output, here and in Activity."));
                var test = SetupResourceArgv.Test(Resource, row.Key);
                var secret = w.SecretEnv.Length > 0
                    ? $" It takes the secret from the variable {w.SecretEnv} (the environment, or .env); if that is not set the test stops before it sends."
                    : string.Empty;
                return new SetupChangePlan(
                    verb,
                    row,
                    test,
                    $"Send a test event through webhook “{name}”?",
                    $"Delivers one synthetic event through this {w.Type} webhook, even if it is disabled.{secret} The configuration is not changed.",
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
                    "Deletes this webhook from config.yaml (the top-level webhooks list). The variable that holds its secret is left alone. The app cannot bring it back: add it again with Add.",
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
