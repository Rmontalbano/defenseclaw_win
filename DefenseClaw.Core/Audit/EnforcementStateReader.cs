using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// What DefenseClaw is doing to one skill, MCP server, plugin or tool <em>right now</em>: the three dimensions of its row in the <c>actions</c> table
/// (<c>actions_json</c>: <c>install</c> = <c>block</c> or <c>allow</c>, <c>file</c> = <c>quarantine</c>, <c>runtime</c> = <c>disable</c>), read the way the Govern
/// panels read them from <c>&lt;noun&gt; list --json</c> (<c>GovernJson.Interpret</c>: the same fields, the same words). The Audit inspector's "Current
/// state" (the TUI's <c>Current State</c> row, <c>ActionState.summary()</c>).
/// </summary>
public sealed record EnforcementState
{
    /// <summary><c>quarantine</c> when the item's files are quarantined; otherwise empty.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary><c>disable</c> when the item is disabled at runtime; otherwise empty.</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary><c>block</c> or <c>allow</c>; empty when neither was decided.</summary>
    public string Install { get; init; } = string.Empty;

    public bool IsBlocked => Is(Install, "block");

    public bool IsAllowed => Is(Install, "allow");

    public bool IsQuarantined => Is(File, "quarantine");

    public bool IsDisabled => Is(Runtime, "disable");

    /// <summary>True when nothing is being done to the item (a row that only remembers where it was found, or whose action was cleared).</summary>
    public bool IsEmpty => !IsBlocked && !IsAllowed && !IsQuarantined && !IsDisabled;

    /// <summary>
    /// The state in words, the TUI's way: <c>blocked</c>, <c>allowed</c>, <c>quarantined</c>, <c>disabled</c> (those that hold, in that order, joined by
    /// <c>, </c>), or <c>none</c> when nothing is being done.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(4);
            if (IsBlocked)
            {
                parts.Add("blocked");
            }

            if (IsAllowed)
            {
                parts.Add("allowed");
            }

            if (IsQuarantined)
            {
                parts.Add("quarantined");
            }

            if (IsDisabled)
            {
                parts.Add("disabled");
            }

            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }
    }

    /// <summary>The badge tone the Govern panels give this state: <c>Bad</c> for blocked or quarantined, <c>Warn</c> for disabled, <c>Ok</c> for allowed, else <c>Neutral</c>.</summary>
    public string Tone => IsBlocked || IsQuarantined ? "Bad" : IsDisabled ? "Warn" : IsAllowed ? "Ok" : "Neutral";

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads one row of <c>audit.db</c>'s <c>actions</c> table, read-only: the current enforcement state of a skill, MCP server, plugin or tool
/// (<see cref="EnforcementState"/>). The TUI does the same for an audit event (<c>get_action(target_type, target)</c>); the Govern panels get the same table
/// through the CLI, which a click on an audit row must not start.
/// <para>
/// <b>Most specific wins, per field</b> (<c>enforce/policy.py</c> <c>is_blocked_for_connector</c>): an entry with no connector is global and applies to
/// every connector, an entry for a connector narrows it to that one, and for a connector the entry's own fields are authoritative where it sets them and
/// the global ones fall through where it does not (a connector's allow overrides a global block). The TUI reads only the global entry; given the connector
/// of the event this reads both and merges them, which is the same answer whenever there is no entry for the connector.
/// </para>
/// <para>
/// The lookup is one index seek (<c>idx_actions_type_name_conn</c>, unique on type, name and connector), cancellable and timed (a token or a timeout
/// ends the statement), and every value is a bound parameter. A database without the table, or without the file, has no state to report: the answer is null.
/// </para>
/// </summary>
public sealed class EnforcementStateReader
{
    /// <summary>How long a lookup may run unless its caller says otherwise (8 s, the app's limit for an inspector's lookups; see <see cref="ReaderTimeouts"/>).</summary>
    public static readonly TimeSpan DefaultTimeout = ReadOnlyQuery.DefaultTimeout;

    private readonly string _databasePath;
    private readonly TimeSpan _readTimeout;
    private long _reads;

