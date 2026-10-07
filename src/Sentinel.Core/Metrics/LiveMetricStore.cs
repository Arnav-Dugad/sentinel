using System.Collections.Concurrent;
using Sentinel.Domain;

namespace Sentinel.Core.Metrics;

public readonly record struct MetricPoint(DateTimeOffset Timestamp, double Value);

/// <summary>Fixed-capacity, thread-safe ring buffer of samples for one metric. Never grows.</summary>
public sealed class MetricSeries
{
    private readonly long[] _ticks;
    private readonly double[] _values;
    private readonly Lock _gate = new();
    private int _head;
    private int _count;

    public MetricSeries(string key, int capacity)
    {
        Key = key;
        _ticks = new long[capacity];
        _values = new double[capacity];
    }

    public string Key { get; }
    public int Capacity => _values.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Add(DateTimeOffset ts, double value)
    {
        lock (_gate)
        {
            _ticks[_head] = ts.UtcTicks;
            _values[_head] = value;
            _head = (_head + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }
    }

    /// <summary>Marks a discontinuity (sleep, provider restart) so charts break the line instead of interpolating.</summary>
    public void AddGap(DateTimeOffset ts) => Add(ts, double.NaN);

    public MetricPoint? Last
    {
        get
        {
            lock (_gate)
            {
                if (_count == 0) return null;
                var i = (_head - 1 + _values.Length) % _values.Length;
                return new MetricPoint(new DateTimeOffset(_ticks[i], TimeSpan.Zero), _values[i]);
            }
        }
    }

    public MetricPoint[] Since(DateTimeOffset from)
    {
        lock (_gate)
        {
            var result = new List<MetricPoint>(Math.Min(_count, 4096));
            var start = (_head - _count + _values.Length) % _values.Length;
            var fromTicks = from.UtcTicks;
            for (var n = 0; n < _count; n++)
            {
                var i = (start + n) % _values.Length;
                if (_ticks[i] >= fromTicks) result.Add(new MetricPoint(new DateTimeOffset(_ticks[i], TimeSpan.Zero), _values[i]));
            }
            return [.. result];
        }
    }

    public (double Min, double Max, double Avg, int N) Stats(DateTimeOffset from)
    {
        lock (_gate)
        {
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            var n = 0;
            var start = (_head - _count + _values.Length) % _values.Length;
            var fromTicks = from.UtcTicks;
            for (var k = 0; k < _count; k++)
            {
                var i = (start + k) % _values.Length;
                if (_ticks[i] < fromTicks || double.IsNaN(_values[i])) continue;
                var v = _values[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                n++;
            }
            return n == 0 ? (double.NaN, double.NaN, double.NaN, 0) : (min, max, sum / n, n);
        }
    }
}

/// <summary>
/// In-memory recent history for live charts (default: one hour at 1 Hz per metric).
/// Bounded: a fixed number of series of fixed capacity.
/// </summary>
public sealed class LiveMetricStore
{
    public const int DefaultCapacity = 3600;
    public const int MaxSeries = 512;

    private readonly ConcurrentDictionary<string, MetricSeries> _series = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MetricDefinition> _definitions = new(StringComparer.Ordinal);

    public event EventHandler<MetricDefinition>? MetricDefined;

    public void Define(MetricDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_definitions.TryAdd(definition.Key, definition)) MetricDefined?.Invoke(this, definition);
        else _definitions[definition.Key] = definition;
    }

    public MetricDefinition? GetDefinition(string key) => _definitions.TryGetValue(key, out var d) ? d : null;

    public IReadOnlyCollection<MetricDefinition> Definitions => [.. _definitions.Values];

    public void Record(string key, double value, DateTimeOffset ts)
    {
        if (_series.TryGetValue(key, out var s))
        {
            s.Add(ts, value);
            return;
        }
        if (_series.Count >= MaxSeries) return; // hard bound; protects against runaway dynamic keys
        _series.GetOrAdd(key, k => new MetricSeries(k, DefaultCapacity)).Add(ts, value);
    }

    public MetricSeries? Get(string key) => _series.TryGetValue(key, out var s) ? s : null;

    public double? LatestValue(string key, TimeSpan maxAge, DateTimeOffset now)
    {
        var last = Get(key)?.Last;
        if (last is not { } p || double.IsNaN(p.Value) || now - p.Timestamp > maxAge) return null;
        return p.Value;
    }

    public void MarkGapAll(DateTimeOffset ts)
    {
        foreach (var s in _series.Values) s.AddGap(ts);
    }

    public IEnumerable<string> Keys => _series.Keys;
}
