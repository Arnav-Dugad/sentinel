using Microsoft.Data.Sqlite;

namespace Sentinel.Data;

/// <summary>Forward-only schema migrations. Each step runs in its own transaction.</summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    [
        // v1 — initial schema
        """
        CREATE TABLE metric (id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, unit TEXT, persist INTEGER NOT NULL DEFAULT 2);

        CREATE TABLE sample_10s (metric_id INTEGER NOT NULL, ts INTEGER NOT NULL, avg REAL NOT NULL, min REAL NOT NULL, max REAL NOT NULL,
            PRIMARY KEY (metric_id, ts)) WITHOUT ROWID;
        CREATE TABLE sample_1m (metric_id INTEGER NOT NULL, ts INTEGER NOT NULL, avg REAL NOT NULL, min REAL NOT NULL, max REAL NOT NULL, n INTEGER NOT NULL,
            PRIMARY KEY (metric_id, ts)) WITHOUT ROWID;
        CREATE TABLE sample_1h (metric_id INTEGER NOT NULL, ts INTEGER NOT NULL, avg REAL NOT NULL, min REAL NOT NULL, max REAL NOT NULL,
            p50 REAL NOT NULL, p95 REAL NOT NULL, n INTEGER NOT NULL, PRIMARY KEY (metric_id, ts)) WITHOUT ROWID;

        CREATE TABLE event (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, category TEXT NOT NULL, severity INTEGER NOT NULL, title TEXT NOT NULL,
            detail TEXT, source TEXT NOT NULL, subject TEXT, code TEXT, dedupe TEXT UNIQUE);
        CREATE INDEX ix_event_ts ON event (ts);
        CREATE INDEX ix_event_cat_ts ON event (category, ts);

        CREATE TABLE change (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, before TEXT, after TEXT, source TEXT NOT NULL);
        CREATE INDEX ix_change_ts ON change (ts);

        CREATE TABLE inventory (kind TEXT PRIMARY KEY, ts INTEGER NOT NULL, hash TEXT NOT NULL, json TEXT NOT NULL);

        CREATE TABLE anomaly (id TEXT PRIMARY KEY, metric TEXT NOT NULL, title TEXT NOT NULL, description TEXT NOT NULL, baseline REAL NOT NULL,
            observed REAL NOT NULL, deviation REAL NOT NULL, deviation_text TEXT NOT NULL, start INTEGER NOT NULL, last_seen INTEGER NOT NULL,
            severity INTEGER NOT NULL, confidence INTEGER NOT NULL, evidence TEXT NOT NULL, correlated TEXT NOT NULL, active INTEGER NOT NULL);
        CREATE INDEX ix_anomaly_start ON anomaly (start);

        CREATE TABLE baseline (metric TEXT NOT NULL, context TEXT NOT NULL, median REAL NOT NULL, mad REAL NOT NULL, p05 REAL NOT NULL, p95 REAL NOT NULL,
            mean REAL NOT NULL, n INTEGER NOT NULL, days INTEGER NOT NULL, computed INTEGER NOT NULL, PRIMARY KEY (metric, context));

        CREATE TABLE workload_session (id INTEGER PRIMARY KEY, start INTEGER NOT NULL, end INTEGER NOT NULL, app TEXT, cpu_avg REAL, cpu_peak REAL,
            gpu_avg REAL, gpu_peak REAL, gpu_temp_avg REAL, gpu_temp_peak REAL, cpu_temp_avg REAL, cpu_temp_peak REAL, gpu_power_avg REAL,
            mem_peak INTEGER, crashes INTEGER NOT NULL DEFAULT 0, on_battery INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_workload_start ON workload_session (start);

        CREATE TABLE power_session (id INTEGER PRIMARY KEY, kind INTEGER NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL, start_pct REAL, end_pct REAL,
            start_mwh REAL, end_mwh REAL, avg_w REAL, notes TEXT);
        CREATE INDEX ix_power_start ON power_session (start);

        CREATE TABLE capacity_history (day INTEGER PRIMARY KEY, full_mwh REAL NOT NULL, design_mwh REAL, source TEXT NOT NULL);

        CREATE TABLE disk_health_history (disk_id TEXT NOT NULL, day INTEGER NOT NULL, pct_used REAL, spare REAL, temp REAL, written REAL, read REAL,
            poh REAL, media_errors REAL, unsafe REAL, health TEXT, PRIMARY KEY (disk_id, day)) WITHOUT ROWID;

        CREATE TABLE app_usage_1m (ts INTEGER NOT NULL, app TEXT NOT NULL, cpu REAL NOT NULL, mem REAL NOT NULL, gpu REAL NOT NULL, disk REAL NOT NULL,
            PRIMARY KEY (ts, app)) WITHOUT ROWID;
        CREATE INDEX ix_app_usage_app ON app_usage_1m (app, ts);

        CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
    ];

    public static int Latest => Steps.Length;

    public static void Apply(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version;";
            var current = Convert.ToInt32(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (current > Steps.Length)
                throw new InvalidOperationException($"Database schema v{current} is newer than this build of Sentinel supports (v{Steps.Length}).");

            for (var v = current; v < Steps.Length; v++)
            {
                using var tx = connection.BeginTransaction();
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = Steps[v] + $"\nPRAGMA user_version = {v + 1};";
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }
    }
}
