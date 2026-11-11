using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>Why the gateway did not start a configured connector.</summary>
public enum ConnectorSkipKind
{
    /// <summary>The agent is not installed for this account (1.0.7): reinstall it and restart the gateway, or remove the connector.</summary>
    AgentNotInstalled,

    /// <summary>The agent's own settings refuse DefenseClaw's hooks (Claude Code <c>disableAllHooks</c>, Codex <c>[features] hooks = false</c>).</summary>
    RefusedByAgentSettings,

    /// <summary>The connector's setup failed when the gateway started.</summary>
    SetupFailed,
}

/// <summary>A configured connector the gateway did not start, so it is not enforced.</summary>
/// <param name="Name">The connector's lower-case wire name.</param>
/// <param name="Kind">Why it was not started.</param>
/// <param name="Reason">The gateway's sentence (or the CLI's own text when the gateway gave none): single line, at most <see cref="ConnectorStartHealth.MaxReasonLength"/> characters.</param>
public sealed record ConnectorSkip(string Name, ConnectorSkipKind Kind, string Reason);

/// <summary>
/// Reads the connectors the gateway did not start out of <c>/health</c> <c>guardrail.details</c> (<c>connectors_not_started</c>,
/// <c>connectors_refused_by_agent_settings</c>, <c>connectors_not_started_reasons</c>; the CLI's <c>status</c> words are cmd_status.py). A skipped connector
/// is not in <c>connectors[]</c> at all. Pure and never throws: anything malformed is dropped.
/// </summary>
public static class ConnectorStartHealth
{
    /// <summary>The longest reason kept; the gateway's own text is already capped, this guards the display.</summary>
    public const int MaxReasonLength = 1024;

    /// <summary>The start of the gateway's reason for an agent that is not installed (agent_not_installed.go, 1.0.7).</summary>
    public const string NotInstalledReasonPrefix = "the agent is not installed for this account";

    private const int MaxSkips = 64;

    private static readonly Regex NamePattern = new(
        "^[a-z0-9][a-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(250));

    /// <summary>The skipped connectors, sorted by name; empty when the block or the keys are absent or malformed.</summary>
    public static IReadOnlyList<ConnectorSkip> From(ServiceState? guardrail)
    {
        try
        {
            if (guardrail?.Details is not { ValueKind: JsonValueKind.Object } details)
            {
                return [];
            }

            var names = new SortedSet<string>(StringComparer.Ordinal);
            if (details.TryGetProperty("connectors_not_started", out var started) && started.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in started.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && CleanName(item.GetString()) is { } name)
                    {
                        _ = names.Add(name);
                    }
                }
            }

            var refused = ReadMap(details, "connectors_refused_by_agent_settings");
            var reasons = ReadMap(details, "connectors_not_started_reasons");
            foreach (var name in refused.Keys)
            {
                _ = names.Add(name);
            }

            var result = new List<ConnectorSkip>();
            foreach (var name in names.Take(MaxSkips))
            {
                var isRefused = refused.TryGetValue(name, out var refusedReason);
                var reason = isRefused && !string.IsNullOrEmpty(refusedReason)
                    ? refusedReason
                    : reasons.TryGetValue(name, out var listed) ? listed : string.Empty;

                var kind = reason.StartsWith(NotInstalledReasonPrefix, StringComparison.OrdinalIgnoreCase)
                    ? ConnectorSkipKind.AgentNotInstalled
                    : isRefused ? ConnectorSkipKind.RefusedByAgentSettings : ConnectorSkipKind.SetupFailed;
                if (kind == ConnectorSkipKind.SetupFailed && reason.Length == 0)
                {
                    reason = $"setup failed when the gateway started, so it is not enforced; see gateway.log, then run: defenseclaw setup {name}";
                }

                result.Add(new ConnectorSkip(name, kind, reason));
            }

            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or RegexMatchTimeoutException or ArgumentException)
        {
            return [];
        }
    }

    private static Dictionary<string, string> ReadMap(JsonElement details, string key)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!details.TryGetProperty(key, out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var entry in block.EnumerateObject())
        {
            if (CleanName(entry.Name) is not { } name)
            {
                continue;
            }

            map[name] = entry.Value.ValueKind == JsonValueKind.String ? CleanReason(entry.Value.GetString()) : string.Empty;
        }

        return map;
    }

    private static string? CleanName(string? raw)
    {
        var name = raw?.Trim().ToLowerInvariant();
        return name is { Length: > 0 } && NamePattern.IsMatch(name) ? name : null;
    }

    private static string CleanReason(string? raw)
    {
        var text = DisplayNames.Visible(raw?.Trim()).Trim();
        if (text.Length <= MaxReasonLength)
        {
            return text;
        }

        var cut = text[..MaxReasonLength];
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }
}
