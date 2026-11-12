using System.Globalization;
using System.Text.Json;

namespace DefenseClaw.Core.ClaudeCode;

/// <summary>
/// Whether Claude Cowork is protected, as the runtime itself judges it (1.0.8): the top-level <c>cowork</c> object of <c>defenseclaw status --json</c>
/// (<c>CoworkStatus.as_dict</c>, cli/defenseclaw/cowork.py). Cowork is not a connector: it is a surface of <c>claudecode</c>, and its plugin runs the
/// Claude Code connector's hooks, so the Claude Code mode, fail mode and rules apply to it.
/// <para>
/// The CLI counts Cowork as protected only once a Cowork hook event reached the gateway after the current plugin was built; a built plugin that was
/// never uploaded reads <c>cowork_no_events</c>. The object is absent when Cowork plays no part on this account (no plugin state and no Claude Desktop,
/// or a managed host) and when the CLI skipped live health because config.yaml is invalid: absent is "nothing to say", never a warning.
/// </para>
/// </summary>
/// <param name="Protected">The CLI's verdict; the only thing that may make the app say "protected".</param>
/// <param name="Detail">The CLI's sentence for the state, shown as written.</param>
/// <param name="Step">The CLI's next step (a command or an instruction); empty when protected.</param>
/// <param name="ReasonCode">One of the <see cref="CoworkReasonCodes"/>; empty when protected.</param>
/// <param name="LastEventAt">The newest Cowork event the CLI counted for this plugin build; null when none.</param>
/// <param name="PluginVersion">The plugin build <c>setup cowork</c> made (<c>1.0.&lt;timestamp&gt;</c>); null when none was built.</param>
/// <param name="PluginZip">Where the built plugin zip is; null when none was built.</param>
public sealed record CoworkProtection(
    bool Protected,
    string Detail,
    string Step,
    string ReasonCode,
    DateTimeOffset? LastEventAt,
    string? PluginVersion,
    string? PluginZip)
{
    /// <summary>The key of the object in <c>status --json</c>.</summary>
    public const string StatusKey = "cowork";

    /// <summary>No plugin was ever built, and Claude Desktop is installed: <c>setup cowork</c> is the step.</summary>
    public bool IsNotBuilt => !Protected && string.Equals(ReasonCode, CoworkReasonCodes.PluginNotBuilt, StringComparison.Ordinal);

    /// <summary>A plugin was built, but no Cowork event reached DefenseClaw since: it was not uploaded yet (or Claude has not loaded it).</summary>
    public bool AwaitsUpload => !Protected && string.Equals(ReasonCode, CoworkReasonCodes.NoEvents, StringComparison.Ordinal);

    /// <summary>The Claude Code connector, whose hooks the plugin runs, is off.</summary>
    public bool IsClaudeCodeOff => !Protected && string.Equals(ReasonCode, CoworkReasonCodes.ClaudeCodeOff, StringComparison.Ordinal);

    /// <summary>
    /// The state depends on the gateway (down, skipped Claude Code at start, authenticated status unreadable): the gateway's own rows say what to do,
    /// so these are not Cowork's to raise again.
    /// </summary>
    public bool IsGatewayCondition => !Protected && ReasonCode is CoworkReasonCodes.GatewayDown or CoworkReasonCodes.ClaudeCodeNotStarted or CoworkReasonCodes.HealthUnavailable;

    /// <summary>The label the app puts before the CLI's sentence when Cowork was never set up.</summary>
    public const string NotSetUpLabel = "Not set up";

    /// <summary>The CLI's own lead-in on a not-protected detail (cli/defenseclaw/cowork.py, 1.0.8); dropped after <see cref="NotSetUpLabel"/>, which says it already.</summary>
    private const string NotProtectedPrefix = "not protected: ";

    /// <summary>
    /// The never-set-up line: <c>Not set up: Claude Desktop is installed, but ...</c>. The CLI's sentence is kept as written, minus its leading
    /// <c>not protected: </c>; a sentence without that prefix follows the label unchanged, and an empty one leaves the label alone.
    /// </summary>
    public string NotSetUpText
    {
        get
        {
            var detail = Detail.StartsWith(NotProtectedPrefix, StringComparison.Ordinal) ? Detail[NotProtectedPrefix.Length..] : Detail;
            return detail.Length > 0 ? NotSetUpLabel + ": " + detail : NotSetUpLabel;
        }
    }

    /// <summary>The zip's folder, or null when no plugin was built.</summary>
    public string? PluginFolder => string.IsNullOrWhiteSpace(PluginZip) ? null : Path.GetDirectoryName(PluginZip);

    /// <summary>
    /// The <c>cowork</c> object of a parsed <c>status --json</c> root, or null when the key is absent or not an object. Tolerant: a missing or oddly typed
    /// member reads as empty (a non-bool <c>protected</c> reads as not protected), never an error.
    /// </summary>
    public static CoworkProtection? FromStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(StatusKey, out var cowork) ||
            cowork.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CoworkProtection(
            cowork.TryGetProperty("protected", out var flag) && flag.ValueKind == JsonValueKind.True,
            Text(cowork, "detail") ?? string.Empty,
            Text(cowork, "step") ?? string.Empty,
            Text(cowork, "reason_code") ?? string.Empty,
            Timestamp(Text(cowork, "last_event_at")),
            Text(cowork, "plugin_version") is { Length: > 0 } version ? version : null,
            Text(cowork, "plugin_zip") is { Length: > 0 } zip ? zip : null);
    }

    /// <summary>Parses <paramref name="json"/> (a whole <c>status --json</c> document) and reads its <c>cowork</c> object; null when absent.</summary>
    /// <exception cref="JsonException">Not JSON.</exception>
    public static CoworkProtection? FromStatusJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return FromStatus(document.RootElement);
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static DateTimeOffset? Timestamp(string? text) =>
        text is { Length: > 0 } &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)
            ? stamp
            : null;
}

/// <summary>The six <c>reason_code</c> values of the <c>cowork</c> status object (cli/defenseclaw/cowork.py, <c>cowork_status</c>, 1.0.8).</summary>
public static class CoworkReasonCodes
{
    /// <summary>Claude Desktop is installed, but no Cowork plugin was built. Step: <c>defenseclaw setup cowork</c>.</summary>
    public const string PluginNotBuilt = "cowork_plugin_not_built";

    /// <summary>The Claude Code connector is off. Step: <c>defenseclaw setup claude-code</c>.</summary>
    public const string ClaudeCodeOff = "cowork_claudecode_off";

    /// <summary>The gateway is not answering. Step: <c>defenseclaw-gateway start</c>.</summary>
    public const string GatewayDown = "cowork_gateway_down";

    /// <summary>The gateway skipped the Claude Code connector when it started. Step: <c>defenseclaw doctor</c>.</summary>
    public const string ClaudeCodeNotStarted = "cowork_claudecode_not_started";

    /// <summary>The gateway's authenticated status is unavailable. Step: <c>defenseclaw-gateway status</c>.</summary>
    public const string HealthUnavailable = "cowork_health_unavailable";

    /// <summary>A plugin was built, but no Cowork event reached DefenseClaw since. Step: upload the zip in Claude Desktop, then start a Cowork task.</summary>
    public const string NoEvents = "cowork_no_events";

    /// <summary>All six, in the CLI's order of evaluation.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        PluginNotBuilt, ClaudeCodeOff, GatewayDown, ClaudeCodeNotStarted, HealthUnavailable, NoEvents,
    };
}
