using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// A scratch data directory holding the files AI Discovery reads - a state file from a fixture, optionally an <c>inventory.db</c> and an
/// <c>audit.db</c> built from invented rows - and a view-model that has read them (its disk phase only: no process is started and no
/// request is made, so nothing here can reach a real DefenseClaw install). The view-model is built and loaded on the shared UI thread.
/// </summary>
internal sealed class DiscoveryScene : IDisposable
{
    /// <summary>The 0.8.10 tables AI Discovery reads (the live DDL, without the rollup view the app does not read).</summary>
    private const string InventorySchema = """
        CREATE TABLE ai_scans (
            scan_id TEXT PRIMARY KEY, scanned_at DATETIME NOT NULL, duration_ms INTEGER NOT NULL,
            source TEXT NOT NULL, privacy_mode TEXT NOT NULL, result TEXT NOT NULL,
            total_signals INTEGER NOT NULL, active_signals INTEGER NOT NULL, files_scanned INTEGER NOT NULL
        );
        CREATE TABLE ai_signals (
            scan_id TEXT NOT NULL REFERENCES ai_scans(scan_id) ON DELETE CASCADE,
            fingerprint TEXT NOT NULL, signal_id TEXT NOT NULL, signature_id TEXT NOT NULL, name TEXT NOT NULL,
            vendor TEXT NOT NULL, product TEXT NOT NULL, category TEXT NOT NULL, detector TEXT NOT NULL,
            state TEXT NOT NULL, confidence REAL NOT NULL, component_ecosystem TEXT, component_name TEXT,
            component_framework TEXT, component_version TEXT, last_seen DATETIME NOT NULL,
            last_active_at DATETIME, evidence_json TEXT, runtime_json TEXT, model_json TEXT,
            PRIMARY KEY (scan_id, fingerprint)
        );
        CREATE TABLE ai_confidence_snapshots (
            scan_id TEXT NOT NULL REFERENCES ai_scans(scan_id) ON DELETE CASCADE,
            ecosystem TEXT NOT NULL, name TEXT NOT NULL, identity_score REAL NOT NULL, identity_band TEXT NOT NULL,
            presence_score REAL NOT NULL, presence_band TEXT NOT NULL, policy_version INTEGER NOT NULL,
            detectors TEXT, factors_json TEXT, PRIMARY KEY (scan_id, ecosystem, name)
        );
        """;

    private DiscoveryScene(TempDirectory temp, AppServices services, AiDiscoveryPanelViewModel viewModel)
    {
        Temp = temp;
        Services = services;
        ViewModel = viewModel;
    }

    public TempDirectory Temp { get; }

    public AppServices Services { get; }

    public AiDiscoveryPanelViewModel ViewModel { get; }

    /// <summary>
    /// Writes the files (<paramref name="stateFixture"/> as <c>ai_discovery_state.json</c>, when given; whatever <paramref name="seed"/> adds),
    /// builds the services over them and runs the view-model's disk phase.
    /// </summary>
    public static DiscoveryScene Open(string? stateFixture, string? configYaml = null, Action<TempDirectory>? seed = null)
    {
        var temp = new TempDirectory();
        if (stateFixture is not null)
        {
            _ = temp.WriteFile("ai_discovery_state.json", PayloadFixtures.Read(stateFixture));
        }

        seed?.Invoke(temp);
        var services = TestServices.Create(temp, configYaml);

        AiDiscoveryPanelViewModel? viewModel = null;
        Task? load = null;
        UiThread.Run(() =>
        {
            viewModel = new AiDiscoveryPanelViewModel(services);
            load = viewModel.LoadFromDiskAsync(CancellationToken.None);
        });
        UiThread.WaitFor(() => load!.IsCompleted, "the disk phase of the AI Discovery load");
        load!.GetAwaiter().GetResult();

        return new DiscoveryScene(temp, services, viewModel!);
    }

    /// <summary>Config that turns AI Discovery on, so an empty list is "found nothing" rather than "turned off".</summary>
    public const string DiscoveryOn = "ai_discovery:\n  enabled: true\n  mode: enhanced\n";

