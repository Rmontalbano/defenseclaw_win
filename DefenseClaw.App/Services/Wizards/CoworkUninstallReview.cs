using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What the review of <c>defenseclaw uninstall</c> or <c>defenseclaw reset</c> says about the Cowork plugin (runtime 1.0.8, CUST-445). DefenseClaw cannot remove
/// its plugin from the user's Claude account: until the user removes it in Claude Desktop, every Cowork tool call is blocked, because the plugin's hooks find
/// no DefenseClaw. The CLI prints one line about it when <c>&lt;data dir&gt;\cowork\plugin-state.json</c> exists; this puts the same sentence in the review,
/// before anything runs, rather than only in the console afterwards. The check is a read-only look at one file.
/// </summary>
public static class CoworkUninstallReview
{
    /// <summary>The heading of the bar.</summary>
    public const string Title = "Remove the Cowork plugin in Claude Desktop";

    /// <summary>The heading of the bar for <c>setup remove claudecode</c> (1.0.9, CUST-453).</summary>
    public const string ConnectorTitle = "Cowork stops being protected";

    /// <summary>The message of that bar on 1.0.9, from the CLI's own sentence (cmd_setup.py@1.0.9:14786-14792, printed only after the removal).</summary>
    public const string ConnectorMessage = "Cowork is no longer protected after this; remove the DefenseClaw plugin in Claude (Customize > Plugins).";

    /// <summary>
    /// The message on 1.0.8, which prints no such line. What 1.0.8 does show: with the Claude Code connector off, <c>cowork status</c> reports "not protected" with
    /// reason <c>cowork_claudecode_off</c> (cowork.py@1.0.8:503-510), and only the user can take the plugin out of the Claude account (REMOVE_PATH_TEXT).
    /// It does not repeat the uninstall sentence's "tool calls are blocked", which 1.0.8 states for uninstall and reset only.
    /// </summary>
    public const string ConnectorMessage108 =
        "If the removal succeeds, Cowork is no longer protected: its plugin runs the Claude Code connector's hooks, and Cowork status then reports it as not protected. " +
        "The plugin stays in your Claude account until you remove it there (" + CoworkProtectionFacts.RemovePath + ").";

    /// <summary>The connector bar's message for <paramref name="capabilities"/>: the CLI's own line from 1.0.9, the 1.0.8 wording before.</summary>
    public static string ConnectorMessageFor(RuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.Has(RuntimeCapability.CoworkPluginRunsAfterRemoval) ? ConnectorMessage : ConnectorMessage108;
    }

    /// <summary>
    /// The warning for the <c>defenseclaw</c> command <paramref name="argv"/> (the arguments without the executable): one bar when the runtime has Cowork
    /// protection, the plugin state exists under <paramref name="dataDirectory"/>, and the command is <c>uninstall</c> or <c>reset</c> (the remove-plugin
    /// sentence for the runtime), or <c>setup remove</c> of the Claude Code connector under any of its names (<c>claudecode</c>, <c>claude</c>,
    /// <c>claude-code</c>, <c>claude_code</c>, any case; connector_contracts.py@1.0.9:120-124); otherwise none.
    /// The caller passes only the DefenseClaw CLI's argv (the gateway's verbs are not this CLI's).
    /// </summary>
    /// <param name="pluginStateExists">Tells whether the plugin state exists for a data directory; a test supplies its own. Defaults to <see cref="CoworkProtectionFacts.PluginStateExists"/>.</param>
    public static IReadOnlyList<CommandReviewWarning> Warnings(
        IReadOnlyList<string> argv,
        RuntimeCapabilities capabilities,
        string? dataDirectory,
        Func<string?, bool>? pluginStateExists = null)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (!capabilities.Has(RuntimeCapability.CoworkProtection))
        {
            return Array.Empty<CommandReviewWarning>();
        }

        var removesAll = RemovesDefenseClaw(argv);
        if (!removesAll && !RemovesClaudeCodeConnector(argv))
        {
            return Array.Empty<CommandReviewWarning>();
        }

        var exists = pluginStateExists ?? CoworkProtectionFacts.PluginStateExists;
        if (!exists(dataDirectory))
        {
            return Array.Empty<CommandReviewWarning>();
        }

        return removesAll
            ? new[] { new CommandReviewWarning(Title, CoworkProtectionFacts.RemovePluginStepFor(capabilities)) }
            : new[] { new CommandReviewWarning(ConnectorTitle, ConnectorMessageFor(capabilities)) };
    }

    /// <summary>
    /// True for <c>setup remove &lt;connector&gt;</c> where the connector is Claude Code under any of the CLI's names (connector_contracts.py@1.0.9:120-124: stripped,
    /// lower-cased; <c>claude</c>, <c>claude-code</c> and <c>claude_code</c> mean <c>claudecode</c>). The connector is the first argument after <c>remove</c> that
    /// is not an option; every option of the command is a flag.
    /// </summary>
    internal static bool RemovesClaudeCodeConnector(IReadOnlyList<string> argv)
    {
        if (argv.Count < 3 ||
            !string.Equals(argv[0], "setup", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(argv[1], "remove", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 2; i < argv.Count; i++)
        {
            var token = argv[i].Trim();
            if (token.Length == 0 || token.StartsWith('-'))
            {
                continue;
            }

            return token.ToLowerInvariant() is "claudecode" or "claude" or "claude-code" or "claude_code";
        }

        return false;
    }

    /// <summary>True when the first verb is <c>uninstall</c> or <c>reset</c>, the two commands that take DefenseClaw (and its hooks) away.</summary>
    internal static bool RemovesDefenseClaw(IReadOnlyList<string> argv) =>
        argv.Count > 0 &&
        (string.Equals(argv[0], "uninstall", StringComparison.OrdinalIgnoreCase) || string.Equals(argv[0], "reset", StringComparison.OrdinalIgnoreCase));
}
