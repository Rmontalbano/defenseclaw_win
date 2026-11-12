using DefenseClaw.Core.Config;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.Core.ClaudeCode;

/// <summary>
/// The fixed facts and sentences of Claude Cowork protection (runtime 1.0.8) that more than one place of the app says: the setup command the app runs,
/// the manual upload steps in Claude Desktop, the remove-plugin step at uninstall and reset, and the plugin's files under the data directory. The
/// sentences are the CLI's own (cli/defenseclaw/cowork.py <c>upload_steps</c>, <c>UPLOAD_PATH_TEXT</c>, <c>REMOVE_PATH_TEXT</c>;
/// cli/defenseclaw/commands/cmd_uninstall.py <c>_COWORK_REMOVAL_STEP</c>), so the app and the CLI never disagree.
/// </summary>
public static class CoworkProtectionFacts
{
    /// <summary>The state folder under the data directory (<c>STATE_DIR</c>).</summary>
    public const string StateFolderName = "cowork";

    /// <summary>The plugin zip <c>setup cowork</c> builds (<c>ZIP_NAME</c>).</summary>
    public const string PluginZipName = "defenseclaw-cowork-plugin.zip";

    /// <summary>The plugin state file (<c>STATE_NAME</c>); its existence is what makes uninstall and reset print the remove-plugin step.</summary>
    public const string PluginStateName = "plugin-state.json";

    /// <summary>Where Claude Desktop uploads a plugin (<c>UPLOAD_PATH_TEXT</c>).</summary>
    public const string UploadPath = "Customize > Plugins > Add > Upload plugin";

    /// <summary>Where Claude Desktop removes one (<c>REMOVE_PATH_TEXT</c>).</summary>
    public const string RemovePath = "Customize > Plugins, then the ... menu > Remove";

    /// <summary>
    /// The one sentence for uninstall and reset: DefenseClaw cannot remove its account plugin, and until the user does, Cowork tool calls are blocked.
    /// The CLI's own warning (<c>cmd_uninstall.py</c>: "DefenseClaw cannot remove its Cowork plugin from your Claude account: " + <c>_COWORK_REMOVAL_STEP</c>).
    /// </summary>
    public const string RemovePluginStep =
        "DefenseClaw cannot remove its Cowork plugin from your Claude account: remove it in Claude Desktop (Customize > Plugins, then the ... menu > Remove). " +
        "Until then Cowork tool calls are blocked.";

    /// <summary>
    /// Why Claude Code's settings are Cowork's: the plugin runs the Claude Code connector's hooks, so its mode, fail mode and rules apply to Cowork too
    /// (docs connectors/cowork.mdx; <c>mode_text</c> in cowork.py).
    /// </summary>
    public const string ClaudeCodeSettingsApply =
        "Cowork runs through the Claude Code connector's hooks: the Claude Code mode, hook fail mode and rules apply to Cowork too.";

    /// <summary>
    /// What /health's <c>connectors[].surfaces.cowork</c> counters are: the hook's own label on a call (the <c>X-DefenseClaw-Cowork</c> header), counted
    /// since the gateway started. They are attribution, never proof that Cowork is covered (internal/gateway/health.go, <c>Surfaces</c>).
    /// </summary>
    public const string SurfaceCountersAreLabels =
        "These counters are the hook's own label on each call, counted since the gateway started; they do not prove Cowork is covered.";

    /// <summary>The heading of the review bar that says <c>setup cowork</c> also turns on the Claude Code connector.</summary>
    public const string ClaudeCodeToo = "This also turns on the Claude Code connector";

    /// <summary>
    /// What the review says when Claude Code is not a configured connector: <c>setup cowork --yes</c> sets it up first (cmd_setup_cowork.py:56-74, 230-234
    /// at 1.0.8), which adds Claude Code's hooks and restarts the gateway.
    /// </summary>
    public const string ClaudeCodeTooMessage =
        "Claude Code is not a configured connector on this computer. Cowork runs through the Claude Code connector's hooks, so this command also sets that connector up: " +
        "it adds the DefenseClaw hooks to Claude Code's settings and restarts the gateway. If you only want Cowork and not Claude Code, cancel here.";

    /// <summary>
    /// True when the connector roster of <paramref name="config"/> holds Claude Code and the guardrail is not switched off: the app's reading of the CLI's
    /// <c>_claudecode_enabled</c> (cowork.py:393-406 at 1.0.8). The per-connector switch is not read here, so a roster entry the CLI treats as off still reads as on.
    /// </summary>
    public static bool ClaudeCodeConfigured(ConfigDocument? config)
    {
        var reader = ConfigYamlReader.From(config);
        return SetupStateLines.ActiveConnectors(reader).Contains("claudecode", StringComparer.Ordinal) &&
               reader.Bool("guardrail", "enabled") != false;
    }

    /// <summary>
    /// The command the Cowork wizard runs: build the plugin, set up Claude Code first if it is off (<c>--yes</c>), and neither open Claude nor wait for a
    /// session (the app has no console to wait in; it checks the result from <c>status --json</c> instead). Flags from cmd_setup_cowork.py (1.0.8).
    /// </summary>
    public static IReadOnlyList<string> SetupArgv { get; } = new[] { "setup", "cowork", "--yes", "--no-open-claude", "--no-wait" };

    /// <summary>The exit code <c>setup cowork</c> uses on a managed host, where per-user Cowork setup is disabled and nothing is changed.</summary>
    public const int ManagedHostExitCode = 3;

    /// <summary>The three upload steps <c>setup cowork</c> prints on Windows, for the zip at <paramref name="zipPath"/> (<c>upload_steps</c>).</summary>
    public static IReadOnlyList<string> UploadSteps(string zipPath)
    {
        ArgumentNullException.ThrowIfNull(zipPath);
        return new[]
        {
            $"In Claude Desktop open {UploadPath}.",
            $"Choose this file (paste the path into the File name box): {zipPath}",
            "Wait until Claude finishes checking the plugin (about 2 minutes), then start a Cowork task.",
        };
    }

    /// <summary><c>&lt;data dir&gt;\cowork</c>.</summary>
    public static string StateFolder(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        return Path.Combine(dataDirectory, StateFolderName);
    }

    /// <summary><c>&lt;data dir&gt;\cowork\defenseclaw-cowork-plugin.zip</c>, where <c>setup cowork</c> writes the plugin.</summary>
    public static string PluginZipPath(string dataDirectory) => Path.Combine(StateFolder(dataDirectory), PluginZipName);

    /// <summary><c>&lt;data dir&gt;\cowork\plugin-state.json</c>.</summary>
    public static string PluginStatePath(string dataDirectory) => Path.Combine(StateFolder(dataDirectory), PluginStateName);

    /// <summary>
    /// True when a Cowork plugin was built from this data directory: the CLI's own test before it prints the remove-plugin step (<c>_cowork_plugin_built</c>).
    /// Read-only; false for an empty path or a file that cannot be checked.
    /// </summary>
    public static bool PluginStateExists(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return false;
        }

        try
        {
            return File.Exists(PluginStatePath(dataDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
