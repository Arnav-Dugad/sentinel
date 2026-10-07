using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Controls;
using Sentinel.Core.Metrics;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.App.ViewModels;

public sealed class ChartModel
{
    public List<ChartSeries> Series { get; } = [];
    public List<ChartMarker> Markers { get; } = [];
    public List<ChartBand> Bands { get; } = [];
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public double? YMin { get; set; }
    public double? YMax { get; set; }

    public bool HasData => Series.Any(s => s.Points.Any(p => !double.IsNaN(p.V)));
}

public static class ChartData
{
    public const string Live = "Live";

    public static (DateTimeOffset From, DateTimeOffset To) Range(string key, DateTimeOffset now) => (now - RangeSelector.SpanOf(key), now);

    /// <summary>Points for a metric over the selected range: live ring buffer for "Live", stored history otherwise (plus the unflushed live tail).</summary>
    public static IReadOnlyList<ChartPoint> Points(string key, DateTimeOffset from, DateTimeOffset to, bool live, double scale = 1)
    {
        var store = App.Services.GetRequiredService<LiveMetricStore>();
        var series = store.Get(key);
        if (live)
            return series?.Since(from).Select(p => new ChartPoint(p.Timestamp, p.Value * scale)).ToList() ?? [];

        var history = App.Services.GetRequiredService<HistoryStore>().QuerySeries(key, from, to);
        var points = history.Select(p => new ChartPoint(p.Timestamp, p.Avg * scale)).ToList();
        var lastStored = points.Count > 0 ? points[^1].T : from;
        if (series is not null && to >= DateTimeOffset.Now.AddMinutes(-2))
        {
            // Bucket the unflushed tail to roughly the same resolution as the stored tier.
            var bucket = HistoryStore.ChooseTier(to - from) switch { SeriesTier.TenSeconds => 10, SeriesTier.Minute => 60, _ => 3600 };
            foreach (var g in series.Since(lastStored.AddSeconds(1)).Where(p => !double.IsNaN(p.Value)).GroupBy(p => p.Timestamp.ToUnixTimeSeconds() / bucket))
                points.Add(new ChartPoint(DateTimeOffset.FromUnixTimeSeconds(g.Key * bucket).ToLocalTime(), g.Average(p => p.Value) * scale));
        }
        return points;
    }

    /// <summary>Adds event markers and anomaly bands that fall in the range.</summary>
    public static void Annotate(ChartModel model, IReadOnlyCollection<EventCategory> categories, Func<string, bool>? anomalyMetric = null)
    {
        var store = App.Services.GetRequiredService<HistoryStore>();
        foreach (var e in store.QueryEvents(model.From, model.To, categories, 60))
            model.Markers.Add(new ChartMarker(e.Timestamp, $"{e.Timestamp:t} {e.Title}"));
        if (anomalyMetric is null) return;
        foreach (var a in store.QueryAnomalies(model.From, model.To).Where(a => anomalyMetric(a.MetricKey)))
            model.Bands.Add(new ChartBand(a.Start, a.LastSeen, a.Title));
    }

    public static void Apply(this SentinelChart chart, ChartModel model)
    {
        chart.Series.Clear();
        foreach (var s in model.Series) chart.Series.Add(s);
        chart.Markers.Clear();
        foreach (var m in model.Markers) chart.Markers.Add(m);
        chart.Bands.Clear();
        foreach (var b in model.Bands) chart.Bands.Add(b);
        chart.YMin = model.YMin;
        chart.YMax = model.YMax;
        chart.SetRange(model.From, model.To);
        chart.Redraw();
    }
}
