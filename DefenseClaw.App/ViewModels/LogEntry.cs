using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.ViewModels;

/// <summary>One name/value row in the log inspector's grid.</summary>
public sealed record LogField(string Name, string Value);

/// <summary>
/// One row of the Logs list: a line of <c>gateway.log</c> / <c>watchdog.log</c>, or a canonical event of the Verdicts / Events streams.
/// Both are shown the same way: a tone bar, a time, a bracketed <c>[type:action]</c> label and the message.
/// <para>
/// <b>Everything shown is masked.</b> <see cref="Raw"/> and <see cref="Message"/> go through <see cref="DisplayRedaction"/> (an event from the
/// database arrives masked already; a file line is masked here), so nothing the list, the inspector or Copy hands out carries a credential.
/// Derived fields (the mask, the severity, the humanised message, the inspector's grid) are computed the first time something asks, not when
/// the line arrives: the panel buffers every line of a chatty gateway whether or not anyone is looking.
/// </para>
/// </summary>
public sealed partial class LogEntry : ObservableObject
{
    private static readonly string[] NoisePatterns =
    {
        "event tick seq=", "event health seq=", "payload_len=20", "mallocstacklogging", "event sessions.changed", "content-length=0",
    };

    private readonly LogLine? _line;
    private readonly StreamEvent? _event;
    private readonly string _stream;
    private Derived? _derived;

    /// <summary>How many adjacent identical rows this one stands for (1 when it is alone); the "Repeated N times" chip shows from 2.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeated))]
    [NotifyPropertyChangedFor(nameof(RepeatText))]
    private int _count = 1;

    /// <summary>A line of a log file. <paramref name="stream"/> names the log (<c>gateway</c> / <c>watchdog</c>); it is the label of a line with no <c>[component]</c>.</summary>
    public LogEntry(LogLine line, string stream = "gateway")
    {
        ArgumentNullException.ThrowIfNull(line);
        _line = line;
        _stream = stream;
        Sequence = line.Sequence;
        HasComponent = line.Component is { Length: > 0 };
        ComponentText = HasComponent ? line.Component! : "-";
        LevelText = line.Level == LogLevel.Unknown ? string.Empty : line.Level.ToString().ToUpperInvariant();
        TimestampText = line.Timestamp is { } at ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
        TimeText = line.Timestamp is { } when ? when.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
        LastTime = line.Timestamp;
    }

