using System.Diagnostics;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// The interpreter the pseudo-console tests run their one-line stand-ins with, and the stand-ins.
/// <para>
/// The DefenseClaw CLI reads a secret with <c>getpass.win_getpass</c>, which reads the console and not stdin; a Python one-liner that calls
/// <c>getpass.getpass</c> takes exactly the same path and touches nothing of DefenseClaw, so it is the stand-in for <c>keys set</c>. The real
/// <c>defenseclaw</c> is never started by these tests. The interpreter is found where a developer machine and a CI runner have one: the
/// runtime the DefenseClaw installer ships (the interpreter itself, run isolated, importing nothing of DefenseClaw), then the first real
/// <c>python.exe</c> on PATH. Where there is none the facts are skipped, like <see cref="LiveFactAttribute"/>.
/// </para>
/// </summary>
public static class PythonStandIn
{
    /// <summary>A prompt that reads one value and prints how long it was: the whole of what <c>keys set</c> asks of the console.</summary>
    public const string ReadOneValue = "import getpass; v = getpass.getpass('Value: '); print(len(v))";

    /// <summary>The interpreter, or null where there is none.</summary>
    public static string? Path { get; } = Locate();

    /// <summary>The arguments that run <paramref name="code"/> isolated (no site, no environment, no script directory on the path).</summary>
    public static string[] Run(string code) => new[] { "-I", "-c", code };

    /// <summary>
    /// True when the interpreter can <c>import click</c>: the runtime the DefenseClaw installer ships can (it is what the CLI is written with), a
    /// bare interpreter cannot. The hidden prompt of <c>keys set</c> is <c>click.prompt(hide_input=True)</c>, so a stand-in that calls it is the
    /// closest to the real thing that still imports nothing of DefenseClaw.
    /// </summary>
    public static bool HasClick { get; } = Path is not null && Probe("import click");

    private static bool Probe(string code)
    {
        try
        {
            var info = new ProcessStartInfo(Path!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in Run(code))
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? Locate()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0)
        {
            var shipped = System.IO.Path.Combine(local, "Programs", "DefenseClaw", "runtime", "python", "python.exe");
            if (System.IO.File.Exists(shipped))
            {
                return shipped;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // The Microsoft Store's "python.exe" is an app-execution alias: a zero-length file that opens the Store, not an interpreter.
            if (directory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var candidate = System.IO.Path.Combine(directory.Trim().Trim('"'), "python.exe");
                if (System.IO.File.Exists(candidate) && new System.IO.FileInfo(candidate).Length > 0)
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry that is not a path.
            }
        }

        return null;
    }

    /// <summary>True while the process is still there: a test that waits for a kill asks this, never a fixed sleep.</summary>
    public static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A fact that needs <see cref="PythonStandIn.Path"/> and is skipped where there is no interpreter.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (PythonStandIn.Path is null)
        {
            Skip = "No Python interpreter was found on this machine.";
        }
    }
}

/// <summary>A fact that needs an interpreter that has Click (see <see cref="PythonStandIn.HasClick"/>) and is skipped where there is none.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClickFactAttribute : FactAttribute
{
    public ClickFactAttribute()
    {
        if (!PythonStandIn.HasClick)
        {
            Skip = "No Python interpreter that can import click was found on this machine.";
        }
    }
}

/// <summary><see cref="PythonFactAttribute"/> for a theory.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PythonTheoryAttribute : TheoryAttribute
{
    public PythonTheoryAttribute()
    {
        if (PythonStandIn.Path is null)
        {
            Skip = "No Python interpreter was found on this machine.";
        }
    }
}
