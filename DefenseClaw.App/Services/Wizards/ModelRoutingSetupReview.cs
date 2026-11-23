namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Whether <c>setup routing</c> and <c>setup tokenomics</c> restart the gateway, from the <c>setup</c> group's result callback
/// (<c>_auto_restart_sidecar_after_setup</c>, <c>cmd_setup.py</c> at 1.0.10): it restarts a running gateway only after a subcommand that changed
/// config.yaml, and not even then when every changed key is one the gateway applies in place (<c>_only_hot_config_changes</c>). The general
/// rule in <see cref="CommandReview.RestartsGatewayFor"/> says yes for any <c>setup</c> command that is not a read, so it needs these two.
/// </summary>
public static class ModelRoutingSetupReview
{
    /// <summary>
    /// null when <paramref name="argv"/> is neither command (the general rule decides).
    /// <para>
    /// <c>setup tokenomics</c> (1.0.10 and later; the command does not exist before) writes only <c>observability.tokenomics.*</c> and
    /// <c>observability.pricing.*</c>, hot keys: the callback prints "applies it without a restart" and restarts nothing.
    /// </para>
    /// <para>
    /// <c>setup routing</c>: <c>--status</c> and the bare command print the state and write nothing (<c>setup_routing</c>, <c>cmd_setup.py</c>), so the
    /// callback sees an unchanged config.yaml and restarts nothing. That is a deliberate fix on every runtime that has the command (the general rule
    /// wrongly said "restarts the gateway"). <c>--enable</c> and <c>--disable</c> write <c>routing.enabled</c>, a restart key at every release
    /// (1.0.10 lists nine routing keys, 1.0.9 and older the whole <c>routing</c> section), so they keep the general answer (a running gateway restarts).
    /// </para>
    /// </summary>
    public static bool? RestartsGateway(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count < 2 || !string.Equals(argv[0], "setup", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(argv[1], "tokenomics", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(argv[1], "routing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The command takes no value options, so every token after the path is a flag and a plain search is exact. Nothing after "--" is one.
        var flags = argv.Skip(2).TakeWhile(a => !string.Equals(a, "--", StringComparison.Ordinal)).ToArray();
        var status = flags.Contains("--status", StringComparer.OrdinalIgnoreCase);
        var enable = flags.Contains("--enable", StringComparer.OrdinalIgnoreCase);
        var disable = flags.Contains("--disable", StringComparer.OrdinalIgnoreCase);
        return status || (!enable && !disable) ? false : null;
    }
}