    /// <summary>A canonical event of the Verdicts / Events streams; <paramref name="sequence"/> is its position in the read (oldest 0).</summary>
    public LogEntry(StreamEvent streamEvent, long sequence, string stream)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        _event = streamEvent;
        _stream = stream;
        Sequence = sequence;
        HasComponent = streamEvent.Connector.Length > 0;
        ComponentText = HasComponent ? streamEvent.Connector : "-";
        LevelText = streamEvent.Severity == AuditSeverity.Unknown ? string.Empty : streamEvent.Severity.ToString().ToUpperInvariant();
        var known = streamEvent.Timestamp != DateTimeOffset.MinValue;
        TimestampText = known ? streamEvent.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
        TimeText = known ? streamEvent.Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
        LastTime = known ? streamEvent.Timestamp : null;
    }

    private LogEntry(LogEntry source)
    {
        _line = source._line;
        _event = source._event;
        _stream = source._stream;
        _derived = source._derived;
        Sequence = source.Sequence;
        HasComponent = source.HasComponent;
        ComponentText = source.ComponentText;
        LevelText = source.LevelText;
        TimestampText = source.TimestampText;
        TimeText = source.TimeText;
        LastTime = source.LastTime;
    }

    /// <summary>
    /// A row of its own for the displayed list, sharing everything derived with this one: the list's "Repeated N times" count belongs to the
    /// displayed row, and the buffered one must stay at 1 so that projecting the buffer again does not count its repeats twice.
    /// </summary>
    internal LogEntry Fork()
    {
        _ = Get();
        return new LogEntry(this);
    }

    /// <summary>The position in the file, or in the read, for a stable order.</summary>
    public long Sequence { get; }

    /// <summary>True for a canonical event from the database (Verdicts, Events); false for a log-file line.</summary>
    public bool IsStructured => _event is not null;

    /// <summary>The line's own timestamp (local time) as date and time; empty when it has none.</summary>
    public string TimestampText { get; }

    /// <summary>The time of day for the list; empty when the row has none.</summary>
    public string TimeText { get; }

    /// <summary>When the last row this one stands for happened (equal to the first unless <see cref="Count"/> is above 1).</summary>
    public DateTimeOffset? LastTime { get; private set; }

    /// <summary>The parsed level in capitals; empty for the usual log-file line that has none.</summary>
    public string LevelText { get; }

    /// <summary>The component (connector for an event) the list's filter checklist keys on; "-" when there is none.</summary>
    public string ComponentText { get; }

    public bool HasComponent { get; }

    /// <summary>The full text, masked: the line itself, or the event as indented JSON.</summary>
    public string Raw => Get().Raw;

    /// <summary>The message without its <c>[component]</c> prefix, masked; what the inspector shows.</summary>
    public string Message => Get().Message;

    /// <summary>The message as the list shows it: timestamps, ids and hashes stripped, whitespace collapsed (the Mac's humanMessage); never empty.</summary>
    public string DisplayMessage => Get().DisplayMessage;

    /// <summary>True when stripping left nothing worth showing (two characters or fewer): the Mac hides such a row under every preset but "all".</summary>
    public bool IsBlank => Get().HumanMessage.Length <= 2;

    public AuditSeverity Severity => Get().Severity;

    /// <summary>The <see cref="DcToneBar"/> tag for the row's severity: Critical, High, Medium, Low, or empty for a neutral bar.</summary>
    public string Tone => Get().Tone;

    /// <summary>The Mac's coarse kind (<c>verdict</c>, <c>scan</c>, <c>error</c>…); for a file line, the first part of its <c>[component]</c>.</summary>
    public string EventType => Get().EventType;

    public string Action => Get().Action;

    public string Connector => Get().Connector;

    /// <summary>The bracketed label: <c>[type:action]</c>, <c>[type]</c>, or the stream's name when there is neither.</summary>
    public string Label => Get().Label;

    /// <summary>The lower-cased text the presets and the event / action pickers match against.</summary>
    public string MatchText => Get().MatchText;

    /// <summary>The inspector's key/value grid: only what the row actually carries.</summary>
    public IReadOnlyList<LogField> Fields => Get().Fields;

    public bool IsRepeated => Count > 1;

    public string RepeatText => string.Create(CultureInfo.InvariantCulture, $"Repeated {Count} times");

    /// <summary>Folds <paramref name="next"/>, an adjacent identical row, into this one: one more occurrence, and the later time.</summary>
    internal void Absorb(LogEntry next)
    {
        Count++;
        LastTime = next.LastTime ?? LastTime;
    }

    /// <summary>True when <paramref name="other"/> reads the same as this row: the Mac's "adjacent duplicate" test.</summary>
    internal bool Repeats(LogEntry other) =>
        string.Equals(DisplayMessage, other.DisplayMessage, StringComparison.Ordinal)
        && string.Equals(EventType, other.EventType, StringComparison.Ordinal)
        && string.Equals(Action, other.Action, StringComparison.Ordinal)
        && string.Equals(Connector, other.Connector, StringComparison.Ordinal)
        && Severity == other.Severity;

    /// <summary>
    /// What a screen reader announces for the row (UI Automation falls back to <c>ToString()</c> for a list item with no explicit name),
    /// instead of the type name.
    /// </summary>
    public override string ToString() => IsStructured
        ? $"{Label} {Message}"
        : HasComponent ? $"{ComponentText}: {Message}" : Message;

    private Derived Get() => _derived ??= Derive();

    private Derived Derive()
    {
        if (_event is { } e)
        {
            return DeriveEvent(e);
        }

        return DeriveLine(_line!);
    }

    private Derived DeriveLine(LogLine line)
    {
        var raw = DisplayRedaction.Text(line.Raw);
        var message = DisplayRedaction.Text(line.Message.Length > 0 ? line.Message : line.Raw);
        var lower = raw.ToLowerInvariant();

        var severity = line.Level switch
        {
            LogLevel.Fatal => AuditSeverity.Critical,
            LogLevel.Error => AuditSeverity.High,
            LogLevel.Warn => AuditSeverity.Medium,
            _ => AuditSeverity.Unknown,
        };

        if (lower.Contains("critical", StringComparison.Ordinal) || lower.Contains("fatal", StringComparison.Ordinal))
        {
            severity = Max(severity, AuditSeverity.Critical);
        }
        else if (lower.Contains("error", StringComparison.Ordinal) || lower.Contains("http 4", StringComparison.Ordinal) || lower.Contains("http 5", StringComparison.Ordinal))
        {
            severity = Max(severity, AuditSeverity.High);
        }
        else if (lower.Contains("warn", StringComparison.Ordinal))
        {
            severity = Max(severity, AuditSeverity.Medium);
        }
        else
        {
            severity = Max(severity, AuditSeverity.Info);
        }

        var eventType = _stream;
        var connector = string.Empty;
        if (HasComponent)
        {
            var parts = line.Component!.Split(':', 2);
            eventType = parts[0].Trim().ToLowerInvariant();
            if (parts.Length == 2)
            {
                connector = parts[1].Trim().ToLowerInvariant();
            }
        }

        var action = FirstValue("phase", raw) ?? FirstValue("action", raw) ?? (lower.Contains("completed", StringComparison.Ordinal) ? "completed" : string.Empty);

        var fields = new List<LogField>();
        if (HasComponent)
        {
            fields.Add(new LogField("component", ComponentText));
        }

        if (LevelText.Length > 0)
        {
            fields.Add(new LogField("level", LevelText));
        }

        if (TimestampText.Length > 0)
        {
            fields.Add(new LogField("time", TimestampText));
        }

        if (severity > AuditSeverity.Info)
        {
            fields.Add(new LogField("severity", severity.ToString().ToUpperInvariant()));
        }

        if (action.Length > 0)
        {
            fields.Add(new LogField("action", action));
        }

        if (connector.Length > 0)
        {
            fields.Add(new LogField("connector", connector));
        }

        fields.Add(new LogField("line", line.Sequence.ToString(CultureInfo.InvariantCulture)));

        return Build(raw, message, severity, eventType, action, connector, lower, fields);
    }

    private Derived DeriveEvent(StreamEvent e)
    {
        var fields = new List<LogField>();
        void Add(string name, string value)
        {
            if (value.Length > 0)
            {
                fields.Add(new LogField(name, value));
            }
        }

        Add("time", TimestampText);
        Add("stream", _stream);
        Add("event", e.EventType);
        Add("action", e.Action);
        Add("severity", LevelText);
        Add("connector", e.Connector);
        Add("bucket", e.Bucket);
        Add("event name", e.EventName);
        Add("source", e.Source);
        Add("actor", e.Actor);
        Add("id", e.Id);

        return Build(e.RawJson, e.Message, e.Severity, e.EventType, e.Action, e.Connector, e.Message.ToLowerInvariant(), fields);
    }

    private static Derived Build(string raw, string message, AuditSeverity severity, string eventType, string action, string connector, string matchText, List<LogField> fields)
    {
        var human = Humanise(message);
        var parts = new[] { eventType, action }.Where(p => p.Length > 0 && p != "event").ToArray();
        var label = parts.Length == 0 ? string.Empty : "[" + string.Join(':', parts) + "]";
        var tone = severity switch
        {
            AuditSeverity.Critical => "Critical",
            AuditSeverity.High => "High",
            AuditSeverity.Medium or AuditSeverity.Warn => "Medium",
            AuditSeverity.Low => "Low",
            _ => string.Empty,
        };

        return new Derived
        {
            Raw = raw,
            Message = message,
            HumanMessage = human,
            DisplayMessage = human.Length == 0 ? "Redacted event payload" : human,
            Severity = severity,
            Tone = tone,
            EventType = eventType,
            Action = action,
            Connector = connector,
            Label = label,
            MatchText = matchText,
            Fields = fields,
        };
    }

    private static AuditSeverity Max(AuditSeverity a, AuditSeverity b) => a >= b ? a : b;

    /// <summary>The value after <c>key=</c>, up to whitespace; null when the key is absent or empty.</summary>
    private static string? FirstValue(string key, string line)
    {
        var at = line.IndexOf(key + "=", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + key.Length + 1;
        var end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
        {
            end++;
        }

        return end > start ? line[start..end] : null;
    }

    /// <summary>The noise the "no-noise" preset drops; kept beside <see cref="MatchText"/> so both live in one place.</summary>
    internal static bool IsNoise(string matchText)
    {
        foreach (var pattern in NoisePatterns)
        {
            if (matchText.Contains(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // The Mac's humanMessage: the parts of a runtime line that differ on every repeat of it, removed so repeats collapse.
    [GeneratedRegex(@"^\s*[-–—]?\s*\d{1,2}:\d{2}:\d{2}(?:\.\d+)?\s+", RegexOptions.CultureInvariant, 500)]
    private static partial Regex LeadingClock();

    [GeneratedRegex(@"<redacted(?:\s+[^>]*)?>", RegexOptions.CultureInvariant, 500)]
    private static partial Regex RedactedTag();

    [GeneratedRegex(@"\b(?:call_id|session|run_id|audit_id|content_hash|payload_hmac|sha(?:256)?|len|body_bytes|request_bytes|response_bytes)=[^\s]+", RegexOptions.CultureInvariant, 500)]
    private static partial Regex Identifiers();

    [GeneratedRegex(@"\b(?:sha|a)=[A-Fa-f0-9]{8,}>", RegexOptions.CultureInvariant, 500)]
    private static partial Regex HashTail();

    [GeneratedRegex(@"\s+(?:cause|msg)=\s*$", RegexOptions.CultureInvariant, 500)]
    private static partial Regex DanglingKey();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 500)]
    private static partial Regex Whitespace();

    internal static string Humanise(string source)
    {
        try
        {
            var text = LeadingClock().Replace(source, string.Empty);
            text = RedactedTag().Replace(text, string.Empty);
            text = Identifiers().Replace(text, string.Empty);
            text = HashTail().Replace(text, string.Empty);
            text = DanglingKey().Replace(text, string.Empty);
            return Whitespace().Replace(text, " ").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return source.Trim();
        }
    }

    private sealed class Derived
    {
        public string Raw { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public string HumanMessage { get; init; } = string.Empty;

        public string DisplayMessage { get; init; } = string.Empty;

        public AuditSeverity Severity { get; init; }

        public string Tone { get; init; } = string.Empty;

        public string EventType { get; init; } = string.Empty;

        public string Action { get; init; } = string.Empty;

        public string Connector { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string MatchText { get; init; } = string.Empty;

        public IReadOnlyList<LogField> Fields { get; init; } = Array.Empty<LogField>();
    }
}
