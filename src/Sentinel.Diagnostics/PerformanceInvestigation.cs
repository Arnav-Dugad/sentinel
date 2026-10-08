using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Sentinel.Telemetry;

namespace Sentinel.Diagnostics;

public sealed record InvestigationProgress(double Fraction, string Status);

public sealed record ProcessContribution(string Name, double AvgCpu, double PeakCpu, double PeakMemoryBytes, double AvgDiskBytesPerSec, double AvgGpu);

public sealed record InvestigationResult(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<Fact> Metrics,
    IReadOnlyList<ProcessContribution> Processes,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<SystemEvent> Events);

/// <summary>
/// User-initiated 30-second investigation. Raises every provider to its detail sampling rate for the duration,
/// records per-process activity each second, then summarises. It stops automatically and keeps nothing on disk
/// beyond the normal history. (Kernel ETW tracing is not used: it requires administrator rights.)
/// </summary>
public sealed class PerformanceInvestigation(TelemetryEngine engine, ProviderSet providers, LiveMetricStore live, Data.HistoryStore store, UnitFormatter units)
{
    public async Task<InvestigationResult> RunAsync(TimeSpan duration, IProgress<InvestigationProgress>? progress, CancellationToken ct)
    {
        var start = DateTimeOffset.Now;
        var perApp = new Dictionary<string, List<AppUsage>>();
        engine.SetInvestigationMode(true);
        try
        {
            var end = start + duration;
            while (DateTimeOffset.Now < end)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var a in providers.Processes.Latest.Apps.Take(40))
                {
                    if (!perApp.TryGetValue(a.DisplayName, out var list)) perApp[a.DisplayName] = list = [];
                    list.Add(a);
                }
                var elapsed = DateTimeOffset.Now - start;
                progress?.Report(new InvestigationProgress(Math.Clamp(elapsed / duration, 0, 1), $"Capturing… {(int)elapsed.TotalSeconds} of {(int)duration.TotalSeconds} s"));
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            engine.SetInvestigationMode(false);
        }
        var stop = DateTimeOffset.Now;
        progress?.Report(new InvestigationProgress(1, "Analyzing…"));

        var metrics = new List<Fact>();
        var findings = new List<Finding>();
        (double Avg, double Max, int N)? S(string key)
        {
            var s = live.Get(key)?.Stats(start);
            return s is { N: > 0 } v ? (v.Avg, v.Max, v.N) : null;
        }
        void M(string key, string label, Func<double, string> f)
        {
            if (S(key) is { } s) metrics.Add(new Fact(label, $"avg {f(s.Avg)} · peak {f(s.Max)}", "Live telemetry"));
        }
        M(MetricKeys.CpuUtil, "CPU utilization", v => $"{v:F0}%");
        M(MetricKeys.CpuFreq, "CPU effective clock", v => UnitFormatter.Frequency(v));
        M(MetricKeys.CpuQueue, "Processor queue", v => $"{v:F1}");
        M(MetricKeys.CpuDpc, "DPC time", v => $"{v:F2}%");
        M(MetricKeys.CpuInterrupts, "Interrupts", v => $"{v:N0}/s");
        M("cpu.perflimit", "Performance limit", v => $"{v:F0}%");
        M(MetricKeys.GpuUtilAny, "GPU utilization", v => $"{v:F0}%");
        M(MetricKeys.MemUsedPct, "Memory in use", v => $"{v:F0}%");
        M(MetricKeys.MemHardFaults, "Hard faults", v => $"{v:N0}/s");
        M(MetricKeys.DiskActive, "Disk active time", v => $"{v:F0}%");
        M(MetricKeys.DiskRead, "Disk read", v => units.DiskRate(v));
        M(MetricKeys.DiskWrite, "Disk write", v => units.DiskRate(v));
        M(MetricKeys.NetRx, "Network download", v => units.Throughput(v));

        var processes = perApp.Select(kv => new ProcessContribution(kv.Key, kv.Value.Average(a => a.CpuPercent), kv.Value.Max(a => a.CpuPercent),
                kv.Value.Max(a => (double)a.PrivateBytes), kv.Value.Average(a => a.DiskBytesPerSec), kv.Value.Average(a => a.GpuPercent)))
            .OrderByDescending(p => p.AvgCpu).Take(12).ToList();

        if (S(MetricKeys.CpuUtil) is { Avg: > 85 } cpu)
            findings.Add(new(EvidenceKind.Inferred, $"The CPU was saturated (average {cpu.Avg:F0}%)." + (processes.FirstOrDefault() is { } p ? $" {p.Name} contributed the most ({p.AvgCpu:F0}% average)." : ""), null, Severity.Notice));
        if (S(MetricKeys.CpuQueue) is { } q && q.Avg > providers.Cpu.Inventory.LogicalProcessors)
            findings.Add(new(EvidenceKind.Observed, $"More threads were waiting for a processor than there are logical processors (queue {q.Avg:F1}).", "Performance counters", Severity.Notice));
        if (S(MetricKeys.CpuDpc) is { Max: > 5 } dpc)
            findings.Add(new(EvidenceKind.Observed, $"DPC time peaked at {dpc.Max:F1}%: a driver spent unusually long handling interrupts, which can cause audio crackle or stutter.", "Performance counters", Severity.Notice));
        if (S(MetricKeys.MemHardFaults) is { Avg: > 500 } hf)
            findings.Add(new(EvidenceKind.Inferred, $"Heavy paging ({hf.Avg:N0} hard faults/s) indicates memory pressure.", null, Severity.Notice));
        if (S(MetricKeys.DiskActive) is { Avg: > 80 } da)
            findings.Add(new(EvidenceKind.Inferred, $"A disk was busy {da.Avg:F0}% of the time; storage was a bottleneck.", null, Severity.Notice));
        if (S("cpu.perflimit") is { Avg: > 10 } pl)
            findings.Add(new(EvidenceKind.Observed, $"Processor performance was limited {pl.Avg:F0}% of the time (thermal or power limits).", "Performance counters", Severity.Notice));
        if (findings.Count == 0)
            findings.Add(new(EvidenceKind.Inferred, "No resource bottleneck was detected during the capture."));
        findings.Add(new(EvidenceKind.Unknown, "Per-thread stacks and per-process network use require kernel tracing with administrator rights and were not captured."));

        var events = store.QueryEvents(start.AddMinutes(-1), stop, null, 50).Concat(providers.Events.Recent.Where(e => e.Timestamp >= start)).DistinctBy(e => e.DedupeKey).ToList();
        return new InvestigationResult(start, stop, metrics, processes, findings, events);
    }
}
