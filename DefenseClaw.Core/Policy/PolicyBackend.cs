namespace DefenseClaw.Core.Policy;

/// <summary>What a runtime says about itself that decides which Policies surface fits it (the seam CUST-293 uses).</summary>
/// <param name="HasSevenViewPanel">True for a runtime whose <c>defenseclaw.tui.policy_panel</c> serves the Mac's seven-view model (newer than 0.8.10).</param>
public sealed record PolicyRuntimeCapabilities(bool HasSevenViewPanel = false)
{
    /// <summary>The installed 0.8.10 runtime: the plain <c>policy</c> commands, no seven-view model.</summary>
    public static PolicyRuntimeCapabilities Release0810 { get; } = new();
}

/// <summary>Which of the panel's actions a backend can carry out.</summary>
public sealed record PolicyBackendCapabilities(bool CanCreate, bool CanEdit, bool CanDelete, bool CanValidate, bool CanTest);

/// <summary>
/// How the Policies panel talks to one generation of the DefenseClaw runtime: the argv of each action and the reading of what comes
/// back. The panel owns the behaviour that does not change with the runtime - trust in the list, validate before activate, the
/// consequence summary, the acknowledgement for a weaker policy, the review of every change - and asks the backend only for the
/// commands and the parsing. A backend never runs anything and holds no state, so the panel (and its tests) are the only place a
/// command is started.
/// <para>
/// <b>The seam for CUST-293.</b> The newer runtime's seven-view model is a different surface (posture, opt-in packs, chains, rule
/// families, policies, rule packs, sandbox packs), not a different spelling of these commands. <see cref="PolicyBackends.Select"/>
/// picks a backend by <see cref="PolicyRuntimeCapabilities"/>; the 0.8.10 one below is the only one that exists today, and a
/// capability flag that selects another is where that work plugs in (a second implementation, and a view for it chosen the same way).
/// There is no code here for that runtime.
/// </para>
/// </summary>
public interface IPolicyBackend
{
    /// <summary>A stable id for traces and tests: <c>defenseclaw-0.8.10</c>.</summary>
    string Id { get; }

    PolicyBackendCapabilities Capabilities { get; }

    /// <summary>True when this backend is the one for a runtime with these capabilities.</summary>
    bool Supports(PolicyRuntimeCapabilities capabilities);

    IReadOnlyList<string> ListArgv { get; }

    IReadOnlyList<string> ShowArgv(string name);

    IReadOnlyList<string> ValidateArgv { get; }

    IReadOnlyList<string> TestArgv { get; }

    IReadOnlyList<string> ActivateArgv(string name);

    /// <summary>The delete command; <paramref name="force"/> is for the active policy (the CLI then re-activates <c>default</c>).</summary>
    IReadOnlyList<string> DeleteArgv(string name, bool force);

    IReadOnlyList<string> CreateArgv(PolicyCreate create);

    IReadOnlyList<string> EditArgv(string name, PolicyEdit edit);

    bool TryParseList(string stdout, out PolicyListing listing, out string error);

    bool TryParseDetail(string stdout, out PolicyDetail? detail, out string error);
}

/// <summary>The installed 0.8.10 <c>defenseclaw policy</c> commands (list, show, activate, create, delete, edit, test, validate).</summary>
public sealed class Release0810PolicyBackend : IPolicyBackend
{
    public static Release0810PolicyBackend Instance { get; } = new();

    public string Id => "defenseclaw-0.8.10";

    public PolicyBackendCapabilities Capabilities { get; } = new(CanCreate: true, CanEdit: true, CanDelete: true, CanValidate: true, CanTest: true);

    public bool Supports(PolicyRuntimeCapabilities capabilities) => !capabilities.HasSevenViewPanel;

    public IReadOnlyList<string> ListArgv { get; } = new[] { "policy", "list" };

    public IReadOnlyList<string> ValidateArgv { get; } = new[] { "policy", "validate" };

    public IReadOnlyList<string> TestArgv { get; } = new[] { "policy", "test" };

    public IReadOnlyList<string> ShowArgv(string name) => new[] { "policy", "show", Safe(name) };

    public IReadOnlyList<string> ActivateArgv(string name) => new[] { "policy", "activate", Safe(name) };

    public IReadOnlyList<string> DeleteArgv(string name, bool force) =>
        force ? new[] { "policy", "delete", Safe(name), "--force" } : new[] { "policy", "delete", Safe(name) };

    public IReadOnlyList<string> CreateArgv(PolicyCreate create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return create.ToArgv();
    }

    public IReadOnlyList<string> EditArgv(string name, PolicyEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return edit.ToArgv(Safe(name));
    }

    public bool TryParseList(string stdout, out PolicyListing listing, out string error) =>
        PolicyTextParser.TryParseList(stdout, out listing, out error);

    public bool TryParseDetail(string stdout, out PolicyDetail? detail, out string error) =>
        PolicyTextParser.TryParseDetail(stdout, out detail, out error);

    private static string Safe(string name) =>
        PolicyNames.IsSafe(name) ? name : throw new ArgumentException("Not a policy name the CLI can be handed.", nameof(name));
}

/// <summary>Chooses the backend for a runtime.</summary>
public static class PolicyBackends
{
    /// <summary>Every backend this build has: today the 0.8.10 one.</summary>
    public static IReadOnlyList<IPolicyBackend> Available { get; } = new IPolicyBackend[] { Release0810PolicyBackend.Instance };

    /// <summary>The first of <paramref name="backends"/> that supports <paramref name="capabilities"/>; null when none does (a runtime this build has no Policies surface for).</summary>
    public static IPolicyBackend? Select(IEnumerable<IPolicyBackend> backends, PolicyRuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(capabilities);
        return backends.FirstOrDefault(b => b.Supports(capabilities));
    }

    /// <summary>The backend for the runtime the app manages (0.8.10).</summary>
    public static IPolicyBackend ForInstalledRuntime() =>
        Select(Available, PolicyRuntimeCapabilities.Release0810) ?? Release0810PolicyBackend.Instance;
}
