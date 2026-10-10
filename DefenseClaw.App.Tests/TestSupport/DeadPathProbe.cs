using System.Collections.Concurrent;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A filesystem probe with one dead PATH entry: any probe of it blocks (as a <c>File.Exists</c> on a UNC path to a
/// host that is down does, for ~40 s) until <see cref="Release"/>, then reports "not there". Everything else is
/// answered from the set of paths marked <see cref="Install"/>ed. Nothing touches the network or the disk; the entry is
/// TEST-NET-1 (never routable) and only the prefix is recognised.
/// </summary>
internal sealed class DeadPathProbe : IDisposable
{
    public const string DeadEntry = @"\\192.0.2.1\tools";

    private readonly ManualResetEventSlim _release = new(false);
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ConcurrentDictionary<string, byte> _installed = new(StringComparer.OrdinalIgnoreCase);

    public DeadPathProbe Install(string path)
    {
        _installed[path] = 0;
        return this;
    }

    public bool Exists(string path)
    {
        if (path.StartsWith(DeadEntry, StringComparison.OrdinalIgnoreCase))
        {
            _entered.Set();
            _ = _release.Wait(TimeSpan.FromSeconds(30));
            return false;
        }

        return _installed.ContainsKey(path);
    }

    /// <summary>Paths whose only PATH entry is the dead one, so every lookup has to get past it.</summary>
    /// <param name="temp">Data directory.</param>
    /// <param name="binDirectory">The installer directory, which is probed BEFORE PATH (CUST-248): anything installed there is found without meeting the dead entry.</param>
    /// <param name="afterDead">A second PATH entry, behind the dead one, for a test that wants the lookup to get past it to find something.</param>
    public DefenseClawPaths PathsFor(TempDirectory temp, string binDirectory, string? afterDead = null) =>
        new(
            dataDirectory: temp.Path,
            binDirectory: binDirectory,
            searchPath: afterDead is null ? new[] { DeadEntry } : new[] { DeadEntry, afterDead },
            fileExists: Exists);

    public void WaitUntilBlocked() =>
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(10)), "no lookup ever reached the dead PATH entry");

    public void Release() => _release.Set();

    // Releases anything still blocked. The events are not disposed: a scan on a pool thread may still be probing the
    // second file name of the dead entry, and touching a disposed event there would fault a task nobody awaits.
    public void Dispose() => Release();
}
