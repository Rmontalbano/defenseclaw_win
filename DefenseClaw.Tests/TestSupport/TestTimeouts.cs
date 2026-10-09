namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// How long a test lets something that happens on its own take (a process starting, a read finishing) before it calls it hung.
/// <para>
/// The waits in this suite are conditions polled up to a ceiling, never a fixed sleep, so a generous ceiling costs nothing when things
/// are healthy and only bounds a genuine hang. It has to be generous because the same test can run on a quiet laptop and on a CI runner
/// where the two test projects, xunit's parallel classes and the build servers share a few cores: starting <c>powershell.exe</c> and having
/// it start a process took 0.9 s on a quiet machine and 23 to 32 s with sixteen busy processes sharing two cores (measured), and a
/// fixed 30 s bound for exactly that failed on CI twice in a row (CUST-301).
/// </para>
/// </summary>
public static class TestTimeouts
{
    /// <summary>True on a CI runner: GitHub Actions (and most other CI systems) set <c>CI</c> and, for Actions, <c>GITHUB_ACTIONS</c>.</summary>
    public static bool OnCi { get; } = IsSet("CI") || IsSet("GITHUB_ACTIONS");

    /// <summary>
    /// The ceiling for one condition wait: two minutes on a development machine (which may be busy with a build or another test run),
    /// five on CI. Use it for a wait that ends when something happens; keep a short, exact bound only where the test is about the bound
    /// itself.
    /// </summary>
    public static TimeSpan Ceiling { get; } = TimeSpan.FromSeconds(OnCi ? 300 : 120);

    private static bool IsSet(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 };
}