    /// <summary>Reads the files again (a Refresh), on the UI thread, and returns when the view-model has finished.</summary>
    public void Reload()
    {
        Task? load = null;
        UiThread.Run(() => load = ViewModel.LoadFromDiskAsync(CancellationToken.None));
        UiThread.WaitFor(() => load!.IsCompleted, "a second disk phase of the AI Discovery load");
        load!.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asks the gateway for its AI usage report the way a load does - but of <paramref name="answer"/>, a script, so no request is made - and
    /// returns when the view-model has taken the answer.
    /// </summary>
    public void LoadUsage(Func<GatewayResult<JsonDocument>> answer)
    {
        Task? load = null;
        UiThread.Run(() =>
        {
            ViewModel.ReadUsage = _ => Task.FromResult(answer());
            load = ViewModel.LoadUsageAsync(CancellationToken.None);
        });
        UiThread.WaitFor(() => load!.IsCompleted, "the gateway's AI usage report");
        load!.GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="body"/> on the UI thread (the view-model's collection views belong to it).</summary>
    public void OnUi(Action body) => UiThread.Run(body);

    public T OnUi<T>(Func<T> body) => UiThread.Run(body);

    public void Dispose()
    {
        Services.Dispose();
        Temp.Dispose();
    }

    // ---- inventory.db -----------------------------------------------------------------------------------------

    /// <summary>The one scan of the invented databases: a full scan that finished ok.</summary>
    private const string ScanRow =
        "INSERT INTO ai_scans VALUES ('scan-1', '2026-10-08 15:20:00 +0000 UTC', 100, 'scheduled', 'enhanced', 'ok', 4, 4, 1000)";

    /// <summary>
    /// The signal of the 0.8.10 state-file fixture's SDK component, as <c>inventory.db</c> holds it: the rollup that gives a component its
    /// confidence snapshot starts from the signals of the latest scan that name a component.
    /// </summary>
    public const string SdkSignalRow =
        "INSERT INTO ai_signals (scan_id, fingerprint, signal_id, signature_id, name, vendor, product, category, detector, state, confidence, " +
        "component_ecosystem, component_name, component_framework, component_version, last_seen, evidence_json) " +
        "VALUES ('scan-1', 'fp-sdk', 'sig-sdk', 'example-sdk', 'example-sdk', 'Example Corp', 'Example SDK', 'package_dependency', 'package_manifest', " +
        "'seen', 0.9, 'pypi', 'example-sdk', 'Example Python SDK', '1.4.2', '2026-10-08 15:20:00 +0000 UTC', '[]')";

    /// <summary>The engine's snapshot for that component, in different letter case from the signal's (the join folds case).</summary>
    public const string SdkSnapshotRow =
        "INSERT INTO ai_confidence_snapshots (scan_id, ecosystem, name, identity_score, identity_band, presence_score, presence_band, policy_version) " +
        "VALUES ('scan-1', 'PyPI', 'Example-SDK', 0.86, 'high', 0.31, 'low', 1)";

    /// <summary>The statements of a database with its one scan and <paramref name="extra"/> rows.</summary>
    public static string[] InventoryOf(params string[] extra) => new[] { ScanRow }.Concat(extra).ToArray();

    /// <summary>Creates <paramref name="path"/> with the 0.8.10 tables and runs <paramref name="statements"/> (invented rows) in them.</summary>
    public static void WriteInventory(string path, params string[] statements)
    {
        using (var connection = Open(path))
        {
            Execute(connection, InventorySchema);
            foreach (var statement in statements)
            {
                Execute(connection, statement);
            }
        }

        SqlitePools.Release(path);
    }

    // ---- audit.db ---------------------------------------------------------------------------------------------

    /// <summary>One <c>ai.discovery</c> bucket event: when, its event name, and the structured attributes it carries.</summary>
    public sealed record AuditSpec(DateTimeOffset At, string EventName, string StructuredJson);

    /// <summary>Creates <paramref name="path"/> from the real audit DDL and fills it with <c>ai.discovery</c> events.</summary>
    public static void WriteAudit(string path, params AuditSpec[] events)
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-schema.sql"));
        using (var connection = Open(path))
        {
            Execute(connection, schema);
            var index = 0;
            foreach (var spec in events)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, structured_json, bucket, event_name)
                    VALUES ($id, $timestamp, 'ai_discovery', '', 'sidecar', 'synthetic', 'INFO', $structured, 'ai.discovery', $name)
                    """;
                _ = insert.Parameters.AddWithValue("$id", "ai-" + (index++).ToString("D4", CultureInfo.InvariantCulture));
                _ = insert.Parameters.AddWithValue("$timestamp", spec.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
                _ = insert.Parameters.AddWithValue("$structured", spec.StructuredJson);
                _ = insert.Parameters.AddWithValue("$name", spec.EventName);
                _ = insert.ExecuteNonQuery();
            }
        }

        SqlitePools.Release(path);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }
}
