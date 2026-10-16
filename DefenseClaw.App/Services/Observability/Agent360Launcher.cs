using System.ComponentModel;
using System.Diagnostics;

namespace DefenseClaw.App.Services.Observability;

/// <summary>
/// Hands an Agent360 address to the default browser. Opening a page is not a change of state, but the shell opens whatever it is given, so this is the one
/// place that checks it is what <see cref="Agent360Link.Build"/> makes: a loopback http(s) address whose path is the Agent360 dashboard. Anything else
/// is refused and nothing is started.
/// </summary>
internal static class Agent360Launcher
{
    /// <summary>True when <paramref name="url"/> is an address <see cref="Agent360Link"/> could have built.</summary>
    public static bool IsAgent360Url(string? url)
    {
        if (url is null || Agent360Link.TryNormalizeBase(url) is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath == "/d/" + Agent360Link.DashboardUid && uri.Fragment.Length == 0;
    }

    /// <summary>
    /// Opens <paramref name="url"/> through <paramref name="opener"/> (a test's seam) or the shell. Returns false, having started nothing, when the
    /// address is not an Agent360 one or the machine has no browser to give it to.
    /// </summary>
    public static bool TryOpen(string? url, Action<string>? opener = null)
    {
        if (!IsAgent360Url(url))
        {
            return false;
        }

        if (opener is not null)
        {
            opener(url!);
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
