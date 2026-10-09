using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Core.Policy;

/// <summary>Which Policies surface a backend serves.</summary>
public enum PolicySurface
{
    /// <summary>The table of named policies with the plain <c>policy</c> commands (list, show, activate, create, edit, delete): 0.8.10 (CUST-281).</summary>
    NamedPolicies,

    /// <summary>
    /// The newer runtime's policy model: posture, opt-in packs, chains, rule families, named policies, rule packs - and, where sandboxes run, sandbox
    /// packs, which the runtime's own Windows TUI leaves out and so does this panel (CUST-293). Called "seven-view" after the runtime's model.
    /// </summary>
    SevenViewModel,
}

/// <summary>What a runtime says about itself that decides which Policies surface fits it (the seam CUST-293 uses).</summary>
/// <param name="HasSevenViewPanel">True for a runtime whose <c>defenseclaw.tui.policy_panel</c> serves the Mac's seven-view model (newer than 0.8.10).</param>
public sealed record PolicyRuntimeCapabilities(bool HasSevenViewPanel = false)
{
    /// <summary>The installed 0.8.10 runtime: the plain <c>policy</c> commands, no seven-view model.</summary>
    public static PolicyRuntimeCapabilities Release0810 { get; } = new();

    /// <summary>
    /// What the connected runtime's probe says (<see cref="RuntimeCapability.PolicyModel"/>: <c>guardrail --help</c> lists
    /// <c>protection</c>). A runtime that has not been probed, or whose probe failed, has no seven-view model: the 0.8.10 surface is the
    /// fallback, and the only one it can be.
    /// </summary>
    public static PolicyRuntimeCapabilities From(RuntimeCapabilities? capabilities) =>
        new(capabilities is not null && capabilities.Has(RuntimeCapability.PolicyModel));
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
/// families, policies, rule packs; six of its seven views on Windows), not a different spelling of these commands. <see cref="PolicyBackends.Select"/>
/// picks a backend by <see cref="PolicyRuntimeCapabilities"/>, which comes from the runtime probe
/// (<see cref="PolicyRuntimeCapabilities.From"/>): <see cref="Release0810PolicyBackend"/> for a runtime without the model, and
/// <see cref="SevenViewPolicyBackend"/> for one that has it. <see cref="Surface"/> says which view the panel then shows.
/// </para>
/// </summary>
public interface IPolicyBackend
{
    /// <summary>A stable id for traces and tests: <c>defenseclaw-0.8.10</c>.</summary>
    string Id { get; }

    /// <summary>Which Policies surface this backend serves; the panel shows the matching view.</summary>
    PolicySurface Surface { get; }

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

    public PolicySurface Surface => PolicySurface.NamedPolicies;

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

/// <summary>
/// The Policies backend of the runtime verified against DefenseClaw source commit 95159fd, the one with the seven-view model
/// (<see cref="RuntimeCapability.PolicyModel"/>). It serves <see cref="PolicySurface.SevenViewModel"/>: the panel reads the catalog with
/// <see cref="Reader"/> (read-only commands that exist at that commit - no Python bridge) and builds the views with <see cref="PolicyModel"/>.
/// The runtime's model is called seven-view because that is what it has where sandboxes run; on Windows, where its own TUI leaves the Sandbox
/// packs view out, this panel shows the other six (see <see cref="PolicyModel"/>).
/// The named-policy members of <see cref="IPolicyBackend"/> are the plain <c>policy</c> commands in their <c>--json</c> form where the seven-view
/// panel needs one (list, activate, validate); creating, editing and deleting policies are not part of that panel, so those members throw.
/// </summary>
public sealed class SevenViewPolicyBackend : IPolicyBackend
{
    public static SevenViewPolicyBackend Instance { get; } = new();

    public string Id => "defenseclaw-policy-model";

    public PolicySurface Surface => PolicySurface.SevenViewModel;

