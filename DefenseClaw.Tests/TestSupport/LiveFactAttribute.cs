using DefenseClaw.Core.Paths;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// A fact that needs the real <c>defenseclaw</c> CLI (found the way the app finds it: PATH, then the installer's bin
/// directory) and is skipped where there is none, such as a CI runner. It may only run read-only commands
/// (<c>status</c>, <c>list</c>, <c>show</c>): it is pointed at the developer's real install.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (LiveCli.Path is null)
        {
            Skip = "The defenseclaw CLI is not installed on this machine.";
        }
    }
}

/// <summary>Where the real CLI is, if anywhere.</summary>
public static class LiveCli
{
    public static string? Path { get; } = new DefenseClawPaths().FindExecutable("defenseclaw");
}
