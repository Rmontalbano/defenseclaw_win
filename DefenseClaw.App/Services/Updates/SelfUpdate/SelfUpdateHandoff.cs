using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>A wait for "the new copy has started": a named event in production, a fake in a test.</summary>
internal interface IStartedSignal : IDisposable
{
    /// <summary>The name the successor is told (<c>--update-signal</c>).</summary>
    string Name { get; }

    /// <summary>True when the successor reported in within <paramref name="timeout"/>.</summary>
    bool Wait(TimeSpan timeout);
}

/// <summary>
/// The old copy's end of the hand-off, on a named event in the user's session (<c>Local\</c>). Created before the successor is started, so
/// the successor can only ever open an event that exists.
/// </summary>
internal sealed class NamedStartedSignal : IStartedSignal
{
    private readonly EventWaitHandle _event;

    public NamedStartedSignal()
    {
        Name = SelfUpdateHandshake.NewName();
        _event = new EventWaitHandle(false, EventResetMode.ManualReset, SelfUpdateHandshake.EventPath(Name));
    }

    public string Name { get; }

    public bool Wait(TimeSpan timeout) => _event.WaitOne(timeout);

    public void Dispose() => _event.Dispose();
}

/// <summary>
/// What an update's restart needs beyond a plain restart: the file to start (the new exe at the app's own path, not the renamed running
/// one), the signal to wait for, how long, and what to do when it never comes.
/// </summary>
internal sealed class UpdateHandoff
{
    /// <summary>How long the old copy waits for the new one to say it started before it restores the old files and stays.</summary>
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(30);

    public required string ExecutablePath { get; init; }

    public required IStartedSignal Signal { get; init; }

    public TimeSpan Timeout { get; init; } = DefaultStartTimeout;

    /// <summary>Puts the previous files back; returns what could not be restored (empty when all of it was).</summary>
    public required Func<IReadOnlyList<string>> Rollback { get; init; }
}

/// <summary>The successor's end: <c>--update-signal &lt;name&gt;</c> on its command line, answered first thing at start.</summary>
internal static partial class SelfUpdateHandshake
{
    public const string Flag = "--update-signal";

    private const string Prefix = "DefenseClawWin.SelfUpdate.";

    [GeneratedRegex("^DefenseClawWin\\.SelfUpdate\\.[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static string NewName() => Prefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public static string EventPath(string name) => "Local\\" + name;

    /// <summary>The signal name after the flag, or null when absent or not of the exact form we make (anything else is ignored: it names an event we did not create).</summary>
    public static string? ParseName(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase))
            {
                return NamePattern().IsMatch(args[i + 1]) ? args[i + 1] : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Sets the event the old copy waits on. Only an event that already exists is opened (never created), so a stray flag cannot make one.
    /// True when it was set.
    /// </summary>
    public static bool SignalStarted(IReadOnlyList<string> args, Func<string, bool>? set = null)
    {
        if (ParseName(args) is not { } name)
        {
            return false;
        }

        if (set is not null)
        {
            return set(name);
        }

        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventPath(name), out var handle))
            {
                return false;
            }

            using (handle)
            {
                return handle.Set();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}
