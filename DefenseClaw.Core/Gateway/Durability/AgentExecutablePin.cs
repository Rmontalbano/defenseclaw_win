using System.Text.Json;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>
/// The agent executable DefenseClaw 1.0 has pinned for a connector, and whether it is still on disk. Codex desktop updates itself into a new
/// versioned folder (<c>bin\&lt;hash&gt;\codex.exe</c>) and deletes the old one; the gateway never checks that the pinned path exists, so the
/// next start fails (CUST-352, incident A). This reads the same two files the gateway does and does the check it does not.
/// </summary>
/// <param name="Connector">The connector, e.g. <c>codex</c>.</param>
/// <param name="Executable">The pinned path, or null when nothing is pinned.</param>
/// <param name="Source">Which file named it: <c>hook_contract_lock.json</c> or the short-lived <c>agent_selection.json</c> receipt.</param>
/// <param name="Exists">True when <paramref name="Executable"/> is a file now.</param>
public sealed record AgentExecutablePin(string Connector, string? Executable, string? Source, bool Exists)
{
    /// <summary>True when a path is pinned and it is not there: the next gateway start will fail on it.</summary>
    public bool IsMissing => Executable is not null && !Exists;

    public const string LockFileName = "hook_contract_lock.json";

    public const string SelectionFileName = "agent_selection.json";

    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The pin for <paramref name="connector"/> under <paramref name="dataDirectory"/>. The lock file's <c>connectors.&lt;name&gt;.agent_executable</c>
    /// wins; without one, a selection receipt (<c>selections.&lt;name&gt;.executable</c>) that has not expired. A missing, unreadable or odd file is "nothing pinned",
    /// never an error. Reads only.
    /// </summary>
    /// <param name="exists">Whether a path is a file; the real file system when null. A test passes its own.</param>
    /// <param name="now">The clock the receipt's <c>expires_at</c> is judged against.</param>
    public static AgentExecutablePin Read(string dataDirectory, string connector, DateTimeOffset now, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentException.ThrowIfNullOrEmpty(connector);

        exists ??= File.Exists;

        var locked = FromLock(Path.Combine(dataDirectory, LockFileName), connector);
        if (locked is not null)
        {
            return new AgentExecutablePin(connector, locked, LockFileName, Safe(exists, locked));
        }

        var selected = FromSelection(Path.Combine(dataDirectory, SelectionFileName), connector, now);
        return selected is null
            ? new AgentExecutablePin(connector, null, null, false)
            : new AgentExecutablePin(connector, selected, SelectionFileName, Safe(exists, selected));
    }

    private static bool Safe(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown is not "missing": a share that did not answer must not raise the alarm.
            return true;
        }
    }

    private static string? FromLock(string path, string connector)
    {
        if (!TryParse(path, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("connectors", out var connectors) || connectors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var entry in connectors.EnumerateObject())
        {
            if (string.Equals(entry.Name, connector, StringComparison.OrdinalIgnoreCase) &&
                entry.Value.ValueKind == JsonValueKind.Object &&
                entry.Value.TryGetProperty("agent_executable", out var exe) &&
                exe.ValueKind == JsonValueKind.String)
            {
                return Usable(exe.GetString());
            }
        }

        return null;
    }

    private static string? FromSelection(string path, string connector, DateTimeOffset now)
    {
        if (!TryParse(path, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("selections", out var selections) || selections.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var entry in selections.EnumerateObject())
        {
            if (!string.Equals(entry.Name, connector, StringComparison.OrdinalIgnoreCase) || entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // A receipt is good for 15 minutes; an expired one is ignored by the gateway, so it names nothing.
            if (!entry.Value.TryGetProperty("expires_at", out var expires) ||
                expires.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(expires.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var until) ||
                until <= now)
            {
                return null;
            }

            return entry.Value.TryGetProperty("executable", out var exe) && exe.ValueKind == JsonValueKind.String ? Usable(exe.GetString()) : null;
        }

        return null;
    }

    // Only an absolute path with nothing odd in it is judged: anything else is not a path this app can say is gone.
    private static string? Usable(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0 || !Path.IsPathRooted(value) ? null : value;

    private static bool TryParse(string path, out JsonElement root)
    {
        root = default;
        try
        {
            var text = SharedFile.ReadAllText(path, MaxBytes);
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
