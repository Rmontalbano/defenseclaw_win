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

    /// <summary>
    /// The warning for the <c>defenseclaw</c> command <paramref name="argv"/> (the arguments without the executable): one bar when the runtime has Cowork
    /// protection, the first verb is <c>uninstall</c> or <c>reset</c>, and the plugin state exists under <paramref name="dataDirectory"/>; otherwise none.
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

        if (!capabilities.Has(RuntimeCapability.CoworkProtection) || !RemovesDefenseClaw(argv))
        {
            return Array.Empty<CommandReviewWarning>();
        }

        var exists = pluginStateExists ?? CoworkProtectionFacts.PluginStateExists;
        return exists(dataDirectory)
            ? new[] { new CommandReviewWarning(Title, CoworkProtectionFacts.RemovePluginStep) }
            : Array.Empty<CommandReviewWarning>();
    }

    /// <summary>True when the first verb is <c>uninstall</c> or <c>reset</c>, the two commands that take DefenseClaw (and its hooks) away.</summary>
    internal static bool RemovesDefenseClaw(IReadOnlyList<string> argv) =>
        argv.Count > 0 &&
        (string.Equals(argv[0], "uninstall", StringComparison.OrdinalIgnoreCase) || string.Equals(argv[0], "reset", StringComparison.OrdinalIgnoreCase));
}