    /// <param name="databasePath">Path to the <em>live</em> <c>audit.db</c>: the state is about now, whichever database the event came from.</param>
    /// <param name="readTimeout">
    /// How long a lookup may run when its caller names no timeout; <see cref="DefaultTimeout"/> when null. A composition built for a test passes its
    /// own (<see cref="ReaderTimeouts"/>): the app's 8 s is a bound on how long a person waits, not on how long a loaded machine needs.
    /// </param>
    public EnforcementStateReader(string databasePath, TimeSpan? readTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _databasePath = databasePath;
        _readTimeout = readTimeout ?? DefaultTimeout;
        if (_readTimeout <= TimeSpan.Zero && _readTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(readTimeout), _readTimeout, "The timeout must be positive.");
        }
    }

    /// <summary>How long a lookup that names no timeout may run: <see cref="DefaultTimeout"/> unless this reader was built with another.</summary>
    public TimeSpan ReadTimeout => _readTimeout;

    /// <summary>How many lookups were asked for; the idle tests hold it still.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>
    /// The state of <paramref name="targetName"/> of <paramref name="targetType"/> (<c>skill</c>, <c>mcp</c>, <c>plugin</c>, <c>tool</c>) for
    /// <paramref name="connector"/> (null or blank: the global entry only). Null when there is no entry at all, no <c>actions</c> table, or no database.
    /// </summary>
    public Task<EnforcementState?> ReadAsync(
        string targetType,
        string targetName,
        string? connector = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);
        ArgumentException.ThrowIfNullOrEmpty(targetType);
        ArgumentException.ThrowIfNullOrEmpty(targetName);
        var scope = connector?.Trim() ?? string.Empty;

        return ReadOnlyQuery.RunAsync<EnforcementState?>(
            _databasePath,
            timeout ?? _readTimeout,
            cancellationToken,
            null,
            async (connection, token) =>
            {
                var columns = await ReadOnlyQuery.ColumnsOfAsync(connection, "actions", token).ConfigureAwait(false);
                if (!columns.Contains("target_type") || !columns.Contains("target_name") || !columns.Contains("actions_json"))
                {
                    return null;
                }

                // A database from before connector scoping has one entry per target and no column to scope it by.
                var scoped = columns.Contains("connector");
                await using var command = connection.CreateCommand();
                command.CommandText = scoped
                    ? "SELECT connector, actions_json FROM actions WHERE target_type = $type AND target_name = $name AND (connector = '' OR connector = $connector)"
                    : "SELECT '' AS connector, actions_json FROM actions WHERE target_type = $type AND target_name = $name";
                command.Parameters.AddWithValue("$type", targetType);
                command.Parameters.AddWithValue("$name", targetName);
                if (scoped)
                {
                    command.Parameters.AddWithValue("$connector", scope);
                }

                EnforcementState? global = null;
                EnforcementState? narrowed = null;
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var owner = ReadOnlyQuery.Text(reader, 0) ?? string.Empty;
                    var state = Decode(ReadOnlyQuery.Text(reader, 1));
                    if (owner.Length == 0)
                    {
                        global = state;
                    }
                    else
                    {
                        narrowed = state;
                    }
                }

                return Merge(global, narrowed);
            });
    }

    /// <summary>The connector's own fields where it sets them, the global entry's where it does not; null when there is neither entry.</summary>
    internal static EnforcementState? Merge(EnforcementState? global, EnforcementState? narrowed)
    {
        if (narrowed is null)
        {
            return global;
        }

        if (global is null)
        {
            return narrowed;
        }

        return new EnforcementState
        {
            File = narrowed.File.Length > 0 ? narrowed.File : global.File,
            Runtime = narrowed.Runtime.Length > 0 ? narrowed.Runtime : global.Runtime,
            Install = narrowed.Install.Length > 0 ? narrowed.Install : global.Install,
        };
    }

    /// <summary>The three fields of an <c>actions_json</c> document; empty for text that is not one (a row is still a row).</summary>
    internal static EnforcementState Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new EnforcementState();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new EnforcementState();
            }

            return new EnforcementState
            {
                File = Field(document.RootElement, "file"),
                Runtime = Field(document.RootElement, "runtime"),
                Install = Field(document.RootElement, "install"),
            };
        }
        catch (JsonException)
        {
            return new EnforcementState();
        }
    }

    private static string Field(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
}
