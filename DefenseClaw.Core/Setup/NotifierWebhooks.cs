using System.Text.Json;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Setup;

/// <summary>
/// One webhook of <c>defenseclaw setup webhook list --json</c> or <c>show --json</c> (0.8.10 <c>cmd_setup_webhook._view_to_dict</c>): a
/// Slack, PagerDuty, Webex or generic notifier. The CLI prints its address with the path, query and userinfo replaced (<c>redact_webhook_url</c>:
/// a chat webhook's secret is the path), and this keeps only the host of that: the row has <b>no member for an address</b>, only
/// <see cref="Endpoint"/>. The secret the webhook signs with is never printed, only the NAME of the variable that holds it
/// (<see cref="SecretEnv"/>).
/// </summary>
/// <param name="Name">The webhook's name; what every verb is given.</param>
/// <param name="Type"><c>slack</c>, <c>pagerduty</c>, <c>webex</c> or <c>generic</c>.</param>
/// <param name="Enabled">Whether events are delivered to it.</param>
/// <param name="Endpoint">The host (and port) it delivers to.</param>
/// <param name="SecretEnv">The name of the environment variable that holds its secret or routing key; empty for one that has none (a Slack webhook's secret is its address).</param>
/// <param name="RoomId">The Webex room; empty for the others.</param>
/// <param name="MinSeverity">The lowest severity it is sent (<c>INFO</c> ... <c>CRITICAL</c>), upper-case as the CLI prints it.</param>
/// <param name="Events">The event categories it is sent; empty means all of them.</param>
/// <param name="TimeoutSeconds">How long one delivery may take.</param>
/// <param name="CooldownSeconds">The same event is not sent again within this many seconds; null is the runtime's default (300), 0 is no limit.</param>
public sealed record NotifierWebhook(
    string Name,
    string Type,
    bool Enabled,
    string Endpoint,
    string SecretEnv,
    string RoomId,
    string MinSeverity,
    IReadOnlyList<string> Events,
    int TimeoutSeconds,
    int? CooldownSeconds)
{
    /// <summary>The CLI's own word for the state: <c>enabled</c> or <c>disabled</c>.</summary>
    public string State => Enabled ? "enabled" : "disabled";

    /// <summary>"all events" or the categories, comma separated.</summary>
    public string EventsText => Events.Count == 0 ? "all events" : string.Join(", ", Events);

    /// <summary>How the cooldown reads: the runtime's default, none, or a number of seconds.</summary>
    public string CooldownText => CooldownSeconds switch
    {
        null => "runtime default (300 s)",
        0 => "none: every matching event is delivered",
        { } seconds => $"{seconds} s",
    };
}

/// <summary>
/// Reads <c>defenseclaw setup webhook list --json</c> (an array) and <c>show --json</c> (one object). The same strictness as
/// <see cref="ObservabilityDestinationParser"/>: a row needs a name and an <c>enabled</c>, and no output is a failed read, never an empty list.
/// </summary>
public static class NotifierWebhookParser
{
    public static bool TryParse(string? text, out IReadOnlyList<NotifierWebhook> rows, out string error) =>
        SetupJson.TryReadRows(text, "The webhook list", Read, out rows, out error);

    /// <summary>Reads what <c>setup webhook show --json</c> printed.</summary>
    public static bool TryParseOne(string? text, out NotifierWebhook? webhook, out string error) =>
        SetupJson.TryReadObject(text, "The webhook", Read, out webhook, out error);

    private static NotifierWebhook Read(JsonElement element) => new(
        Name: SetupJson.RequiredName(element, "name"),
        Type: DisplayNames.Visible(SetupJson.StringOf(element, "type").Trim().ToLowerInvariant()),
        Enabled: SetupJson.RequiredFlag(element, "enabled"),
        Endpoint: EndpointDisplay.Host(SetupJson.StringOf(element, "url")),
        SecretEnv: DisplayNames.Visible(SetupJson.StringOf(element, "secret_env").Trim()),
        RoomId: DisplayNames.Visible(SetupJson.StringOf(element, "room_id").Trim()),
        MinSeverity: DisplayNames.Visible(SetupJson.StringOf(element, "min_severity").Trim().ToUpperInvariant()),
        Events: SetupJson.Strings(element, "events").Select(DisplayNames.Visible).ToArray(),
        TimeoutSeconds: Math.Max(0, SetupJson.Number(element, "timeout_seconds")),
        CooldownSeconds: SetupJson.OptionalNumber(element, "cooldown_seconds"));
}
