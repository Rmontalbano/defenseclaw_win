namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>The only things the helper does. There is no free-form argument beside one of these.</summary>
public enum AdminVerb
{
    /// <summary>Put (or refresh) the helper and the administrator copy of the gateway in Program Files.</summary>
    InstallCopy,

    /// <summary>Run <c>defenseclaw-gateway start</c> from the administrator copy.</summary>
    Start,

    /// <summary>Stop the running gateway, refresh the copy from the installed gateway, start again.</summary>
    Restart,

    /// <summary>Run <c>defenseclaw-gateway stop</c> from the administrator copy.</summary>
    Stop,

    /// <summary>Give files under the user's <c>.defenseclaw</c> that an elevated gateway made back to the user.</summary>
    RepairOwnership,

    /// <summary>Print what the helper can see (no change).</summary>
    Status,
}

/// <summary>Exit codes of the helper. 64 and 77 are the Mac helper's.</summary>
public static class AdminExit
{
    public const int Ok = 0;
    public const int Failed = 1;

    /// <summary>The command line was not exactly one known verb.</summary>
    public const int Usage = 64;

    /// <summary>The helper refused: nothing was changed. The text on stdout and in the result file says why.</summary>
    public const int Refused = 77;

    /// <summary>The operator declined the helper's own confirmation.</summary>
    public const int Declined = 130;

    /// <summary>What <c>ShellExecuteEx</c> reports (as a Win32 error) when the operator dismisses the UAC prompt.</summary>
    public const int UacCancelled = 1223;
}

/// <summary>
/// The command line of the helper: exactly one argument, exactly one of the verb names, in lower case. Anything else - no argument, two
/// arguments, an unknown word, a different case, a leading <c>/</c> or <c>-</c>, an embedded space - is refused before anything runs. Paths are
/// never an argument: <see cref="AdminLayout"/> derives them.
/// </summary>
public static class AdminVerbs
{
    public static string Name(AdminVerb verb) => verb switch
    {
        AdminVerb.InstallCopy => "install-copy",
        AdminVerb.Start => "start",
        AdminVerb.Restart => "restart",
        AdminVerb.Stop => "stop",
        AdminVerb.RepairOwnership => "repair-ownership",
        AdminVerb.Status => "status",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    /// <summary>Every verb, in the order they are documented.</summary>
    public static IReadOnlyList<AdminVerb> All { get; } = Enum.GetValues<AdminVerb>();

    /// <summary>True for the verbs that change something and so need an elevated token.</summary>
    public static bool Changes(AdminVerb verb) => verb != AdminVerb.Status;

    public static bool TryParse(IReadOnlyList<string>? args, out AdminVerb verb, out string problem)
    {
        verb = default;
        problem = string.Empty;

        if (args is null || args.Count == 0)
        {
            problem = "A verb is required: " + Usage();
            return false;
        }

        if (args.Count != 1)
        {
            problem = "Exactly one verb is accepted and no other argument: " + Usage();
            return false;
        }

        var word = args[0];
        foreach (var candidate in All)
        {
            if (string.Equals(word, Name(candidate), StringComparison.Ordinal))
            {
                verb = candidate;
                return true;
            }
        }

        problem = "Unknown verb. " + Usage();
        return false;
    }

    public static string Usage() => string.Join(" | ", All.Select(Name));
}
