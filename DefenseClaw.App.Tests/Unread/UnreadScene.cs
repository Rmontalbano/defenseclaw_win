using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Unread;

/// <summary>
/// A scratch installation for the sidebar's "new since last visit" counts (CUST-265): an isolated composition (its real runner and change probe),
/// an <c>audit.db</c> built from the real DDL with invented rows, a settings file of the scene's own, and an <see cref="UnreadCountsService"/> over
/// them whose clock is the test's and whose alert tick is a fake that counts its subscribers. Nothing here can reach a real DefenseClaw install.
/// <para>
/// The service raises <c>Changed</c> in place (no dispatcher), so a scene needs no UI thread; <see cref="PassAsync"/> is what one tick's work is,
/// awaited, so an assertion made after it sees the result of a read that began after the change the test just made.
/// </para>
/// </summary>
internal sealed class UnreadScene : IDisposable
{
    private static readonly string[] NoArguments = { "keys", "set" };

    private readonly List<UnreadCountsService> _services = new();
    private int _rows;

    public UnreadScene(int auditRows = 0, bool withProbe = true)
    {
        Temp = new TempDirectory();
        Services = TestServices.Create(Temp);
        AuditPath = Services.Paths.AuditDatabasePath;
        SettingsPath = Temp.File("seen-settings.json");
        Tick = new FakeSnapshotSource();
        WithProbe = withProbe;

        AuditTestDatabase.Create(AuditPath, auditRows);
        _rows = auditRows;
        Store = AppSettingsStore.OpenFresh(SettingsPath);
        Reader = NewReader();
        Service = Build(Store, Reader);
    }

    public TempDirectory Temp { get; }

    public AppServices Services { get; }

    public string AuditPath { get; }

    public string SettingsPath { get; }

    public FakeSnapshotSource Tick { get; }

    public bool WithProbe { get; }

    /// <summary>The settings store the first service writes through.</summary>
    public AppSettingsStore Store { get; }

    public AuditHeadReader Reader { get; }

    public UnreadCountsService Service { get; }

    /// <summary>
    /// What the service's clock says - the instant a visit to Activity (or the first sight of AI Discovery's file) is marked at - or null for the real
    /// one. A run of the runner is stamped with the real clock whatever this says, so a test that needs a run to fall before or after a visit either
    /// leaves this alone and waits a few milliseconds between the two, or fixes it where the run's own time does not matter (AI Discovery's).
    /// </summary>
    public DateTimeOffset? Now { get; set; }

    /// <summary>Every set of counts <c>Changed</c> carried, in order.</summary>
    public List<IReadOnlyDictionary<string, int>> Published { get; } = new();

    public AuditHeadReader NewReader() =>
        new(AuditPath, WithProbe ? Services.AuditChanges : null, TestTimeouts.Ceiling);

    /// <summary>A service over <paramref name="store"/> and <paramref name="reader"/>: another "process" when the store is a fresh one over the same file.</summary>
    public UnreadCountsService Build(AppSettingsStore store, AuditHeadReader reader)
    {
        var service = new UnreadCountsService(store, reader, Services.Cli, Tick, () => Now ?? DateTimeOffset.UtcNow, static action => action());
        _services.Add(service);
        return service;
    }

    /// <summary>A second process: a store over the same settings file that shares nothing with the first, and a service over it.</summary>
    public UnreadCountsService Restart(out AppSettingsStore store)
    {
        store = AppSettingsStore.OpenFresh(SettingsPath);
        return Build(store, NewReader());
    }

    /// <summary>Subscribes (which is what starts the service) and returns once the catch-up pass it starts has run.</summary>
    public async Task StartAsync(UnreadCountsService? service = null)
    {
        service ??= Service;
        service.Changed += Record;
        Assert.True(service.IsRunning);
        await service.RefreshAsync();
        await WaitUntilAsync(() => service.PassCount >= 2 && !service.IsPassRunning, "the catch-up pass the subscription started");
    }

    private void Record(object? sender, UnreadChangedEventArgs e)
    {
        lock (Published)
        {
            Published.Add(e.Counts);
        }
    }

    /// <summary>Unsubscribes the scene's recorder; the service goes idle if it was the only listener.</summary>
    public void Stop(UnreadCountsService? service = null) => (service ?? Service).Changed -= Record;

    /// <summary>One tick's work, awaited: what a count looks like after a read that began now.</summary>
    public Task PassAsync(UnreadCountsService? service = null) => (service ?? Service).RefreshAsync();

    public int PublishedCount
    {
        get
        {
            lock (Published)
            {
                return Published.Count;
            }
        }
    }

    // ------------------------------------------------------------------ the audit database

    /// <summary>Rows the gateway wrote, committed in one transaction (their times are all within the last day).</summary>
    public void AddAuditRows(int count)
    {
        if (count == 1)
        {
            AuditEventWriter.Add(AuditPath, "scene-" + _rows.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddSeconds(-1));
            _rows++;
            return;
        }

        var first = _rows;
        AuditEventWriter.AddMany(
            AuditPath,
            count,
            i => ("scene-" + (first + i).ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddSeconds(-1), "hook_decision", "INFO", "synthetic extra", "claudecode"));
        _rows += count;
    }

    /// <summary>A scan finding stamped a day before everything else and committed now: the late commit the live refresh is built for.</summary>
    public void AddLateRow()
    {
        AuditEventWriter.Add(AuditPath, "scene-late-" + _rows.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddDays(-1), action: "scan-finding", severity: "HIGH", bucket: "security.finding");
        _rows++;
    }

    public void Exec(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = AuditPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ the runner's list

    /// <summary>Entries in Activity, the way a hand-off to a terminal is recorded: no process, a real entry, the runner's own start event.</summary>
    public void RunCommands(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _ = Services.Cli.RecordHandOff("defenseclaw", NoArguments, "synthetic entry");
        }
    }

    // ------------------------------------------------------------------ waiting

    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + (long)TestTimeouts.Ceiling.TotalMilliseconds;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    public int CountOf(string panelId) => Service.CountOf(panelId);

    /// <summary>Long enough for the system clock to have moved on: a run started after this has a later time than anything marked before it.</summary>
    public static Task ClockMovesOnAsync() => Task.Delay(40);

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        Services.Dispose();
        SqlitePools.Release(Temp.Path);
        Temp.Dispose();
    }
}
