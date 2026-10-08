using Microsoft.Extensions.Logging.Abstractions;
using Sentinel.Core.Metrics;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Telemetry;
using Xunit;

namespace Sentinel.Tests;

public class HistoryStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SamplesRoundTripAcrossTiers()
    {
        using var t = new TempStore();
        var tens = Enumerable.Range(0, 60).Select(i => new SampleRow("cpu.util", T0.AddSeconds(i * 10), i, i, i, 10)).ToList();
        var mins = Enumerable.Range(0, 10).Select(i => new SampleRow("cpu.util", T0.AddMinutes(i), i * 6, 0, 60, 60)).ToList();
        await t.Store.WriteSamplesAsync(tens, mins, TestContext.Current.CancellationToken);

        var fine = t.Store.QuerySeries("cpu.util", T0, T0.AddMinutes(10), SeriesTier.TenSeconds);
        Assert.Equal(60, fine.Count);
        var coarse = t.Store.QuerySeries("cpu.util", T0, T0.AddDays(2));
        Assert.Equal(10, coarse.Count);
        var stats = t.Store.Stats("cpu.util", T0, T0.AddMinutes(10));
        Assert.True(stats.HasData);
        Assert.Equal(60, stats.Max);
        Assert.Empty(t.Store.QuerySeries("does.not.exist", T0, T0.AddDays(1)));
    }

    [Fact]
    public async Task HourAggregatesIncludeMedianAndP95()
    {
        using var t = new TempStore();
        var mins = Enumerable.Range(0, 60).Select(i => new SampleRow("mem.used.pct", T0.AddMinutes(i), i, i, i, 60)).ToList();
        await t.Store.WriteSamplesAsync([], mins, TestContext.Current.CancellationToken);
        await t.Store.AggregateHoursAsync(T0.AddHours(2), TestContext.Current.CancellationToken);
        var hour = t.Store.QuerySeries("mem.used.pct", T0, T0.AddHours(1), SeriesTier.Hour);
        Assert.Single(hour);
        Assert.Equal(29.5, hour[0].Avg, 6);
        Assert.Equal(0, hour[0].Min);
        Assert.Equal(59, hour[0].Max);
    }

    [Fact]
    public async Task RetentionRemovesOldDetailButKeepsHours()
    {
        using var t = new TempStore();
        var now = DateTimeOffset.UtcNow;
        await t.Store.WriteSamplesAsync([new SampleRow("cpu.util", now.AddDays(-5), 1, 1, 1, 1)], [new SampleRow("cpu.util", now.AddDays(-40), 1, 1, 1, 1)],
            TestContext.Current.CancellationToken);
        await t.Store.ApplyRetentionAsync(now, 90, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(t.Store.QuerySeries("cpu.util", now.AddDays(-6), now, SeriesTier.TenSeconds), p => p.Timestamp < now.AddDays(-2));
        Assert.Empty(t.Store.QuerySeries("cpu.util", now.AddDays(-41), now.AddDays(-39), SeriesTier.Minute));
    }

    [Fact]
    public async Task ImplausibleSamplesCanBeRemoved()
    {
        using var t = new TempStore();
        var rows = new List<SampleRow>
        {
            new("gpu.x.power", T0, 40, 30, 55, 60),
            new("gpu.x.power", T0.AddMinutes(1), 400, 300, 590, 60),
        };
        await t.Store.WriteSamplesAsync([], rows, TestContext.Current.CancellationToken);
        var removed = await t.Store.DeleteSamplesAboveAsync("gpu.x.power", 175, TestContext.Current.CancellationToken);
        Assert.Equal(1, removed);
        var left = Assert.Single(t.Store.QuerySeries("gpu.x.power", T0.AddMinutes(-1), T0.AddMinutes(5), SeriesTier.Minute));
        Assert.Equal(40, left.Avg);
        Assert.Equal(0, await t.Store.DeleteSamplesAboveAsync("does.not.exist", 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EventsAreDeduplicatedByKey()
    {
        using var t = new TempStore();
        var e = new SystemEvent(T0, EventCategory.AppCrash, Severity.Notice, "App crashed", null, "test", "app.exe", "dedupe-1");
        var first = await t.Store.InsertEventsAsync([e, e with { Title = "dup" }], TestContext.Current.CancellationToken);
        var second = await t.Store.InsertEventsAsync([e], TestContext.Current.CancellationToken);
        Assert.Single(first);
        Assert.Empty(second);
        var stored = t.Store.QueryEvents(T0.AddMinutes(-1), T0.AddMinutes(1));
        Assert.Single(stored);
        Assert.Equal("App crashed", stored[0].Title);
        Assert.Equal(EventCategory.AppCrash, stored[0].Category);
    }

    [Fact]
    public async Task AnomalyRoundTripsEvidence()
    {
        using var t = new TempStore();
        var a = new Anomaly("x@1", "thermal.cpu", "Hot", "Hotter than usual", 50, 70, 5, "+20 °C", T0, T0.AddMinutes(12), Severity.Warning, Confidence.Moderate,
            [new EvidenceItem(EvidenceKind.Observed, "Median 70", "Live"), new EvidenceItem(EvidenceKind.Possible, "Dust", null)], ["12:00 Game launched"], true);
        await t.Store.UpsertAnomalyAsync(a, TestContext.Current.CancellationToken);
        var back = Assert.Single(t.Store.QueryAnomalies(T0.AddHours(-1), T0.AddHours(1)));
        Assert.Equal(2, back.Evidence.Count);
        Assert.Equal(EvidenceKind.Possible, back.Evidence[1].Kind);
        Assert.Equal("12:00 Game launched", Assert.Single(back.CorrelatedEvents));
        Assert.True(back.Active);
    }

    [Fact]
    public async Task CapacityHistoryPrefersSentinelReadings()
    {
        using var t = new TempStore();
        var day = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await t.Store.UpsertCapacityAsync([new CapacityPoint(day, 80000, 90000, "Sentinel")], TestContext.Current.CancellationToken);
        await t.Store.UpsertCapacityAsync([new CapacityPoint(day, 70000, 90000, "Windows battery report")], TestContext.Current.CancellationToken);
        var p = Assert.Single(t.Store.QueryCapacity());
        Assert.Equal(80000, p.FullChargeMWh);
        Assert.Equal("Sentinel", p.Source);
    }

    [Fact]
    public async Task TopAppsAveragesOverRecordedMinutes()
    {
        using var t = new TempStore();
        var rows = new List<AppUsageRow>();
        for (var i = 0; i < 10; i++)
        {
            rows.Add(new AppUsageRow(T0.AddMinutes(i), "busy.exe", 20, 1e9, 0, 0));
            if (i < 5) rows.Add(new AppUsageRow(T0.AddMinutes(i), "light.exe", 2, 1e8, 0, 0));
        }
        await t.Store.InsertAppUsageAsync(rows, TestContext.Current.CancellationToken);
        var top = t.Store.TopApps(T0, T0.AddHours(1));
        Assert.Equal("busy.exe", top[0].App);
        Assert.Equal(20, top[0].AvgCpu, 6);
        Assert.Equal(1, top[1].AvgCpu, 6);
    }
}

public class HistoryRecorderTests
{
    [Fact]
    public async Task RecorderDownsamplesAndPersists()
    {
        using var t = new TempStore();
        var live = new LiveMetricStore();
        live.Define(new MetricDefinition("cpu.util", "CPU", MetricUnit.Percent, "CPU", ""));
        live.Define(new MetricDefinition("cpu.core.0.util", "Core", MetricUnit.Percent, "CPU", "", PersistPolicy.Detail));
        var rec = new HistoryRecorder(live, t.Store, NullLogger<HistoryRecorder>.Instance);
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        start = DateTimeOffset.FromUnixTimeSeconds(start.ToUnixTimeSeconds() / 60 * 60);
        for (var i = 0; i < 180; i++)
        {
            rec.Record("cpu.util", i % 2 == 0 ? 10 : 30, start.AddSeconds(i));
            rec.Record("cpu.core.0.util", 50, start.AddSeconds(i));
            rec.Record("unknown.metric", 1, start.AddSeconds(i));
        }
        await rec.FlushAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var tens = t.Store.QuerySeries("cpu.util", start, start.AddMinutes(3), SeriesTier.TenSeconds);
        Assert.Equal(18, tens.Count);
        Assert.All(tens, p => Assert.Equal(20, p.Avg, 6));
        Assert.All(tens, p => Assert.Equal(30, p.Max, 6));
        Assert.Equal(3, t.Store.QuerySeries("cpu.util", start, start.AddMinutes(3), SeriesTier.Minute).Count);
        // Detail-only metrics never reach the minute tier; undefined metrics are live-only.
        Assert.Empty(t.Store.QuerySeries("cpu.core.0.util", start, start.AddMinutes(3), SeriesTier.Minute));
        Assert.Empty(t.Store.QuerySeries("unknown.metric", start, start.AddMinutes(3)));
        Assert.Equal(180, live.Get("unknown.metric")!.Count);
    }

    [Fact]
    public async Task PausedRecorderKeepsLiveButWritesNothing()
    {
        using var t = new TempStore();
        var live = new LiveMetricStore();
        live.Define(new MetricDefinition("cpu.util", "CPU", MetricUnit.Percent, "CPU", ""));
        var rec = new HistoryRecorder(live, t.Store, NullLogger<HistoryRecorder>.Instance) { Paused = true };
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 60; i++) rec.Record("cpu.util", 5, start.AddSeconds(i));
        await rec.FlushAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(60, live.Get("cpu.util")!.Count);
        Assert.Empty(t.Store.QuerySeries("cpu.util", start.AddMinutes(-1), DateTimeOffset.UtcNow, SeriesTier.TenSeconds));
    }
}

public class LiveMetricStoreTests
{
    [Fact]
    public void RingBufferIsBounded()
    {
        var s = new MetricSeries("x", 100);
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1000; i++) s.Add(t0.AddSeconds(i), i);
        Assert.Equal(100, s.Count);
        Assert.Equal(999, s.Last!.Value.Value);
        Assert.Equal(900, s.Since(t0).First().Value);
    }

    [Fact]
    public void GapsAreExcludedFromStats()
    {
        var s = new MetricSeries("x", 10);
        var t0 = DateTimeOffset.UtcNow;
        s.Add(t0, 10);
        s.AddGap(t0.AddSeconds(1));
        s.Add(t0.AddSeconds(2), 20);
        var stats = s.Stats(t0);
        Assert.Equal(2, stats.N);
        Assert.Equal(15, stats.Avg);
    }

    [Fact]
    public void SeriesCountIsCapped()
    {
        var live = new LiveMetricStore();
        for (var i = 0; i < LiveMetricStore.MaxSeries + 50; i++) live.Record("k" + i, 1, DateTimeOffset.UtcNow);
        Assert.Equal(LiveMetricStore.MaxSeries, live.Keys.Count());
    }
}