    public PolicyBackendCapabilities Capabilities { get; } = new(CanCreate: false, CanEdit: false, CanDelete: false, CanValidate: true, CanTest: false);

    public bool Supports(PolicyRuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.HasSevenViewPanel;
    }

    public IReadOnlyList<string> ListArgv => PolicyActionGuard.CatalogReads[0];

    public IReadOnlyList<string> ValidateArgv { get; } = new[] { "policy", "validate" };

    public IReadOnlyList<string> TestArgv { get; } = new[] { "policy", "test" };

    public IReadOnlyList<string> ShowArgv(string name) => new[] { "policy", "show", Safe(name), "--json" };

    public IReadOnlyList<string> ActivateArgv(string name) => PolicyIntents.Activate(name).Args;

    public IReadOnlyList<string> DeleteArgv(string name, bool force) =>
        throw new NotSupportedException("The seven-view Policies panel does not delete policies.");

    public IReadOnlyList<string> CreateArgv(PolicyCreate create) =>
        throw new NotSupportedException("The seven-view Policies panel does not create policies.");

    public IReadOnlyList<string> EditArgv(string name, PolicyEdit edit) =>
        throw new NotSupportedException("The seven-view Policies panel edits only a policy's guardrail thresholds, through PolicyIntents.Threshold.");

    public bool TryParseList(string stdout, out PolicyListing listing, out string error)
    {
        listing = new PolicyListing(Array.Empty<PolicySummary>());
        if (!PolicyCatalogJson.TryParsePolicies(stdout, out var policies, out error))
        {
            return false;
        }

        listing = new PolicyListing(policies.Select(p => new PolicySummary(p.Name, p.Description, p.IsBuiltIn, p.IsActive)).ToArray());
        return true;
    }

    public bool TryParseDetail(string stdout, out PolicyDetail? detail, out string error)
    {
        detail = null;
        error = "policy show --json prints a policy's summary only; the seven-view panel reads that from policy list --json.";
        return false;
    }

    /// <summary>A reader for the catalog, over <paramref name="runner"/> (which must run only the read-only argv it is handed).</summary>
    public PolicyModelReader Reader(PolicyModelReader.Runner runner, IPolicyDataFiles? files = null) => new(runner, files);

    private static string Safe(string name) =>
        PolicyNames.IsSafe(name) ? name : throw new ArgumentException("Not a policy name the CLI can be handed.", nameof(name));
}

/// <summary>Chooses the backend for a runtime.</summary>
public static class PolicyBackends
{
    /// <summary>Every backend this build has: the 0.8.10 one and the seven-view one.</summary>
    public static IReadOnlyList<IPolicyBackend> Available { get; } = new IPolicyBackend[] { Release0810PolicyBackend.Instance, SevenViewPolicyBackend.Instance };

    /// <summary>The first of <paramref name="backends"/> that supports <paramref name="capabilities"/>; null when none does (a runtime this build has no Policies surface for).</summary>
    public static IPolicyBackend? Select(IEnumerable<IPolicyBackend> backends, PolicyRuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(capabilities);
        return backends.FirstOrDefault(b => b.Supports(capabilities));
    }

    /// <summary>The backend for the runtime the app manages when nothing is known about it (0.8.10).</summary>
    public static IPolicyBackend ForInstalledRuntime() =>
        Select(Available, PolicyRuntimeCapabilities.Release0810) ?? Release0810PolicyBackend.Instance;

    /// <summary>
    /// The backend for the connected runtime: the seven-view one when its probe found <see cref="RuntimeCapability.PolicyModel"/>, otherwise
    /// the 0.8.10 one. A runtime that is unknown (not probed, probe failed) gets the 0.8.10 backend, so nothing newer is offered on a guess.
    /// </summary>
    public static IPolicyBackend For(RuntimeCapabilities? capabilities) =>
        Select(Available, PolicyRuntimeCapabilities.From(capabilities)) ?? Release0810PolicyBackend.Instance;
}
