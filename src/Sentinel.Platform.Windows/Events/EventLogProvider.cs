using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Providers;

namespace Sentinel.Platform.Windows.Events;

/// <summary>
/// Reads Windows event logs with narrow XPath queries (specific providers and event IDs only), incrementally by
/// record ID. Logs that are not readable without elevation are reported as unavailable instead of failing.
/// </summary>
public sealed class EventLogProvider(ILogger<EventLogProvider> log) : WindowsProvider(log), IWindowsEventProvider
{
    private const int MaxRecent = 500;
    private const int MaxXmlLength = 64 * 1024;

    private readonly Dictionary<string, long> _lastRecord = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inaccessible = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private List<SystemEvent> _recent = [];

    public override ProviderDescriptor Descriptor { get; } = Describe("events", "Windows event logs", "Reliability", SamplingCost.Low, "Polled every 60 s",
        "Windows Event Log API (System, Application, WLAN-AutoConfig, Kernel-PnP, Diagnostics-Performance)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60));

    public IReadOnlyList<SystemEvent> Recent
    {
        get
        {
            lock (_gate) return _recent;
        }
    }

    public override Task InitializeAsync(CancellationToken ct)
    {
        foreach (var channel in EventMapping.Specs.Select(s => s.Channel).Distinct())
        {
            var ok = CanRead(channel);
            if (!ok) _inaccessible.Add(channel);
            SetCapability(channel, ok, "Windows Event Log API", ok ? null : "This log is not readable without administrator rights or is not present.");
        }
        return Task.CompletedTask;
    }

    private static bool CanRead(string channel)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, "*[System[EventRecordID=1]]"));
            reader.ReadEvent()?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogNotFoundException or EventLogException)
        {
            return false;
        }
    }

    public Task<IReadOnlyList<SystemEvent>> ReadHistoryAsync(DateTimeOffset since, int maxEvents, CancellationToken ct) =>
        Task.Run<IReadOnlyList<SystemEvent>>(() =>
        {
            var ms = Math.Max(60_000L, (long)(DateTimeOffset.Now - since).TotalMilliseconds);
            var result = new List<SystemEvent>();
            foreach (var spec in EventMapping.Specs)
            {
                ct.ThrowIfCancellationRequested();
                if (_inaccessible.Contains(spec.Channel)) continue;
                result.AddRange(Run(spec, $"TimeCreated[timediff(@SystemTime) <= {ms}]", maxEvents / 4));
            }
            Remember(result);
            return result.OrderBy(e => e.Timestamp).TakeLast(maxEvents).ToList();
        }, ct);

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var fresh = new List<SystemEvent>();
        foreach (var spec in EventMapping.Specs)
        {
            if (_inaccessible.Contains(spec.Channel)) continue;
            string filter;
            lock (_gate)
            {
                filter = _lastRecord.TryGetValue(spec.Channel, out var last)
                    ? $"EventRecordID > {last.ToString(CultureInfo.InvariantCulture)}"
                    : "TimeCreated[timediff(@SystemTime) <= 900000]";
            }
            fresh.AddRange(Run(spec, filter, 500));
        }
        foreach (var e in fresh) ctx.Events.Publish(e);
        Remember(fresh);
        return Task.CompletedTask;
    }

    private void Remember(List<SystemEvent> events)
    {
        if (events.Count == 0) return;
        lock (_gate)
        {
            _recent = events.Concat(_recent).GroupBy(e => e.DedupeKey).Select(g => g.First())
                .OrderByDescending(e => e.Timestamp).Take(MaxRecent).ToList();
        }
    }

    private List<SystemEvent> Run(EventSpec spec, string extraFilter, int max)
    {
        var ids = string.Join(" or ", spec.Ids.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture)));
        var xpath = $"*[System[Provider[@Name='{spec.Provider}'] and ({ids}) and {extraFilter}]]";
        var result = new List<SystemEvent>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(spec.Channel, PathType.LogName, xpath) { ReverseDirection = false });
            for (var n = 0; n < max * 4; n++)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                var raw = ToRaw(spec.Channel, record);
                if (raw is null) continue;
                lock (_gate)
                {
                    if (!_lastRecord.TryGetValue(spec.Channel, out var last) || raw.RecordId > last) _lastRecord[spec.Channel] = raw.RecordId;
                }
                if (EventMapping.Map(raw) is { } mapped) result.Add(mapped);
                if (result.Count >= max) break;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogNotFoundException)
        {
            _inaccessible.Add(spec.Channel);
            SetCapability(spec.Channel, false, "Windows Event Log API", "Not readable without administrator rights.");
        }
        catch (EventLogException ex)
        {
            Log.LogDebug(ex, "Event query failed for {Channel}/{Provider}", spec.Channel, spec.Provider);
        }
        return result;
    }

    private static RawEvent? ToRaw(string channel, EventRecord r)
    {
        if (r.RecordId is not { } id || r.TimeCreated is not { } time) return null;
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        try
        {
            var xml = r.ToXml();
            if (xml.Length <= MaxXmlLength)
            {
                var doc = XElement.Parse(xml);
                XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                var data = doc.Element(ns + "EventData")?.Elements(ns + "Data") ?? [];
                foreach (var d in data)
                {
                    var value = d.Value;
                    positional.Add(value);
                    if (d.Attribute("Name")?.Value is { Length: > 0 } name) named[name] = value;
                }
                // Some providers put their payload under UserData/<Element>/<Field>.
                var userData = doc.Element(ns + "UserData")?.Elements().FirstOrDefault();
                if (userData is not null)
                    foreach (var field in userData.Elements())
                        named[field.Name.LocalName] = field.Value;
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or EventLogException)
        {
            // Malformed or unavailable payload: keep the header-only event.
        }
        return new RawEvent(channel, r.ProviderName ?? "", r.Id, id, new DateTimeOffset(time), r.Level ?? 0, named, positional);
    }
}
