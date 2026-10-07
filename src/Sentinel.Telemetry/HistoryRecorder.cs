using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Metrics;
using Sentinel.Core.Providers;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.Telemetry;

/// <summary>
/// Receives every sample. Feeds the live ring buffers immediately and down-samples into 10-second and
/// 1-minute buckets that are written to SQLite in one batched transaction per flush (minimal disk writes).
/// </summary>
public sealed class HistoryRecorder : IMetricSink, IEventSink
{
    private const int MaxPendingRows = 200_000;
    private const int MaxPendingEvents = 5_000;

    private readonly LiveMetricStore _live;
    private readonly HistoryStore _store;
    private readonly ILogger<HistoryRecorder> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Bucket> _tenSecond = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bucket> _minute = new(StringComparer.Ordinal);
    private List<SampleRow> _pending10 = [];
    private List<SampleRow> _pending1m = [];
    private readonly ConcurrentQueue<SystemEvent> _events = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public HistoryRecorder(LiveMetricStore live, HistoryStore store, ILogger<HistoryRecorder> log)
    {
        _live = live;
        _store = store;
        _log = log;
    }

    /// <summary>When true, live telemetry continues but nothing is written to history.</summary>
    public bool Paused { get; set; }

    public event EventHandler<IReadOnlyList<SystemEvent>>? EventsRecorded;

    public long DroppedRows { get; private set; }

    public void Define(MetricDefinition definition) => _live.Define(definition);

    public void Record(string key, double value, DateTimeOffset timestamp)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;
        _live.Record(key, value, timestamp);
        if (Paused) return;
        var policy = _live.GetDefinition(key)?.Persist ?? PersistPolicy.LiveOnly;
        if (policy == PersistPolicy.LiveOnly) return;

        lock (_gate)
        {
            Accumulate(_tenSecond, _pending10, key, timestamp, value, 10);
            if (policy == PersistPolicy.Full) Accumulate(_minute, _pending1m, key, timestamp, value, 60);
        }
    }

    private void Accumulate(Dictionary<string, Bucket> buckets, List<SampleRow> pending, string key, DateTimeOffset ts, double value, int seconds)
    {
        var start = ts.ToUnixTimeSeconds() / seconds * seconds;
        if (buckets.TryGetValue(key, out var b))
        {
            if (b.Start == start)
            {
                b.Add(value);
                return;
            }
            if (pending.Count < MaxPendingRows) pending.Add(b.ToRow(key));
            else DroppedRows++;
        }
        var nb = new Bucket(start);
        nb.Add(value);
        buckets[key] = nb;
    }

    public void Publish(SystemEvent e)
    {
        if (_events.Count >= MaxPendingEvents) return;
        _events.Enqueue(e);
    }

    /// <summary>Closes buckets older than the current window and persists everything pending.</summary>
    public async Task FlushAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!await _flushGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            List<SampleRow> rows10, rows1m;
            lock (_gate)
            {
                CloseStale(_tenSecond, _pending10, now, 10);
                CloseStale(_minute, _pending1m, now, 60);
                rows10 = _pending10;
                rows1m = _pending1m;
                _pending10 = [];
                _pending1m = [];
            }

            if (!Paused) await _store.WriteSamplesAsync(rows10, rows1m, ct).ConfigureAwait(false);

            var events = new List<SystemEvent>();
            while (_events.TryDequeue(out var e)) events.Add(e);
            if (events.Count > 0)
            {
                var inserted = await _store.InsertEventsAsync(events, ct).ConfigureAwait(false);
                if (inserted.Count > 0) EventsRecorded?.Invoke(this, inserted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "History flush failed; samples for this interval were discarded");
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private static void CloseStale(Dictionary<string, Bucket> buckets, List<SampleRow> pending, DateTimeOffset now, int seconds)
    {
        var current = now.ToUnixTimeSeconds() / seconds * seconds;
        foreach (var (key, b) in buckets.ToArray())
        {
            if (b.Start >= current) continue;
            pending.Add(b.ToRow(key));
            buckets.Remove(key);
        }
    }

    private sealed class Bucket(long start)
    {
        public long Start { get; } = start;
        private double _sum;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private int _n;

        public void Add(double v)
        {
            _sum += v;
            if (v < _min) _min = v;
            if (v > _max) _max = v;
            _n++;
        }

        public SampleRow ToRow(string key) => new(key, DateTimeOffset.FromUnixTimeSeconds(Start), _sum / _n, _min, _max, _n);
    }
}
