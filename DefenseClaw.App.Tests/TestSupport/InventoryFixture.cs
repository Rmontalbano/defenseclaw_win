using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A synthetic <c>inventory.db</c> with the 0.8.10 layout (<c>ai_scans</c>, <c>ai_signals</c>,
/// <c>ai_confidence_snapshots</c> and the <c>ai_components_v</c> view — the same DDL
/// <c>InventoryScanReaderTests</c> copies from a live install) and made-up rows: fictional vendors and package
/// names, no real inventory data. One completed scheduled scan holds every signal, so the panel's rollup
/// shows exactly <c>componentCount</c> rows.
/// </summary>
internal static class InventoryFixture
{
    private const string Schema = """
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
        CREATE VIEW ai_components_v AS
            SELECT s.component_ecosystem AS ecosystem, s.component_name AS name,
                   MAX(s.component_framework) AS framework, MAX(s.component_version) AS version,
                   MAX(s.vendor) AS vendor, COUNT(*) AS install_count, MAX(s.last_seen) AS last_seen,
                   MAX(s.last_active_at) AS last_active_at, MAX(c.identity_score) AS identity_score,
                   MAX(c.identity_band) AS identity_band, MAX(c.presence_score) AS presence_score,
                   MAX(c.presence_band) AS presence_band, MAX(c.policy_version) AS policy_version
            FROM ai_signals s
            LEFT JOIN ai_confidence_snapshots c
                ON LOWER(c.ecosystem) = LOWER(s.component_ecosystem) AND LOWER(c.name) = LOWER(s.component_name)
                AND c.scan_id = s.scan_id
            WHERE s.component_ecosystem IS NOT NULL AND s.component_name IS NOT NULL
            GROUP BY LOWER(s.component_ecosystem), LOWER(s.component_name);
        """;

    private static readonly string[] Vendors =
    {
        "Northwind AI", "Contoso Labs", "Fabrikam Intelligence", "Tailspin Models", "Adventure Works ML",
        "Litware Robotics", "Wingtip Analytics", "Proseware Cognitive Services",
    };

    private static readonly string[] Ecosystems = { "npm", "pypi", "nuget", "cargo", "go" };

    private static readonly string[] Frameworks = { "node", "python", "dotnet", "rust", "go-modules", "langchain-style-agent-runtime" };

    private static readonly string[] Bands = { "low", "medium", "high", "very_high" };

    /// <summary>Creates <paramref name="path"/> with <paramref name="componentCount"/> distinct components and a few extra tables.</summary>
    public static void Create(string path, int componentCount)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Execute(connection, Schema);
        Execute(connection, "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT); CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT);");

        using var transaction = connection.BeginTransaction();
        Execute(connection, "INSERT INTO ai_scans VALUES ('scan-1', '2026-07-02 12:00:00 +0000 UTC', 100, 'scheduled', 'enhanced', 'ok', 1, 1, 0)");

        for (var i = 0; i < componentCount; i++)
        {
            var vendor = Vendors[i % Vendors.Length];
            var ecosystem = Ecosystems[i % Ecosystems.Length];
            var framework = Frameworks[(i * 7) % Frameworks.Length];

            // Some names are deliberately long: they are what truncates in a narrow column.
            var name = i % 9 == 0
                ? $"@northwind-internal/model-gateway-client-extended-{i:D4}"
                : $"sample-component-{i:D4}";
            var version = $"{1 + (i % 4)}.{i % 13}.{i % 7}";
            var installs = 1 + (i % 6);

            for (var copy = 0; copy < installs; copy++)
            {
                Execute(
                    connection,
                    """
                    INSERT INTO ai_signals (scan_id, fingerprint, signal_id, signature_id, name, vendor, product, category, detector,
                                            state, confidence, component_ecosystem, component_name, component_framework, component_version, last_seen)
                    VALUES ('scan-1', $fp, $sig, 'sig', $name, $vendor, 'Product', 'sdk', 'package_manifest', 'seen', 0.9,
                            $eco, $name, $framework, $version, '2026-07-02 11:59:00 +0000 UTC')
                    """,
                    ("$fp", $"{i}-{copy}"), ("$sig", $"sig-{i}-{copy}"), ("$name", name), ("$vendor", vendor),
                    ("$eco", ecosystem), ("$framework", framework), ("$version", version));
            }

            var band = Bands[i % Bands.Length];
            Execute(
                connection,
                "INSERT INTO ai_confidence_snapshots (scan_id, ecosystem, name, identity_score, identity_band, presence_score, presence_band, policy_version) VALUES ('scan-1', $eco, $name, $id, $band, $pr, $band, 1)",
                ("$eco", ecosystem), ("$name", name), ("$id", 0.4 + ((i % 6) * 0.1)), ("$pr", 0.3 + ((i % 7) * 0.1)), ("$band", band));
        }

        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] arguments)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments)
        {
            _ = command.Parameters.AddWithValue(name, value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture)! : value);
        }

        _ = command.ExecuteNonQuery();
    }
}
