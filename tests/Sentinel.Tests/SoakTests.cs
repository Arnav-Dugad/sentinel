using Microsoft.Extensions.Logging.Abstractions;
using Sentinel.Core.Metrics;
using Sentinel.Core.Providers;
using Sentinel.Core.Settings;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Telemetry;
using Sentinel.Telemetry.Simulation;
using Xunit;

namespace Sentinel.Tests;

/// <summary>A clock the test advances by hand, so hours of telemetry run in seconds.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class MemorySettings : ISettingsStore
{
    public SentinelSettings Current { get; } = new();

    public event EventHandler<SentinelSettings>? Changed { add { } remove { } }

    public void Update(Action<SentinelSettings> mutate) => mutate(Current);
}

public class SoakTests
{
    private static List<ITelemetryProvider> Providers(SimulationWorld w) =>
    [
        new SimulatedSystemProvider(w), new SimulatedCpuProvider(w), new SimulatedMemoryProvider(w), new SimulatedGpuProvider(w),
        new SimulatedStorageProvider(w), new SimulatedBatteryProvider(w), new SimulatedNetworkProvider(w), new SimulatedProcessProvider(w),
        new SimulatedThermalProvider(w), new SimulatedPowerProvider(w), new SimulatedEventProvider(w),
    ];

    /// <summary>
    /// Six simulated hours of 1 s sampling across every scenario-driven provider. Live buffers, series counts and
    /// managed memory must stay bounded, and the recorder must never drop rows.
    /// </summary>
    [Theory]
    [InlineData(SimulationScenario.Gaming)]
    [InlineData(SimulationScenario.MemoryPressure)]
    [InlineData(SimulationScenario.WifiDropout)]
    public async Task SixHoursOfSamplingStaysBounded(SimulationScenario scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        using var t = new TempStore();
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddHours(-7));
        var world = new SimulationWorld(scenario, clock);
        var live = new LiveMetricStore();
        MetricCatalog.RegisterCore(live);
        var rec = new HistoryRecorder(live, t.Store, NullLogger<HistoryRecorder>.Instance);
        var providers = Providers(world);
        foreach (var p in providers)
        {
            SafetyContract.Validate(p.Descriptor);
            await p.InitializeAsync(ct);
        }

        long midMemory = 0;
        const int seconds = 6 * 3600;
        for (var i = 0; i < seconds; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            var ctx = new SampleContext(clock.Now, SamplingMode.Foreground, rec, rec);
            foreach (var p in providers)
            {
                var interval = p.GetInterval(SamplingMode.Foreground);
                if (interval == Intervals.Never || i % Math.Max(1, (int)interval.TotalSeconds) != 0) continue;
                await p.SampleAsync(ctx, ct);
            }
            if (i % 60 == 59) await rec.FlushAsync(clock.Now, ct);
            if (i == seconds / 2)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                midMemory = GC.GetTotalMemory(true);
            }
        }
        await rec.FlushAsync(clock.Now, ct);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var endMemory = GC.GetTotalMemory(true);

        Assert.Equal(0, rec.DroppedRows);
        Assert.InRange(live.Keys.Count(), 1, LiveMetricStore.MaxSeries);
        Assert.All(live.Keys, k => Assert.True(live.Get(k)!.Count <= LiveMetricStore.DefaultCapacity));
        // Ring buffers are full by the half-way point, so the second half must not grow the heap materially.
        Assert.True(endMemory - midMemory < 24L * 1024 * 1024, $"Managed heap grew {(endMemory - midMemory) / 1024 / 1024} MB in the second half");
        // Persisted history is complete: one minute row per simulated minute for a headline metric.
        var minutes = t.Store.QuerySeries(MetricKeys.CpuUtil, clock.Now.AddHours(-6), clock.Now, SeriesTier.Minute);
        Assert.InRange(minutes.Count, 355, 361);
        Assert.All(minutes, p => Assert.InRange(p.Avg, 0, 100));

        foreach (var p in providers) p.Dispose();
    }

    /// <summary>The real scheduler loop runs every simulated provider without failures and shuts down cleanly.</summary>
    [Fact]
    public async Task EngineRunsAllProvidersAndStopsCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var t = new TempStore();
        var world = new SimulationWorld(SimulationScenario.Gaming, TimeProvider.System);
        var live = new LiveMetricStore();
        MetricCatalog.RegisterCore(live);
        var rec = new HistoryRecorder(live, t.Store, NullLogger<HistoryRecorder>.Instance);
        var engine = new TelemetryEngine(Providers(world), rec, live, new MemorySettings(), NullLogger<TelemetryEngine>.Instance, TimeProvider.System);
        Assert.True(engine.IsSimulation);
        await engine.StartAsync(ct);
        engine.SetInvestigationMode(true);
        await Task.Delay(TimeSpan.FromSeconds(3.5), ct);
        engine.NotifySuspending();
        Assert.Equal(SamplingMode.Paused, engine.GlobalMode);
        engine.NotifyResumed();
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        engine.SetInvestigationMode(false);
        await engine.DisposeAsync();

        Assert.All(engine.Providers, s => Assert.NotEqual(ProviderHealth.Failed, s.Status.Health));
        Assert.All(engine.Providers.Where(s => s.Provider.GetInterval(SamplingMode.Detail) != Intervals.Never &&
                                               s.Provider.GetInterval(SamplingMode.Detail) <= TimeSpan.FromSeconds(2)),
            s => Assert.True(s.Status.SampleCount >= 2, $"{s.Id} sampled {s.Status.SampleCount} times"));
        Assert.True(live.Get(MetricKeys.CpuUtil)!.Count >= 2);
    }
}
