using System.Reflection;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Builds real <see cref="CliInvocation"/> instances without starting a process, so a test can feed the exact
/// retention, trimming and cursor behavior of the runner's own type to a view-model deterministically and in
/// microseconds. The constructor, <c>Append</c> and the completion setters are internal to <c>DefenseClaw.Core</c>
/// (only <see cref="CliRunner"/> may call them), so they are reached by reflection; if their shape changes the
/// lookups below fail loudly at first use rather than silently testing a stand-in.
/// </summary>
internal static class InvocationFactory
{
    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly ConstructorInfo Constructor = typeof(CliInvocation).GetConstructor(
        Internal,
        binder: null,
        new[] { typeof(string), typeof(IReadOnlyList<string>), typeof(DateTimeOffset), typeof(bool) },
        modifiers: null)
        ?? throw new MissingMethodException("CliInvocation(string, IReadOnlyList<string>, DateTimeOffset, bool)");

    private static readonly MethodInfo AppendMethod = typeof(CliInvocation).GetMethod("Append", Internal)
        ?? throw new MissingMethodException("CliInvocation.Append");

    private static readonly PropertyInfo FinishedAt = Property(nameof(CliInvocation.FinishedAt));

    private static readonly PropertyInfo ExitCode = Property(nameof(CliInvocation.ExitCode));

    private static readonly PropertyInfo EnvironmentNames = Property(nameof(CliInvocation.EnvironmentNames));

    private static readonly PropertyInfo FailureReason = Property(nameof(CliInvocation.FailureReason));

    private static readonly PropertyInfo UsedStdinSecret = Property(nameof(CliInvocation.UsedStdinSecret));

    private static readonly PropertyInfo UsedPromptSecret = Property(nameof(CliInvocation.UsedPromptSecret));

    private static readonly PropertyInfo SurvivesShutdown = Property(nameof(CliInvocation.SurvivesShutdown));

    private static readonly PropertyInfo CancelRequested = Property(nameof(CliInvocation.CancelRequested));

    /// <param name="retainFullOutput">True for the ceiling a <see cref="CliRunOptions.JsonRead"/> run gets (200,000 lines).</param>
    public static CliInvocation Create(bool retainFullOutput = false, params string[] argv) =>
        (CliInvocation)Constructor.Invoke(new object[]
        {
            @"C:\test\defenseclaw.exe",
            argv.Length == 0 ? new[] { "doctor" } : argv,
            DateTimeOffset.UtcNow,
            retainFullOutput,
        });

    /// <summary>
    /// An invocation of <paramref name="executable"/> (a file name or a path: <c>defenseclaw-gateway</c>, <c>C:\tools\cosign.exe</c>), started at
    /// <paramref name="startedAt"/> (now when null), so a test can say what a run was before and after a file was written.
    /// </summary>
    public static CliInvocation CreateFor(string executable, string[] argv, DateTimeOffset? startedAt = null) =>
        (CliInvocation)Constructor.Invoke(new object[] { executable, argv, startedAt ?? DateTimeOffset.UtcNow, false });

    public static void Append(CliInvocation invocation, string text, CliStream stream = CliStream.StandardOutput) =>
        AppendMethod.Invoke(invocation, new object[] { new CliOutputLine(DateTimeOffset.UtcNow, stream, text) });

    /// <summary>Appends <c>prefix 1</c> .. <c>prefix count</c> (numbered from <paramref name="first"/>).</summary>
    public static void AppendNumbered(CliInvocation invocation, int count, string prefix = "line", int first = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Append(invocation, $"{prefix} {first + i}");
        }
    }

    public static void Finish(CliInvocation invocation, int exitCode = 0)
    {
        FinishedAt.SetValue(invocation, DateTimeOffset.UtcNow);
        ExitCode.SetValue(invocation, exitCode);
    }

    /// <summary>Ends the run at <paramref name="finishedAt"/> with <paramref name="exitCode"/>.</summary>
    public static void Finish(CliInvocation invocation, int exitCode, DateTimeOffset finishedAt)
    {
        FinishedAt.SetValue(invocation, finishedAt);
        ExitCode.SetValue(invocation, exitCode);
    }

    /// <summary>Marks the run as having been given a secret on stdin (the secret itself is never recorded).</summary>
    public static void UseStdinSecret(CliInvocation invocation) => UsedStdinSecret.SetValue(invocation, true);

    /// <summary>Marks the run as one whose secret the app typed at the command's hidden prompt in a pseudo-console (the secret itself is never recorded).</summary>
    public static void UsePromptSecret(CliInvocation invocation) => UsedPromptSecret.SetValue(invocation, true);

    /// <summary>Marks the run as one that outlives the app (the upgrade installer).</summary>
    public static void SurviveShutdown(CliInvocation invocation) => SurvivesShutdown.SetValue(invocation, true);

    /// <summary>Marks the run as being cancelled: the operator pressed Cancel and the process tree has not finished going.</summary>
    public static void RequestCancel(CliInvocation invocation) => CancelRequested.SetValue(invocation, true);

    /// <summary>Ends the run without an exit code, as a timeout or a cancel does: the runner records why in <see cref="CliInvocation.FailureReason"/>.</summary>
    public static void Fail(CliInvocation invocation, string reason)
    {
        FinishedAt.SetValue(invocation, DateTimeOffset.UtcNow);
        FailureReason.SetValue(invocation, reason);
    }

    /// <summary>Marks the run as having carried secrets in its environment (names only, as the runner records them).</summary>
    public static void UseEnvironmentSecret(CliInvocation invocation, params string[] names) =>
        EnvironmentNames.SetValue(invocation, names);

    private static PropertyInfo Property(string name) =>
        typeof(CliInvocation).GetProperty(name, Internal) ?? throw new MissingMemberException(nameof(CliInvocation), name);
}
