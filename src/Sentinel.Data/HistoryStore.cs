using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Sentinel.Domain;

namespace Sentinel.Data;

public readonly record struct SampleRow(string Key, DateTimeOffset Timestamp, double Avg, double Min, double Max, int Count);

public readonly record struct SeriesPoint(DateTimeOffset Timestamp, double Avg, double Min, double Max);

public enum SeriesTier { TenSeconds, Minute, Hour }

public readonly record struct RangeStats(double Avg, double Min, double Max, double? P95, long Count)
{
    public bool HasData => Count > 0;
}

public sealed record AppUsageRow(DateTimeOffset Timestamp, string App, double Cpu, double MemoryBytes, double Gpu, double DiskBytesPerSec);

public sealed record AppUsageSummary(string App, double AvgCpu, double PeakCpu, double AvgMemoryBytes, double PeakMemoryBytes, double AvgGpu, int Minutes);

public sealed record DiskHealthDay(string DiskId, DateTimeOffset Day, double? PercentUsed, double? Spare, double? Temperature, double? WrittenBytes,
    double? ReadBytes, double? PowerOnHours, double? MediaErrors, double? UnsafeShutdowns, string? Health);

/// <summary>
/// Local SQLite history. One serialised writer connection, short-lived reader connections (WAL).
/// All timestamps are stored as UTC Unix time.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly ILogger<HistoryStore> _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _metricIds = new(StringComparer.Ordinal);
    private SqliteConnection? _writer;

    public HistoryStore(string path, ILogger<HistoryStore> log)
    {
        _path = path;
        _log = log;
    }

    public string Path => _path;

    private string ConnectionString(bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = _path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
    }.ToString();

    public void Initialize()
    {
        _writer = new SqliteConnection(ConnectionString(false));
        _writer.Open();
        Exec(_writer, "PRAGMA auto_vacuum = INCREMENTAL;");
        Exec(_writer, "PRAGMA journal_mode = WAL;");
        Exec(_writer, "PRAGMA synchronous = NORMAL;");
        Exec(_writer, "PRAGMA temp_store = MEMORY;");
        Exec(_writer, "PRAGMA busy_timeout = 5000;");
        Migrations.Apply(_writer);
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "SELECT id, key FROM metric";
        using var r = cmd.ExecuteReader();
        while (r.Read()) _metricIds[r.GetString(1)] = r.GetInt64(0);
        _log.LogInformation("History database ready (schema v{Version})", Migrations.Latest);
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenReader()
    {
        var c = new SqliteConnection(ConnectionString(false));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 3000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private async Task WriteAsync(Action<SqliteConnection, SqliteTransaction> work, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var c = _writer ?? throw new InvalidOperationException("HistoryStore not initialised.");
            using var tx = c.BeginTransaction();
            work(c, tx);
            tx.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private long MetricId(SqliteConnection c, SqliteTransaction tx, string key)
    {
        if (_metricIds.TryGetValue(key, out var id)) return id;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO metric(key) VALUES ($k) ON CONFLICT(key) DO UPDATE SET key = excluded.key RETURNING id;";
        cmd.Parameters.AddWithValue("$k", key);
        id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        _metricIds[key] = id;
        return id;
    }

    private long? TryMetricId(string key) => _metricIds.TryGetValue(key, out var id) ? id : null;

    // ---------------------------------------------------------------- samples

    public Task WriteSamplesAsync(IReadOnlyList<SampleRow> tenSecond, IReadOnlyList<SampleRow> minute, CancellationToken ct = default)
    {
        if (tenSecond.Count == 0 && minute.Count == 0) return Task.CompletedTask;
        return WriteAsync((c, tx) =>
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO sample_10s(metric_id, ts, avg, min, max) VALUES ($m, $t, $a, $lo, $hi)";
                var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
                var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
                var pa = cmd.Parameters.Add("$a", SqliteType.Real);
                var plo = cmd.Parameters.Add("$lo", SqliteType.Real);
                var phi = cmd.Parameters.Add("$hi", SqliteType.Real);
                cmd.Prepare();
                foreach (var s in tenSecond)
                {
                    pm.Value = MetricId(c, tx, s.Key);
                    pt.Value = s.Timestamp.ToUnixTimeSeconds();
                    pa.Value = s.Avg;
                    plo.Value = s.Min;
                    phi.Value = s.Max;
                    cmd.ExecuteNonQuery();
                }
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO sample_1m(metric_id, ts, avg, min, max, n) VALUES ($m, $t, $a, $lo, $hi, $n)";
                var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
                var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
                var pa = cmd.Parameters.Add("$a", SqliteType.Real);
                var plo = cmd.Parameters.Add("$lo", SqliteType.Real);
                var phi = cmd.Parameters.Add("$hi", SqliteType.Real);
                var pn = cmd.Parameters.Add("$n", SqliteType.Integer);
                cmd.Prepare();
                foreach (var s in minute)
                {
                    pm.Value = MetricId(c, tx, s.Key);
                    pt.Value = s.Timestamp.ToUnixTimeSeconds();
                    pa.Value = s.Avg;
                    plo.Value = s.Min;
                    phi.Value = s.Max;
                    pn.Value = s.Count;
                    cmd.ExecuteNonQuery();
                }
            }
        }, ct);
    }

    public static SeriesTier ChooseTier(TimeSpan span) =>
        span <= TimeSpan.FromHours(6) ? SeriesTier.TenSeconds : span <= TimeSpan.FromDays(8) ? SeriesTier.Minute : SeriesTier.Hour;

    /// <summary>
    /// Returns a series for charting, using the coarsest tier that still gives enough resolution,
    /// falling back to coarser tiers when finer data has been retired by retention.
    /// </summary>
    public IReadOnlyList<SeriesPoint> QuerySeries(string key, DateTimeOffset from, DateTimeOffset to, SeriesTier? tier = null)
    {
        if (TryMetricId(key) is not { } id) return [];
        var chosen = tier ?? ChooseTier(to - from);
        using var c = OpenReader();
        for (var t = chosen; t <= SeriesTier.Hour; t++)
        {
            var rows = QueryTier(c, id, t, from, to);
            if (rows.Count > 0) return rows;
        }
        return [];
    }

    private static List<SeriesPoint> QueryTier(SqliteConnection c, long id, SeriesTier tier, DateTimeOffset from, DateTimeOffset to)
    {
        var table = tier switch { SeriesTier.TenSeconds => "sample_10s", SeriesTier.Minute => "sample_1m", _ => "sample_1h" };
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT ts, avg, min, max FROM {table} WHERE metric_id = $m AND ts BETWEEN $a AND $b ORDER BY ts";
        cmd.Parameters.AddWithValue("$m", id);
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var list = new List<SeriesPoint>();
        while (r.Read())
            list.Add(new SeriesPoint(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime(), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)));
        return list;
    }

    /// <summary>Statistics over a range from the minute tier (falls back to 10s, then hours).</summary>
    public RangeStats Stats(string key, DateTimeOffset from, DateTimeOffset to)
    {
        if (TryMetricId(key) is not { } id) return default;
        using var c = OpenReader();
        foreach (var table in new[] { "sample_1m", "sample_10s", "sample_1h" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT AVG(avg), MIN(min), MAX(max), COUNT(*) FROM {table} WHERE metric_id = $m AND ts BETWEEN $a AND $b";
            cmd.Parameters.AddWithValue("$m", id);
            cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$b", to.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            if (r.Read() && r.GetInt64(3) > 0)
                return new RangeStats(r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), null, r.GetInt64(3));
        }
        return default;
    }

    /// <summary>Minute averages for a metric (used by baseline learning and correlation).</summary>
    public IReadOnlyList<(long Minute, double Value)> MinuteValues(string key, DateTimeOffset from, DateTimeOffset to)
    {
        if (TryMetricId(key) is not { } id) return [];
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, avg FROM sample_1m WHERE metric_id = $m AND ts BETWEEN $a AND $b ORDER BY ts";
        cmd.Parameters.AddWithValue("$m", id);
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var list = new List<(long, double)>();
        while (r.Read()) list.Add((r.GetInt64(0) / 60, r.GetDouble(1)));
        return list;
    }

    /// <summary>Value of a metric nearest to a point in time (within tolerance), for Time Machine reconstruction.</summary>
    public SeriesPoint? ValueAt(string key, DateTimeOffset at, TimeSpan tolerance)
    {
        if (TryMetricId(key) is not { } id) return null;
        using var c = OpenReader();
        foreach (var table in new[] { "sample_10s", "sample_1m", "sample_1h" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT ts, avg, min, max FROM {table} WHERE metric_id = $m AND ts BETWEEN $a AND $b ORDER BY ABS(ts - $t) LIMIT 1";
            var t = at.ToUnixTimeSeconds();
            cmd.Parameters.AddWithValue("$m", id);
            cmd.Parameters.AddWithValue("$t", t);
            cmd.Parameters.AddWithValue("$a", t - (long)tolerance.TotalSeconds);
            cmd.Parameters.AddWithValue("$b", t + (long)tolerance.TotalSeconds);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return new SeriesPoint(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime(), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
        }
        return null;
    }

    public DateTimeOffset? EarliestSample()
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MIN(ts) FROM (SELECT MIN(ts) ts FROM sample_1h UNION ALL SELECT MIN(ts) FROM sample_1m UNION ALL SELECT MIN(ts) FROM sample_10s)";
        var v = cmd.ExecuteScalar();
        return v is long l ? DateTimeOffset.FromUnixTimeSeconds(l).ToLocalTime() : null;
    }

    // ---------------------------------------------------------------- maintenance

    /// <summary>Builds hour aggregates (with median and p95) from minute data for completed hours.</summary>
    public async Task AggregateHoursAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var last = GetKv("hour_aggregated_until") is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : 0L;
        var currentHour = now.ToUnixTimeSeconds() / 3600 * 3600;
        if (last == 0)
        {
            using var c0 = OpenReader();
            using var cmd0 = c0.CreateCommand();
            cmd0.CommandText = "SELECT MIN(ts) FROM sample_1m";
            last = cmd0.ExecuteScalar() is long m ? m / 3600 * 3600 : currentHour;
        }
        if (last >= currentHour) return;

        var rows = new List<(long Metric, long Hour, double Avg, double Min, double Max, double P50, double P95, int N)>();
        using (var c = OpenReader())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT metric_id, ts, avg, min, max FROM sample_1m WHERE ts >= $a AND ts < $b ORDER BY metric_id, ts";
            cmd.Parameters.AddWithValue("$a", last);
            cmd.Parameters.AddWithValue("$b", currentHour);
            using var r = cmd.ExecuteReader();
            var bucket = new List<double>();
            long curMetric = -1, curHour = -1;
            double min = double.MaxValue, max = double.MinValue;
            void Flush()
            {
                if (bucket.Count == 0) return;
                bucket.Sort();
                rows.Add((curMetric, curHour, bucket.Average(), min, max, Percentile(bucket, 0.5), Percentile(bucket, 0.95), bucket.Count));
                bucket.Clear();
                min = double.MaxValue;
                max = double.MinValue;
            }
            while (r.Read())
            {
                var metric = r.GetInt64(0);
                var hour = r.GetInt64(1) / 3600 * 3600;
                if (metric != curMetric || hour != curHour)
                {
                    Flush();
                    curMetric = metric;
                    curHour = hour;
                }
                bucket.Add(r.GetDouble(2));
                min = Math.Min(min, r.GetDouble(3));
                max = Math.Max(max, r.GetDouble(4));
            }
            Flush();
        }

        await WriteAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO sample_1h(metric_id, ts, avg, min, max, p50, p95, n) VALUES ($m,$t,$a,$lo,$hi,$p50,$p95,$n)";
            var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
            var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pa = cmd.Parameters.Add("$a", SqliteType.Real);
            var plo = cmd.Parameters.Add("$lo", SqliteType.Real);
            var phi = cmd.Parameters.Add("$hi", SqliteType.Real);
            var p50 = cmd.Parameters.Add("$p50", SqliteType.Real);
            var p95 = cmd.Parameters.Add("$p95", SqliteType.Real);
            var pn = cmd.Parameters.Add("$n", SqliteType.Integer);
            foreach (var row in rows)
            {
                pm.Value = row.Metric;
                pt.Value = row.Hour;
                pa.Value = row.Avg;
                plo.Value = row.Min;
                phi.Value = row.Max;
                p50.Value = row.P50;
                p95.Value = row.P95;
                pn.Value = row.N;
                cmd.ExecuteNonQuery();
            }
            SetKv(c, tx, "hour_aggregated_until", currentHour.ToString(CultureInfo.InvariantCulture));
        }, ct).ConfigureAwait(false);
    }

    internal static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Applies retention: 10s detail for 48h, minute data for min(retention, 21 days), hours/events per retention (0 = unlimited).</summary>
    public async Task ApplyRetentionAsync(DateTimeOffset now, int retentionDays, CancellationToken ct = default)
    {
        var detailCutoff = now.AddHours(-48).ToUnixTimeSeconds();
        var minuteDays = retentionDays == 0 ? 21 : Math.Min(retentionDays, 21);
        var minuteCutoff = now.AddDays(-minuteDays).ToUnixTimeSeconds();
        var appCutoff = now.AddDays(-Math.Min(minuteDays, 14)).ToUnixTimeSeconds();
        long? longCutoff = retentionDays == 0 ? null : now.AddDays(-retentionDays).ToUnixTimeSeconds();

        await WriteAsync((c, tx) =>
        {
            void Del(string sql, long cutoff)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$c", cutoff);
                cmd.ExecuteNonQuery();
            }
            Del("DELETE FROM sample_10s WHERE ts < $c", detailCutoff);
            Del("DELETE FROM sample_1m WHERE ts < $c", minuteCutoff);
            Del("DELETE FROM app_usage_1m WHERE ts < $c", appCutoff);
            if (longCutoff is { } lc)
            {
                Del("DELETE FROM sample_1h WHERE ts < $c", lc);
                Del("DELETE FROM event WHERE ts < $c", lc * 1000);
                Del("DELETE FROM change WHERE ts < $c", lc * 1000);
                Del("DELETE FROM anomaly WHERE last_seen < $c", lc * 1000);
                Del("DELETE FROM workload_session WHERE start < $c", lc * 1000);
                Del("DELETE FROM power_session WHERE start < $c", lc * 1000);
            }
        }, ct).ConfigureAwait(false);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exec(_writer!, "PRAGMA incremental_vacuum(2000);");
            Exec(_writer!, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public long DatabaseSizeBytes()
    {
        long size = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var fi = new FileInfo(_path + suffix);
            if (fi.Exists) size += fi.Length;
        }
        return size;
    }

    public async Task ClearAllHistoryAsync(CancellationToken ct = default)
    {
        await WriteAsync((c, tx) =>
        {
            foreach (var t in new[] { "sample_10s", "sample_1m", "sample_1h", "event", "change", "anomaly", "baseline", "workload_session", "power_session",
                         "capacity_history", "disk_health_history", "app_usage_1m", "inventory", "kv" })
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {t}";
                cmd.ExecuteNonQuery();
            }
        }, ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exec(_writer!, "VACUUM;");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>Inserts events, ignoring duplicates by dedupe key. Returns events that were new.</summary>
    public async Task<IReadOnlyList<SystemEvent>> InsertEventsAsync(IReadOnlyList<SystemEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return [];
        var inserted = new List<SystemEvent>();
        await WriteAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO event(ts, category, severity, title, detail, source, subject, code, dedupe)
                VALUES ($ts, $cat, $sev, $title, $detail, $src, $subj, $code, $dedupe) RETURNING id
                """;
            var pts = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var pcat = cmd.Parameters.Add("$cat", SqliteType.Text);
            var psev = cmd.Parameters.Add("$sev", SqliteType.Integer);
            var ptitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pdetail = cmd.Parameters.Add("$detail", SqliteType.Text);
            var psrc = cmd.Parameters.Add("$src", SqliteType.Text);
            var psubj = cmd.Parameters.Add("$subj", SqliteType.Text);
            var pcode = cmd.Parameters.Add("$code", SqliteType.Text);
            var pded = cmd.Parameters.Add("$dedupe", SqliteType.Text);
            foreach (var e in events)
            {
                pts.Value = e.Timestamp.ToUnixTimeMilliseconds();
                pcat.Value = e.Category.ToString();
                psev.Value = (int)e.Severity;
                ptitle.Value = e.Title;
                pdetail.Value = (object?)e.Detail ?? DBNull.Value;
                psrc.Value = e.Source;
                psubj.Value = (object?)e.Subject ?? DBNull.Value;
                pcode.Value = (object?)e.Code ?? DBNull.Value;
                pded.Value = (object?)e.DedupeKey ?? DBNull.Value;
                var id = cmd.ExecuteScalar();
                if (id is long l) inserted.Add(e with { Id = l });
            }
        }, ct).ConfigureAwait(false);
        return inserted;
    }

    public IReadOnlyList<SystemEvent> QueryEvents(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<EventCategory>? categories = null, int limit = 2000)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        var filter = "";
        if (categories is { Count: > 0 })
        {
            var names = categories.Select((cat, i) => { cmd.Parameters.AddWithValue("$c" + i, cat.ToString()); return "$c" + i; });
            filter = $" AND category IN ({string.Join(",", names)})";
        }
        cmd.CommandText = $"SELECT id, ts, category, severity, title, detail, source, subject, code, dedupe FROM event WHERE ts BETWEEN $a AND $b{filter} ORDER BY ts DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<SystemEvent>();
        while (r.Read())
        {
            if (!Enum.TryParse<EventCategory>(r.GetString(2), out var cat)) cat = EventCategory.Other;
            list.Add(new SystemEvent(
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).ToLocalTime(), cat, (Severity)r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(0), r.IsDBNull(8) ? null : r.GetString(8)));
        }
        return list;
    }

    public IReadOnlyDictionary<DateOnly, int> CountEventsByDay(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<EventCategory> categories)
    {
        var result = new Dictionary<DateOnly, int>();
        foreach (var e in QueryEvents(from, to, categories, 20000))
        {
            var d = DateOnly.FromDateTime(e.Timestamp.LocalDateTime);
            result[d] = result.GetValueOrDefault(d) + 1;
        }
        return result;
    }

    // ---------------------------------------------------------------- changes & inventory

    public Task InsertChangesAsync(IReadOnlyList<ChangeRecord> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return Task.CompletedTask;
        return WriteAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO change(ts, kind, title, before, after, source) VALUES ($ts, $k, $t, $b, $a, $s)";
            var pts = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var pk = cmd.Parameters.Add("$k", SqliteType.Text);
            var pt = cmd.Parameters.Add("$t", SqliteType.Text);
            var pb = cmd.Parameters.Add("$b", SqliteType.Text);
            var pa = cmd.Parameters.Add("$a", SqliteType.Text);
            var ps = cmd.Parameters.Add("$s", SqliteType.Text);
            foreach (var ch in changes)
            {
                pts.Value = ch.Timestamp.ToUnixTimeMilliseconds();
                pk.Value = ch.Kind;
                pt.Value = ch.Title;
                pb.Value = (object?)ch.Before ?? DBNull.Value;
                pa.Value = (object?)ch.After ?? DBNull.Value;
                ps.Value = ch.Source;
                cmd.ExecuteNonQuery();
            }
        }, ct);
    }

    public IReadOnlyList<ChangeRecord> QueryChanges(DateTimeOffset from, DateTimeOffset to, int limit = 2000)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, ts, kind, title, before, after, source FROM change WHERE ts BETWEEN $a AND $b ORDER BY ts DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ChangeRecord>();
        while (r.Read())
            list.Add(new ChangeRecord(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).ToLocalTime(), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetInt64(0)));
        return list;
    }

    public (string Hash, string Json, DateTimeOffset Timestamp)? GetInventory(string kind)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT hash, json, ts FROM inventory WHERE kind = $k";
        cmd.Parameters.AddWithValue("$k", kind);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).ToLocalTime()) : null;
    }

    public Task SetInventoryAsync(string kind, string hash, string json, DateTimeOffset ts, CancellationToken ct = default) =>
        WriteAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO inventory(kind, ts, hash, json) VALUES ($k, $t, $h, $j)";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$t", ts.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.ExecuteNonQuery();
        }, ct);

    // ---------------------------------------------------------------- key/value

    public string? GetKv(string key)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public Task SetKvAsync(string key, string value, CancellationToken ct = default) => WriteAsync((c, tx) => SetKv(c, tx, key, value), ct);

    private static void SetKv(SqliteConnection c, SqliteTransaction tx, string key, string value)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO kv(key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- baselines

    public Task UpsertBaselinesAsync(IReadOnlyList<Baseline> baselines, CancellationToken ct = default)
    {
        if (baselines.Count == 0) return Task.CompletedTask;
        return WriteAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO baseline(metric, context, median, mad, p05, p95, mean, n, days, computed) VALUES ($m,$c,$med,$mad,$p05,$p95,$mean,$n,$d,$t)";
            foreach (var b in baselines)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$m", b.MetricKey);
                cmd.Parameters.AddWithValue("$c", b.Context);
                cmd.Parameters.AddWithValue("$med", b.Median);
                cmd.Parameters.AddWithValue("$mad", b.Mad);
                cmd.Parameters.AddWithValue("$p05", b.P05);
                cmd.Parameters.AddWithValue("$p95", b.P95);
                cmd.Parameters.AddWithValue("$mean", b.Mean);
                cmd.Parameters.AddWithValue("$n", b.SampleCount);
                cmd.Parameters.AddWithValue("$d", b.DaysCovered);
                cmd.Parameters.AddWithValue("$t", b.ComputedAt.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
        }, ct);
    }

    public IReadOnlyList<Baseline> GetBaselines()
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT metric, context, median, mad, p05, p95, mean, n, days, computed FROM baseline";
        using var r = cmd.ExecuteReader();
        var list = new List<Baseline>();
        while (r.Read())
            list.Add(new Baseline(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6),
                r.GetInt32(7), r.GetInt32(8), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)).ToLocalTime()));
        return list;
    }

    // ---------------------------------------------------------------- anomalies

    public Task UpsertAnomalyAsync(Anomaly a, CancellationToken ct = default) => WriteAsync((c, tx) =>
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO anomaly(id, metric, title, description, baseline, observed, deviation, deviation_text, start, last_seen, severity, confidence, evidence, correlated, active)
            VALUES ($id,$m,$t,$d,$b,$o,$dev,$dt,$s,$l,$sev,$conf,$ev,$cor,$act)
            """;
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$m", a.MetricKey);
        cmd.Parameters.AddWithValue("$t", a.Title);
        cmd.Parameters.AddWithValue("$d", a.Description);
        cmd.Parameters.AddWithValue("$b", a.BaselineValue);
        cmd.Parameters.AddWithValue("$o", a.ObservedValue);
        cmd.Parameters.AddWithValue("$dev", a.Deviation);
        cmd.Parameters.AddWithValue("$dt", a.DeviationText);
        cmd.Parameters.AddWithValue("$s", a.Start.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$l", a.LastSeen.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$sev", (int)a.Severity);
        cmd.Parameters.AddWithValue("$conf", (int)a.Confidence);
        cmd.Parameters.AddWithValue("$ev", JsonSerializer.Serialize(a.Evidence.Select(e => new EvidenceDto(e.Kind, e.Statement, e.Source, e.Timestamp)).ToList(), Json));
        cmd.Parameters.AddWithValue("$cor", JsonSerializer.Serialize(a.CorrelatedEvents, Json));
        cmd.Parameters.AddWithValue("$act", a.Active ? 1 : 0);
        cmd.ExecuteNonQuery();
    }, ct);

    private sealed record EvidenceDto(EvidenceKind Kind, string Statement, string? Source, DateTimeOffset? Timestamp);

    public IReadOnlyList<Anomaly> QueryAnomalies(DateTimeOffset from, DateTimeOffset to, int limit = 500)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, metric, title, description, baseline, observed, deviation, deviation_text, start, last_seen, severity, confidence, evidence, correlated, active
            FROM anomaly WHERE last_seen >= $a AND start <= $b ORDER BY start DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Anomaly>();
        while (r.Read())
        {
            var ev = JsonSerializer.Deserialize<List<EvidenceDto>>(r.GetString(12), Json) ?? [];
            var cor = JsonSerializer.Deserialize<List<string>>(r.GetString(13), Json) ?? [];
            list.Add(new Anomaly(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6),
                r.GetString(7), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)).ToLocalTime(), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)).ToLocalTime(),
                (Severity)r.GetInt32(10), (Confidence)r.GetInt32(11), ev.Select(e => new EvidenceItem(e.Kind, e.Statement, e.Source, e.Timestamp)).ToList(),
                cor, r.GetInt32(14) == 1));
        }
        return list;
    }

    // ---------------------------------------------------------------- sessions

    public Task InsertWorkloadSessionAsync(WorkloadSession s, CancellationToken ct = default) => WriteAsync((c, tx) =>
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO workload_session(start, end, app, cpu_avg, cpu_peak, gpu_avg, gpu_peak, gpu_temp_avg, gpu_temp_peak, cpu_temp_avg, cpu_temp_peak, gpu_power_avg, mem_peak, crashes, on_battery)
            VALUES ($s,$e,$app,$ca,$cp,$ga,$gp,$gta,$gtp,$cta,$ctp,$gpa,$mp,$cr,$ob)
            """;
        cmd.Parameters.AddWithValue("$s", s.Start.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$e", s.End.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$app", (object?)s.AppName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", s.CpuAvg);
        cmd.Parameters.AddWithValue("$cp", s.CpuPeak);
        cmd.Parameters.AddWithValue("$ga", (object?)s.GpuAvg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gp", (object?)s.GpuPeak ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gta", (object?)s.GpuTempAvg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gtp", (object?)s.GpuTempPeak ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cta", (object?)s.CpuTempAvg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ctp", (object?)s.CpuTempPeak ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gpa", (object?)s.GpuPowerAvg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mp", s.MemoryPeakBytes);
        cmd.Parameters.AddWithValue("$cr", s.CrashesDuring);
        cmd.Parameters.AddWithValue("$ob", s.OnBattery ? 1 : 0);
        cmd.ExecuteNonQuery();
    }, ct);

    public IReadOnlyList<WorkloadSession> QueryWorkloadSessions(DateTimeOffset from, DateTimeOffset to, int limit = 200)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, start, end, app, cpu_avg, cpu_peak, gpu_avg, gpu_peak, gpu_temp_avg, gpu_temp_peak, cpu_temp_avg, cpu_temp_peak, gpu_power_avg, mem_peak, crashes, on_battery
            FROM workload_session WHERE start BETWEEN $a AND $b ORDER BY start DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<WorkloadSession>();
        double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        while (r.Read())
            list.Add(new WorkloadSession(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).ToLocalTime(),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).ToLocalTime(), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetDouble(4), r.GetDouble(5), D(6), D(7), D(8), D(9), D(10), D(11), D(12), r.GetInt64(13), r.GetInt32(14), r.GetInt32(15) == 1));
        return list;
    }

    public Task InsertPowerSessionAsync(PowerSession s, CancellationToken ct = default) => WriteAsync((c, tx) =>
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO power_session(kind, start, end, start_pct, end_pct, start_mwh, end_mwh, avg_w, notes) VALUES ($k,$s,$e,$sp,$ep,$sm,$em,$w,$n)";
        cmd.Parameters.AddWithValue("$k", (int)s.Kind);
        cmd.Parameters.AddWithValue("$s", s.Start.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$e", s.End.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$sp", (object?)s.StartPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ep", (object?)s.EndPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sm", (object?)s.StartMWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$em", (object?)s.EndMWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", (object?)s.AverageWatts ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)s.Notes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }, ct);

    public IReadOnlyList<PowerSession> QueryPowerSessions(DateTimeOffset from, DateTimeOffset to, PowerSessionKind? kind = null, int limit = 500)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, kind, start, end, start_pct, end_pct, start_mwh, end_mwh, avg_w, notes FROM power_session WHERE start BETWEEN $a AND $b"
            + (kind is null ? "" : " AND kind = $k") + " ORDER BY start DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$l", limit);
        if (kind is { } k) cmd.Parameters.AddWithValue("$k", (int)k);
        using var r = cmd.ExecuteReader();
        var list = new List<PowerSession>();
        double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        while (r.Read())
            list.Add(new PowerSession(r.GetInt64(0), (PowerSessionKind)r.GetInt32(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).ToLocalTime(),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)).ToLocalTime(), D(4), D(5), D(6), D(7), D(8), r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }

    // ---------------------------------------------------------------- battery capacity & disk health

    public Task UpsertCapacityAsync(IReadOnlyList<CapacityPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return Task.CompletedTask;
        return WriteAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            // Sentinel's own direct readings take precedence over imported report rows for the same day.
            cmd.CommandText = """
                INSERT INTO capacity_history(day, full_mwh, design_mwh, source) VALUES ($d, $f, $ds, $s)
                ON CONFLICT(day) DO UPDATE SET full_mwh = excluded.full_mwh, design_mwh = excluded.design_mwh, source = excluded.source
                WHERE capacity_history.source <> 'Sentinel' OR excluded.source = 'Sentinel'
                """;
            foreach (var p in points)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$d", p.Date.ToUnixTimeSeconds() / 86400);
                cmd.Parameters.AddWithValue("$f", p.FullChargeMWh);
                cmd.Parameters.AddWithValue("$ds", (object?)p.DesignMWh ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", p.Source);
                cmd.ExecuteNonQuery();
            }
        }, ct);
    }

    public IReadOnlyList<CapacityPoint> QueryCapacity()
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT day, full_mwh, design_mwh, source FROM capacity_history ORDER BY day";
        using var r = cmd.ExecuteReader();
        var list = new List<CapacityPoint>();
        while (r.Read())
            list.Add(new CapacityPoint(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0) * 86400), r.GetDouble(1), r.IsDBNull(2) ? null : r.GetDouble(2), r.GetString(3)));
        return list;
    }

    public Task UpsertDiskHealthAsync(DiskHealthDay d, CancellationToken ct = default) => WriteAsync((c, tx) =>
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO disk_health_history(disk_id, day, pct_used, spare, temp, written, read, poh, media_errors, unsafe, health)
            VALUES ($id,$d,$pu,$sp,$t,$w,$r,$poh,$me,$us,$h)
            """;
        cmd.Parameters.AddWithValue("$id", d.DiskId);
        cmd.Parameters.AddWithValue("$d", d.Day.ToUnixTimeSeconds() / 86400);
        cmd.Parameters.AddWithValue("$pu", (object?)d.PercentUsed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sp", (object?)d.Spare ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)d.Temperature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", (object?)d.WrittenBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (object?)d.ReadBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$poh", (object?)d.PowerOnHours ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$me", (object?)d.MediaErrors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$us", (object?)d.UnsafeShutdowns ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)d.Health ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }, ct);

    public IReadOnlyList<DiskHealthDay> QueryDiskHealth(string diskId)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT disk_id, day, pct_used, spare, temp, written, read, poh, media_errors, unsafe, health FROM disk_health_history WHERE disk_id = $id ORDER BY day";
        cmd.Parameters.AddWithValue("$id", diskId);
        using var r = cmd.ExecuteReader();
        var list = new List<DiskHealthDay>();
        double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        while (r.Read())
            list.Add(new DiskHealthDay(r.GetString(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1) * 86400), D(2), D(3), D(4), D(5), D(6), D(7), D(8), D(9),
                r.IsDBNull(10) ? null : r.GetString(10)));
        return list;
    }

    // ---------------------------------------------------------------- per-app usage

    public Task InsertAppUsageAsync(IReadOnlyList<AppUsageRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return Task.CompletedTask;
        return WriteAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO app_usage_1m(ts, app, cpu, mem, gpu, disk) VALUES ($t,$a,$c,$m,$g,$d)";
            foreach (var row in rows)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$t", row.Timestamp.ToUnixTimeSeconds() / 60 * 60);
                cmd.Parameters.AddWithValue("$a", row.App);
                cmd.Parameters.AddWithValue("$c", row.Cpu);
                cmd.Parameters.AddWithValue("$m", row.MemoryBytes);
                cmd.Parameters.AddWithValue("$g", row.Gpu);
                cmd.Parameters.AddWithValue("$d", row.DiskBytesPerSec);
                cmd.ExecuteNonQuery();
            }
        }, ct);
    }

    public IReadOnlyList<AppUsageSummary> TopApps(DateTimeOffset from, DateTimeOffset to, int limit = 15)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        // Minutes in which an app was not among the recorded top consumers count as zero usage.
        cmd.CommandText = """
            WITH span AS (SELECT MAX(1, COUNT(DISTINCT ts)) AS minutes FROM app_usage_1m WHERE ts BETWEEN $a AND $b)
            SELECT app, SUM(cpu) / (SELECT minutes FROM span), MAX(cpu), AVG(mem), MAX(mem), SUM(gpu) / (SELECT minutes FROM span), COUNT(*)
            FROM app_usage_1m WHERE ts BETWEEN $a AND $b GROUP BY app ORDER BY SUM(cpu) DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AppUsageSummary>();
        while (r.Read())
            list.Add(new AppUsageSummary(r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetInt32(6)));
        return list;
    }

    public IReadOnlyList<AppUsageRow> AppUsageAt(DateTimeOffset at, int limit = 10)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT ts, app, cpu, mem, gpu, disk FROM app_usage_1m
            WHERE ts = (SELECT ts FROM app_usage_1m WHERE ts BETWEEN $a AND $b ORDER BY ABS(ts - $t) LIMIT 1)
            ORDER BY cpu DESC LIMIT $l
            """;
        var t = at.ToUnixTimeSeconds();
        cmd.Parameters.AddWithValue("$t", t);
        cmd.Parameters.AddWithValue("$a", t - 300);
        cmd.Parameters.AddWithValue("$b", t + 300);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AppUsageRow>();
        while (r.Read())
            list.Add(new AppUsageRow(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime(), r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)));
        return list;
    }

    /// <summary>Per-minute series for one application (for leak heuristics).</summary>
    public IReadOnlyList<AppUsageRow> AppSeries(string app, DateTimeOffset from, DateTimeOffset to)
    {
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, app, cpu, mem, gpu, disk FROM app_usage_1m WHERE app = $app AND ts BETWEEN $a AND $b ORDER BY ts";
        cmd.Parameters.AddWithValue("$app", app);
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var list = new List<AppUsageRow>();
        while (r.Read())
            list.Add(new AppUsageRow(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime(), r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)));
        return list;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writeGate.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
