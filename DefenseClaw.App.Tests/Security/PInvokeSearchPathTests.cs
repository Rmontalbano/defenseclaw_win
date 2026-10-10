using System.Reflection;
using System.Runtime.InteropServices;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Security;

/// <summary>
/// Every P/Invoke in the app and in Core must say where the OS may load its DLL from: <c>System32</c> only. Without that, a
/// <c>user32.dll</c> or <c>kernel32.dll</c> call resolves through the application directory first, so a planted DLL next to the
/// executable would run. The attribute is a per-method guard, so a new declaration without it is an easy mistake; this test is
/// what catches it. Reflection only: no P/Invoke is invoked.
/// </summary>
public sealed class PInvokeSearchPathTests
{
    private const string LibraryImportName = "System.Runtime.InteropServices.LibraryImportAttribute";

    [Fact]
    public void Every_P_invoke_in_DefenseClaw_Core_restricts_its_DLL_search_to_System32()
    {
        var (pinvokes, missing) = Scan(typeof(CliRunner).Assembly.GetTypes());

        Assert.True(pinvokes >= 20, $"The scan found only {pinvokes} P/Invoke declarations in DefenseClaw.Core; it is not looking at them.");
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_P_invoke_in_DefenseClaw_App_restricts_its_DLL_search_to_System32()
    {
        var (pinvokes, missing) = Scan(typeof(ShieldIconFactory).Assembly.GetTypes());

        Assert.True(pinvokes >= 5, $"The scan found only {pinvokes} P/Invoke declarations in DefenseClaw.App; it is not looking at them.");
        Assert.Empty(missing);
    }

    [Fact]
    public void The_scan_flags_a_P_invoke_that_lacks_the_attribute()
    {
        var (pinvokes, missing) = Scan([typeof(Unhardened), typeof(Hardened)]);

        Assert.Equal(3, pinvokes);
        var flagged = Assert.Single(missing);
        Assert.Equal(typeof(Unhardened).FullName + "." + nameof(Unhardened.GetTickCount64), flagged);
    }

    /// <summary>
    /// Counts the P/Invoke declarations (the runtime's own marker, and LibraryImport's, which generates the call) and names the
    /// ones whose search path is not System32 only.
    /// </summary>
    private static (int PInvokes, List<string> Missing) Scan(IEnumerable<Type> types)
    {
        var pinvokes = 0;
        var missing = new List<string>();
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var isLibraryImport = method.GetCustomAttributesData().Any(d => d.AttributeType.FullName == LibraryImportName);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0 && !isLibraryImport)
                {
                    continue;
                }

                pinvokes++;
                var search = method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
                if (search is null || search.Paths != DllImportSearchPath.System32)
                {
                    missing.Add(type.FullName + "." + method.Name);
                }
            }
        }

        return (pinvokes, missing);
    }

    private static class Unhardened
    {
        [DllImport("kernel32.dll")]
        internal static extern ulong GetTickCount64();
    }

    private static class Hardened
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll")]
        internal static extern ulong GetTickCount64();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);
    }
}
