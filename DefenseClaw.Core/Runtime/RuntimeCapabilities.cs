using System.Collections.Frozen;

namespace DefenseClaw.Core.Runtime;

/// <summary>
/// What the connected runtime can do, as far as the probes could tell. Immutable.
/// <para>
/// <b>Fail closed.</b> <see cref="Unknown"/> (nothing probed yet, a probe that timed out, output that is not what a DefenseClaw
/// CLI prints) has every flag false, and <see cref="IsKnown"/> false, so a feature hidden behind a flag stays hidden until the
/// runtime has positively shown it has it. Nothing is ever inferred from a failure.
/// </para>
/// </summary>
public sealed class RuntimeCapabilities
{
    private readonly FrozenSet<RuntimeCapability> _present;
    private readonly FrozenSet<string> _setupCommands;

    /// <summary>Nothing known: every capability is absent.</summary>
    public static RuntimeCapabilities Unknown { get; } = new(
        isKnown: false,
        present: [],
        setupCommands: [],
        notes: []);

    internal RuntimeCapabilities(
        bool isKnown,
        IEnumerable<RuntimeCapability> present,
        IEnumerable<string> setupCommands,
        IEnumerable<string> notes)
    {
        IsKnown = isKnown;
        _present = isKnown ? present.ToFrozenSet() : FrozenSet<RuntimeCapability>.Empty;
        _setupCommands = isKnown ? setupCommands.ToFrozenSet(StringComparer.Ordinal) : FrozenSet<string>.Empty;
        Notes = notes.ToArray();
    }

    /// <summary>True when the probes ran and agreed on what this runtime is. False means every <see cref="Has"/> answer is "no" for want of evidence.</summary>
    public bool IsKnown { get; }

    /// <summary>One-line caveats the probes found (a capability present with a platform limit, a probe that failed). Shown in About.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>True when the runtime has <paramref name="capability"/>. Always false for <see cref="Unknown"/>.</summary>
    public bool Has(RuntimeCapability capability) => _present.Contains(capability);

    /// <summary>Every capability present, in catalog order.</summary>
    public IEnumerable<RuntimeCapability> Present => RuntimeCapabilityCatalog.All.Where(Has);

    /// <summary>
    /// True when <c>defenseclaw setup --help</c> listed <paramref name="name"/> as a subcommand. For the palette entries that
    /// exist only on newer runtimes (<c>setup amp</c>, <c>setup kiro</c>); unknown means false.
    /// </summary>
    public bool HasSetupCommand(string name) => name is not null && _setupCommands.Contains(name);

    /// <summary>Every <c>setup</c> subcommand the probe saw.</summary>
    public IReadOnlyCollection<string> SetupCommands => _setupCommands.Items.ToArray();
}

/// <summary>Who the connected runtime says it is: <c>defenseclaw --version-json</c>, and which executable answered.</summary>
/// <param name="Name">The <c>name</c> field, <c>defenseclaw-cli</c> for both 0.8.10 and the pin.</param>
/// <param name="Version">The <c>version</c> field verbatim (<c>0.8.10</c>, <c>1.0.0</c>).</param>
/// <param name="SchemaVersion">The <c>schema_version</c> of the version document itself (1 on both); null when it was absent.</param>
/// <param name="Source">Where the answer came from, in a phrase: the CLI path, or <c>docker exec NAME</c>.</param>
public sealed record RuntimeIdentity(string Name, string Version, int? SchemaVersion, string Source)
{
    /// <summary>The numeric part of <see cref="Version"/> (<c>1.0.0</c>), or null when it does not start with one.</summary>
    public Version? ParsedVersion => RuntimeProbe.TryParseVersion(Version, out var parsed) ? parsed : null;

    /// <summary>"defenseclaw-cli 1.0.0".</summary>
    public string Display => $"{Name} {Version}";
}

/// <summary>
/// One probe result: who, what, and when. <see cref="Fingerprint"/> is what the cache compares; <see cref="UnknownReason"/> is
/// the sentence About shows when <see cref="Identity"/> is null.
/// </summary>
public sealed record RuntimeSnapshot(
    RuntimeIdentity? Identity,
    RuntimeCapabilities Capabilities,
    string? UnknownReason,
    string? Fingerprint,
    DateTimeOffset ProbedAt)
{
    /// <summary>Before the first probe: nothing known, and not yet asked.</summary>
    public static RuntimeSnapshot NotProbed { get; } = new(
        null, RuntimeCapabilities.Unknown, "The runtime has not been probed yet.", null, DateTimeOffset.MinValue);

    /// <summary>True when the probes identified the runtime.</summary>
    public bool IsKnown => Identity is not null && Capabilities.IsKnown;

    /// <summary>A probe that could not tell what the runtime is: every capability absent, with <paramref name="reason"/>.</summary>
    public static RuntimeSnapshot Failed(string reason, string? fingerprint, DateTimeOffset at) =>
        new(null, RuntimeCapabilities.Unknown, reason, fingerprint, at);
}
