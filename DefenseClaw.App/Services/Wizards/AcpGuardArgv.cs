using System.IO;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>What the ACP Guard wizard asks for (CUST-294): one editor and agent pair, its profile and mode, and, for a managed enrollment, two paths.</summary>
/// <param name="Client">The <c>--client</c> value (<c>zed</c>, <c>jetbrains</c>).</param>
/// <param name="Agent">The <c>--agent</c> value (<c>kiro</c>, <c>copilot</c>, ...).</param>
/// <param name="Profile">The <c>--profile</c> value (an <c>acp.profiles</c> key).</param>
/// <param name="Activate">Action mode (<c>--activate</c>): the guard blocks what policy denies. Off is observe mode.</param>
/// <param name="Managed">Managed enrollment (<c>--managed</c>): user-side files only; the policy and the credential stay the administrator's.</param>
/// <param name="RuntimeDataDirectory">For a managed enrollment, <c>--runtime-data-dir</c>: the per-user ACP runtime folder.</param>
/// <param name="TokenFile">For a managed enrollment, <c>--token-file</c>: the PATH of the administrator-provisioned token, never its contents.</param>
public sealed record AcpGuardRequest(
    string Client,
    string Agent,
    string Profile,
    bool Activate,
    bool Managed = false,
    string RuntimeDataDirectory = "",
    string TokenFile = "");

/// <summary>
/// The one argv builder behind the ACP Guard wizard, for a standard setup and a managed enrollment alike: <c>acp setup --client C --agent A --profile P
/// [--activate] [--managed --runtime-data-dir D --token-file F]</c>, in the Mac's order (<c>SetupDefinitions.acpCommands</c>, v1.1.34). The flags are
/// <c>defenseclaw acp setup</c>'s at 1.0.4 (<c>cli/defenseclaw/commands/cmd_acp.py:1012-1043</c>): <c>--client</c> and <c>--agent</c> are required
/// choices, <c>--profile</c> defaults to <c>default</c> and is always sent so the review names it, <c>--activate</c> is a bare switch, and
/// <c>--runtime-data-dir</c> / <c>--token-file</c> are refused without <c>--managed</c> (line 1072), which itself needs
/// <c>deployment_mode: managed_enterprise</c> (line 1070).
/// <para>
/// <b>No secret is ever on this command line.</b> A managed enrollment names the token by its path; the CLI reads the file itself and insists it is
/// exactly <c>&lt;runtime-data-dir&gt;\acp\&lt;client&gt;-&lt;agent&gt;.token</c> (<c>_managed_token</c>, line 123; the administrator's enrollment
/// publishes it there, <c>internal/acp/credential.go:106</c>). <see cref="Problem"/> refuses anything that is not such a path, so a pasted token
/// never reaches argv.
/// </para>
/// </summary>
public static class AcpGuardArgv
{
    public const string ClientFlag = "--client";
    public const string AgentFlag = "--agent";
    public const string ProfileFlag = "--profile";
    public const string ActivateFlag = "--activate";
    public const string ManagedFlag = "--managed";
    public const string RuntimeDataDirFlag = "--runtime-data-dir";
    public const string TokenFileFlag = "--token-file";

    /// <summary>The CLI's own default profile (<c>--profile</c>, <c>[default: default]</c>).</summary>
    public const string DefaultProfile = "default";

    /// <summary>The command words: <c>defenseclaw acp setup</c>. (<c>defenseclaw setup acp</c> is a different command: <c>acp adopt</c>.)</summary>
    public static IReadOnlyList<string> CommandWords { get; } = new[] { "acp", "setup" };

    /// <summary>The exact argv <see cref="Core.Cli.CliRunner"/> runs and the review prints.</summary>
    public static IReadOnlyList<string> Build(AcpGuardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var argv = new List<string>(CommandWords)
        {
            ClientFlag, request.Client.Trim(),
            AgentFlag, request.Agent.Trim(),
            ProfileFlag, request.Profile.Trim().Length > 0 ? request.Profile.Trim() : DefaultProfile,
        };

        if (request.Activate)
        {
            argv.Add(ActivateFlag);
        }

        if (request.Managed)
        {
            argv.Add(ManagedFlag);
            argv.Add(RuntimeDataDirFlag);
            argv.Add(request.RuntimeDataDirectory.Trim());
            argv.Add(TokenFileFlag);
            argv.Add(request.TokenFile.Trim());
        }

        return argv;
    }

    /// <summary>
    /// Where the CLI requires a managed pair's token to be: <c>&lt;runtime-data-dir&gt;\acp\&lt;client&gt;-&lt;agent&gt;.token</c>. Pure string work; the
    /// disk is not looked at.
    /// </summary>
    public static string ExpectedTokenFile(string runtimeDataDirectory, string client, string agent)
    {
        ArgumentNullException.ThrowIfNull(runtimeDataDirectory);
        return Path.Combine(runtimeDataDirectory.Trim(), "acp", client.Trim() + "-" + agent.Trim() + ".token");
    }

    /// <summary>
    /// Why <paramref name="request"/> cannot run, or null. <paramref name="managedAllowed"/> is whether this installation is a managed one
    /// (<c>deployment_mode: managed_enterprise</c>): the CLI refuses <c>--managed</c> anywhere else.
    /// </summary>
    public static string? Problem(AcpGuardRequest request, bool managedAllowed)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!AcpGuardReader.IsStableName(request.Client.Trim()))
        {
            return "Choose the editor (client) that runs the agent.";
        }

        if (!AcpGuardReader.IsStableName(request.Agent.Trim()))
        {
            return "Choose the ACP agent to guard.";
        }

        if (!AcpGuardReader.IsStableName(request.Profile.Trim().Length > 0 ? request.Profile.Trim() : DefaultProfile))
        {
            return "A profile name is lowercase letters, digits, '-' and '_' (up to 64, starting with a letter or digit), as acp.profiles keys are.";
        }

        if (!request.Managed)
        {
            return null;
        }

        if (!managedAllowed)
        {
            return ManagedNeedsManagedInstallation;
        }

        if (!IsAbsolute(request.RuntimeDataDirectory))
        {
            return "Managed enrollment needs the full path of the runtime data directory (for example %USERPROFILE%\\.defenseclaw, written out).";
        }

        if (!IsAbsolute(request.TokenFile))
        {
            return "Give the full path of the token file your administrator provisioned, never the token itself.";
        }

        var expected = ExpectedTokenFile(request.RuntimeDataDirectory, request.Client, request.Agent);
        return SamePath(request.TokenFile, expected)
            ? null
            : "The CLI accepts the managed token only at " + expected + " (<runtime-data-dir>\\acp\\<client>-<agent>.token).";
    }

    /// <summary>Why the managed switch is off on a per-user installation.</summary>
    public const string ManagedNeedsManagedInstallation =
        "Managed enrollment is for an administrator-managed installation (deployment_mode: managed_enterprise). This one is per-user, so the CLI would refuse --managed.";

    private static bool IsAbsolute(string path)
    {
        var trimmed = path.Trim();
        return trimmed.Length > 0 && trimmed.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(trimmed);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            // Both are fully qualified here, so GetFullPath only normalises (separators, "."): no disk access.
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a.Trim())),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b.Trim())),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
