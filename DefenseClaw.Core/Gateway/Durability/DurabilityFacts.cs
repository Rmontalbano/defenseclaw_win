using System.Text.Json;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>A connector's hook fail mode as the hooks themselves obey it (<c>hooks\.hookcfg</c>).</summary>
public sealed record ConnectorFailMode(string Connector, string Mode)
{
    /// <summary>True for <c>closed</c>: the hook blocks the agent whenever it cannot reach the gateway.</summary>
    public bool IsClosed => string.Equals(Mode, "closed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the disk says about how likely the gateway is to survive its next start, and what it would cost the agent if it does not (CUST-352): the
/// pinned Codex executable, CAS writes that never finished, the fail modes the hooks obey, and files this app moved aside and could put back.
/// Read together and compared by value, so the monitor can publish a snapshot only when one of them changed.
/// </summary>
public sealed class DurabilityFacts : IEquatable<DurabilityFacts>
{
    public static readonly DurabilityFacts Empty = new(null, Array.Empty<CasPendingWrite>(), Array.Empty<ConnectorFailMode>(), null);

    public DurabilityFacts(
        AgentExecutablePin? codexPin,
        IReadOnlyList<CasPendingWrite> pendingWrites,
        IReadOnlyList<ConnectorFailMode> failModes,
        CasQuarantineInfo? quarantine)
    {
        CodexPin = codexPin;
        PendingWrites = pendingWrites;
        FailModes = failModes;
        Quarantine = quarantine;
    }

    /// <summary>The Codex executable the gateway has pinned, or null when the lock names none.</summary>
    public AgentExecutablePin? CodexPin { get; }

    /// <summary>Guarded writes that began at least two minutes ago and never finished.</summary>
    public IReadOnlyList<CasPendingWrite> PendingWrites { get; }

    /// <summary>The fail mode of every connector in <c>.hookcfg</c>, sorted by name.</summary>
    public IReadOnlyList<ConnectorFailMode> FailModes { get; }

    /// <summary>The newest folder of files this app moved aside that has not been put back, or null.</summary>
    public CasQuarantineInfo? Quarantine { get; }

    /// <summary>True when Codex's pinned executable is no longer on disk: the next start will fail on it.</summary>
    public bool CodexExecutableMissing => CodexPin is { IsMissing: true };

    /// <summary>The connectors whose hooks block the agent when the gateway cannot be reached.</summary>
    public IReadOnlyList<string> FailClosedConnectors => FailModes.Where(m => m.IsClosed).Select(m => m.Connector).ToArray();

    /// <summary>The fail mode for <paramref name="connector"/> as the hooks have it, or null when <c>.hookcfg</c> names none.</summary>
    public string? FailModeOf(string connector) =>
        FailModes.FirstOrDefault(m => string.Equals(m.Connector, connector, StringComparison.OrdinalIgnoreCase))?.Mode;

    public bool Equals(DurabilityFacts? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
         (EqualityComparer<AgentExecutablePin?>.Default.Equals(CodexPin, other.CodexPin) &&
          EqualityComparer<CasQuarantineInfo?>.Default.Equals(Quarantine, other.Quarantine) &&
          PendingWrites.SequenceEqual(other.PendingWrites) &&
          FailModes.SequenceEqual(other.FailModes)));

    public override bool Equals(object? obj) => Equals(obj as DurabilityFacts);

    public override int GetHashCode() => HashCode.Combine(CodexPin, Quarantine, PendingWrites.Count, FailModes.Count);
}

/// <summary>Reads <see cref="DurabilityFacts"/> from the data directory. Read-only, bounded, and never throws.</summary>
public static class DurabilityReader
{
    private const long MaxHookConfigBytes = 256 * 1024;

    /// <param name="dataDirectory">The DefenseClaw data directory (<c>~\.defenseclaw</c>): the lock, the selection receipt and the CAS receipts.</param>
    /// <param name="hooksDirectory">Its <c>hooks</c> folder, where <c>.hookcfg</c> is.</param>
    /// <param name="quarantineRoot">Where this app moves stuck writes aside.</param>
    /// <param name="now">The clock.</param>
    /// <param name="exists">Whether a path is a file; the real one when null.</param>
    public static DurabilityFacts Read(string dataDirectory, string hooksDirectory, string quarantineRoot, DateTimeOffset now, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentException.ThrowIfNullOrEmpty(hooksDirectory);
        ArgumentException.ThrowIfNullOrEmpty(quarantineRoot);

        return new DurabilityFacts(
            AgentExecutablePin.Read(dataDirectory, "codex", now, exists) is { Executable: not null } pin ? pin : null,
            CasReceipts.FindPending(dataDirectory, now).Select(w => w.WithAssessment(CasReceipts.Classify(w))).ToArray(),
            ReadFailModes(Path.Combine(hooksDirectory, ".hookcfg")),
            CasQuarantine.LatestUnrestored(quarantineRoot));
    }

    /// <summary>The <c>fail_modes</c> object of <c>.hookcfg</c> (connector name to <c>open</c> or <c>closed</c>); nothing else in the file is read.</summary>
    public static IReadOnlyList<ConnectorFailMode> ReadFailModes(string hookConfigPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(hookConfigPath);

        try
        {
            using var document = JsonDocument.Parse(SharedFile.ReadAllText(hookConfigPath, MaxHookConfigBytes));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("fail_modes", out var modes) ||
                modes.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<ConnectorFailMode>();
            }

            var result = new List<ConnectorFailMode>();
            foreach (var property in modes.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString()?.Trim().ToLowerInvariant() is "open" or "closed" &&
                    property.Name.Length is > 0 and <= 64)
                {
                    result.Add(new ConnectorFailMode(property.Name.Trim().ToLowerInvariant(), property.Value.GetString()!.Trim().ToLowerInvariant()));
                }
            }

            return result.OrderBy(m => m.Connector, StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<ConnectorFailMode>();
        }
    }
}
