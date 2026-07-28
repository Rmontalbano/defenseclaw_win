using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Logs;

public enum LogLevel
{
    Unknown = 0,
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
}

/// <summary>
/// One parsed line of <c>gateway.log</c> / <c>watchdog.log</c>.
/// <para>
/// Parsing is best-effort by design. The 0.8.7 sidecar writes bare
/// <c>[component] message</c> lines with no timestamp and no level (plus an ASCII banner
/// at startup), so <see cref="Raw"/> is always the authoritative text and every parsed
/// field is optional.
/// </para>
/// </summary>
public sealed partial record LogLine(
    string Raw,
    DateTimeOffset? Timestamp,
    LogLevel Level,
    string? Component,
    string Message)
{
    /// <summary>Position in the file, useful for stable list virtualization.</summary>
    public long Sequence { get; init; }

    public static LogLine Parse(string raw, long sequence = 0)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var remainder = raw.TrimEnd('\r');
        DateTimeOffset? timestamp = null;
        var level = LogLevel.Unknown;
        string? component = null;

        var timestampMatch = TimestampPrefix().Match(remainder);
        if (timestampMatch.Success &&
            DateTimeOffset.TryParse(
                timestampMatch.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            timestamp = parsed;
            remainder = remainder[timestampMatch.Length..];
        }

        var levelMatch = LevelPrefix().Match(remainder);
        if (levelMatch.Success)
        {
            level = ParseLevel(levelMatch.Groups[1].Value);
            remainder = remainder[levelMatch.Length..];
        }

        var componentMatch = ComponentPrefix().Match(remainder);
        if (componentMatch.Success)
        {
            component = componentMatch.Groups[1].Value;
            remainder = remainder[componentMatch.Length..];

            // A bracketed token can be either a component or a level; disambiguate.
            if (level == LogLevel.Unknown)
            {
                var asLevel = ParseLevel(component);
                if (asLevel != LogLevel.Unknown)
                {
                    level = asLevel;
                    component = null;
                }
            }
        }

        return new LogLine(raw, timestamp, level, component, remainder.Trim()) { Sequence = sequence };
    }

    public static LogLevel ParseLevel(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "DEBUG" or "DBG" or "TRACE" => LogLevel.Debug,
        "INFO" or "INF" or "NOTICE" => LogLevel.Info,
        "WARN" or "WARNING" or "WRN" => LogLevel.Warn,
        "ERROR" or "ERR" => LogLevel.Error,
        "FATAL" or "PANIC" or "CRITICAL" => LogLevel.Fatal,
        _ => LogLevel.Unknown,
    };

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefix();

    [GeneratedRegex(@"^(?:\[)?(DEBUG|DBG|TRACE|INFO|INF|NOTICE|WARN|WARNING|WRN|ERROR|ERR|FATAL|PANIC|CRITICAL)(?:\])?[:\s]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelPrefix();

    [GeneratedRegex(@"^\[([^\]]{1,48})\]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentPrefix();
}
