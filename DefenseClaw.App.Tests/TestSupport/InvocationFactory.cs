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

    /// <param name="retainFullOutput">True for the ceiling a <see cref="CliRunOptions.JsonRead"/> run gets (200,000 lines).</param>
    public static CliInvocation Create(bool retainFullOutput = false, params string[] argv) =>
        (CliInvocation)Constructor.Invoke(new object[]
        {
            @"C:\test\defenseclaw.exe",
            argv.Length == 0 ? new[] { "doctor" } : argv,
            DateTimeOffset.UtcNow,
            retainFullOutput,
        });

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

    private static PropertyInfo Property(string name) =>
        typeof(CliInvocation).GetProperty(name, Internal) ?? throw new MissingMemberException(nameof(CliInvocation), name);
}
