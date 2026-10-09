namespace DefenseClaw.Core.Setup;

/// <summary>The three lists the Setup editors work on: each is a <c>defenseclaw setup &lt;noun&gt;</c> group of the 0.8.10 CLI.</summary>
public enum SetupResource
{
    /// <summary><c>setup observability</c>: the canonical telemetry destinations (<c>observability.destinations</c> in config.yaml).</summary>
    Observability,

    /// <summary><c>setup webhook</c>: the chat and incident notifiers (the top-level <c>webhooks</c> list).</summary>
    Webhooks,

    /// <summary><c>setup trusted-paths</c>: the directories connector binaries may be run from during discovery.</summary>
    TrustedPaths,
}

/// <summary>
/// The exact command line of every verb the Setup editors run, read from the DefenseClaw 0.8.10 source
/// (<c>commands/cmd_setup_observability.py</c>, <c>cmd_setup_webhook.py</c> and the <c>trusted-paths</c> group of <c>cmd_setup.py</c>) and
/// checked against each command's <c>--help</c>: nothing here is guessed, and nothing here is built from a verb the installed CLI lacks
/// (<c>setup observability</c> has no <c>show</c>).
/// <para>
/// <b>The name comes after <c>--</c>.</b> Every target is given as a positional after the end-of-options marker, so a name that begins with a
/// dash can never be read as a flag, and <see cref="CommandTiers"/> and the review classify only what is before it: a webhook named
/// <c>list</c> or <c>--help</c> cannot turn a removal into a read. The TUI passes the name bare; the CLI parses both the same.
/// </para>
/// <para>
/// <b>Reads and changes.</b> <see cref="List"/> and <see cref="ShowWebhook"/> only read (no verb of theirs writes config.yaml, a secret or
/// the audit log). The first-verb classifier calls every <c>setup ...</c> command a change, so <see cref="IsRead"/> recognises these by their whole
/// shape and <see cref="InstallationGate"/> lets exactly them through on a managed (read-only) installation; <c>enable</c>, <c>disable</c> and
/// <c>test</c> are changes (a test records a local compliance entry; a webhook test delivers a real message), <c>remove</c> is destructive.
/// </para>
/// </summary>
public static class SetupResourceArgv
{
    /// <summary>The longest name the editors will put on a command line.</summary>
    public const int MaxNameLength = 128;

    /// <summary>The setup target name of a resource: the word after <c>setup</c>, and the wizard that adds to it.</summary>
    public static string Noun(SetupResource resource) => resource switch
    {
        SetupResource.Observability => "observability",
        SetupResource.Webhooks => "webhook",
        SetupResource.TrustedPaths => "trusted-paths",
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null),
    };

    /// <summary>The resource whose setup target is <paramref name="target"/> (<c>observability</c>, <c>webhook</c>, <c>trusted-paths</c>), or null for any other target.</summary>
    public static SetupResource? ForTarget(string? target) => target switch
    {
        "observability" => SetupResource.Observability,
        "webhook" => SetupResource.Webhooks,
        "trusted-paths" => SetupResource.TrustedPaths,
        _ => null,
    };

    /// <summary><c>setup &lt;noun&gt; list --json</c>: a JSON array, one object per row.</summary>
    public static IReadOnlyList<string> List(SetupResource resource) => ["setup", Noun(resource), "list", "--json"];

    /// <summary><c>setup webhook show --json -- NAME</c>: the one webhook, as the CLI prints it (its address already cut).</summary>
    public static IReadOnlyList<string> ShowWebhook(string name) => ["setup", "webhook", "show", "--json", "--", name];

    /// <summary><c>setup observability|webhook enable -- NAME</c>.</summary>
    public static IReadOnlyList<string> Enable(SetupResource resource, string name) => Change(resource, "enable", name);

    /// <summary><c>setup observability|webhook disable -- NAME</c>.</summary>
    public static IReadOnlyList<string> Disable(SetupResource resource, string name) => Change(resource, "disable", name);

    /// <summary>
    /// <c>setup observability|webhook test -- NAME</c>. For a destination it connects to each endpoint (a TCP and TLS handshake) and sends
    /// nothing; <c>--write-probe</c>, which sends one marked probe, is not offered. For a webhook it delivers one synthetic event.
    /// </summary>
    public static IReadOnlyList<string> Test(SetupResource resource, string name) => Change(resource, "test", name);

    /// <summary>
    /// <c>setup observability|webhook remove --yes -- NAME</c> (the <c>--yes</c> answers the CLI's "Remove?" question, which this app cannot
    /// answer; the review is where the operator decides), or <c>setup trusted-paths remove -- DIRECTORY</c>.
    /// </summary>
    public static IReadOnlyList<string> Remove(SetupResource resource, string target) => resource == SetupResource.TrustedPaths
        ? ["setup", "trusted-paths", "remove", "--", target]
        : ["setup", Noun(resource), "remove", "--yes", "--", target];

    private static IReadOnlyList<string> Change(SetupResource resource, string verb, string name)
    {
        if (resource == SetupResource.TrustedPaths)
        {
            throw new ArgumentOutOfRangeException(nameof(resource), resource, $"setup trusted-paths has no {verb}");
        }

        return ["setup", Noun(resource), verb, "--", name];
    }

    /// <summary>
    /// True for exactly the command lines <see cref="List"/> and <see cref="ShowWebhook"/> build and nothing else: no extra option, no other
    /// noun, no <c>--connector</c>, no verb that changes. The editors start a command without a review only through this test, and
    /// <see cref="InstallationGate"/> lets only these through on a read-only installation.
    /// </summary>
    public static bool IsRead(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 4)
        {
            return argv[0] == "setup" && argv[2] == "list" && argv[3] == "--json" && argv[1] is "observability" or "webhook" or "trusted-paths";
        }

        return argv.Count == 6 &&
               argv[0] == "setup" && argv[1] == "webhook" && argv[2] == "show" && argv[3] == "--json" && argv[4] == "--" && IsSafeName(argv[5]);
    }

    /// <summary>
    /// True for a name the editors will act on: a letter or digit, then letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, at most
    /// <see cref="MaxNameLength"/> characters. That is wider than what the CLI lets <c>add</c> write (a destination's
    /// <c>^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$</c>, a webhook's lower-case slug) and has none of what Windows would rewrite in an argument
    /// (<c>* ? [ % $ ~</c>), a space or a control character. A row whose name is anything else (a hand-edited config.yaml) is listed, and
    /// is not acted on.
    /// </summary>
    public static bool IsSafeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || !char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
