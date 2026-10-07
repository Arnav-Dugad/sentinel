using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.Analytics;

public sealed class AnomalyContext
{
    public required DateTimeOffset Now { get; init; }
    public required LiveMetricStore Live { get; init; }
    public required HistoryStore History { get; init; }
    public required BaselineEngine Baselines { get; init; }
    public required UnitFormatter Units { get; init; }
    public IReadOnlyList<AppUsage> CurrentApps { get; init; } = [];
    public bool PrivacyMode { get; init; }

    public string MetricName(string key) => Live.GetDefinition(key)?.Name ?? key;
}

public sealed record AnomalyCandidate(
    string Key,
    string MetricKey,
    string Title,
    string Description,
    double Baseline,
    double Observed,
    double Deviation,
    string DeviationText,
    Severity Severity,
    Confidence Confidence,
    IReadOnlyList<EvidenceItem> Evidence,
    DateTimeOffset? Start = null);

public interface IAnomalyDetector
{
    string Id { get; }
    /// <summary>Minimum time a candidate must persist before it becomes an anomaly.</summary>
    TimeSpan Sustain { get; }
    TimeSpan EvaluationInterval { get; }
    IEnumerable<AnomalyCandidate> Evaluate(AnomalyContext context);
}

/// <summary>
/// Runs deterministic detectors, applies persistence (sustain) and hysteresis, stores anomalies with full evidence,
/// and links them to nearby system events. No machine learning is required for any of this.
/// </summary>
public sealed class AnomalyEngine(HistoryStore store, IEnumerable<IAnomalyDetector> detectors, ILogger<AnomalyEngine> log)
{
    private static readonly TimeSpan CloseAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<IAnomalyDetector> _detectors = [.. detectors];
    private readonly Dictionary<string, DateTimeOffset> _lastRun = [];
    private readonly Dictionary<string, (AnomalyCandidate Candidate, DateTimeOffset FirstSeen)> _pending = [];
    private readonly ConcurrentDictionary<string, Anomaly> _active = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPersist = [];
    private readonly Dictionary<string, AnomalyCandidate> _lastCandidates = [];

    public event EventHandler<Anomaly>? AnomalyOpened;

    public IReadOnlyCollection<Anomaly> Active => [.. _active.Values];

    public async Task EvaluateAsync(AnomalyContext ctx, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        foreach (var d in _detectors)
        {
            if (_lastRun.TryGetValue(d.Id, out var last) && ctx.Now - last < d.EvaluationInterval)
            {
                // Detector not due: keep its previous candidates alive so they are not closed prematurely.
                foreach (var key in _lastCandidates.Keys.Where(k => k.StartsWith(d.Id + ":", StringComparison.Ordinal))) seen.Add(key);
                continue;
            }
            _lastRun[d.Id] = ctx.Now;
            foreach (var k in _lastCandidates.Keys.Where(k => k.StartsWith(d.Id + ":", StringComparison.Ordinal)).ToList()) _lastCandidates.Remove(k);

            IEnumerable<AnomalyCandidate> candidates;
            try
            {
                candidates = d.Evaluate(ctx).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Anomaly detector {Detector} failed", d.Id);
                continue;
            }

            foreach (var c in candidates)
            {
                var key = d.Id + ":" + c.Key;
                seen.Add(key);
                _lastCandidates[key] = c;
                if (_active.TryGetValue(key, out var existing))
                {
                    var updated = existing with
                    {
                        LastSeen = ctx.Now,
                        ObservedValue = c.Observed,
                        Deviation = Math.Max(existing.Deviation, c.Deviation),
                        DeviationText = c.DeviationText,
                        Severity = (Severity)Math.Max((int)existing.Severity, (int)c.Severity),
                        Description = c.Description,
                        Evidence = c.Evidence,
                    };
                    _active[key] = updated;
                    if (!_lastPersist.TryGetValue(key, out var lp) || ctx.Now - lp >= PersistEvery)
                    {
                        await store.UpsertAnomalyAsync(updated, ct).ConfigureAwait(false);
                        _lastPersist[key] = ctx.Now;
                    }
                    continue;
                }

                if (!_pending.TryGetValue(key, out var p)) _pending[key] = p = (c, c.Start ?? ctx.Now);
                if (ctx.Now - p.FirstSeen < d.Sustain) continue;

                var correlated = Correlate(p.FirstSeen, ctx.Now);
                var anomaly = new Anomaly($"{key}@{p.FirstSeen.ToUnixTimeSeconds()}", c.MetricKey, c.Title, c.Description, c.Baseline, c.Observed, c.Deviation,
                    c.DeviationText, p.FirstSeen, ctx.Now, c.Severity, c.Confidence, c.Evidence, correlated, Active: true);
                _active[key] = anomaly;
                _pending.Remove(key);
                _lastPersist[key] = ctx.Now;
                await store.UpsertAnomalyAsync(anomaly, ct).ConfigureAwait(false);
                log.LogInformation("Anomaly opened: {Title}", c.Title);
                AnomalyOpened?.Invoke(this, anomaly);
            }
        }

        foreach (var key in _pending.Keys.Where(k => !seen.Contains(k)).ToList()) _pending.Remove(key);

        foreach (var (key, a) in _active.ToArray())
        {
            if (seen.Contains(key) || ctx.Now - a.LastSeen < CloseAfter) continue;
            var closed = a with { Active = false };
            _active.TryRemove(key, out _);
            _lastPersist.Remove(key);
            await store.UpsertAnomalyAsync(closed, ct).ConfigureAwait(false);
        }
    }

    private List<string> Correlate(DateTimeOffset start, DateTimeOffset now)
    {
        try
        {
            return store.QueryEvents(start.AddMinutes(-30), now, null, 50)
                .Where(e => e.Category is not EventCategory.Anomaly)
                .OrderBy(e => e.Timestamp)
                .Take(8)
                .Select(e => $"{e.Timestamp:HH:mm} {e.Title}")
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Correlation lookup failed");
            return [];
        }
    }
}
