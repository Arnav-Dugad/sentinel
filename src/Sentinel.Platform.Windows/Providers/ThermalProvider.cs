using System.Text;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// ACPI thermal zones exposed by Windows' "Thermal Zone Information" counter set (no elevation, no driver).
/// Zones are firmware-defined and are not necessarily the CPU die; they are labelled as such.
/// A zone whose value never changes while load varies is flagged as a likely static firmware value.
/// </summary>
public sealed class ThermalProvider(ILogger<ThermalProvider> log) : WindowsProvider(log), IThermalTelemetryProvider
{
    private const string Source = "ACPI thermal zone (Windows counter)";
    private static readonly TimeSpan FrozenAfter = TimeSpan.FromMinutes(10);

    private PdhQuery? _pdh;
    private PdhCounter? _temp, _hpTemp, _passive;
    private readonly Dictionary<string, (double Value, DateTimeOffset Since)> _lastChange = [];
    private readonly HashSet<string> _defined = [];

    public override ProviderDescriptor Descriptor { get; } = Describe("thermal", "Thermal zones", "Thermals", SamplingCost.Negligible, "Firmware-defined (often 1 °C)", Source);

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    public IReadOnlyList<ThermalSensor> Sensors { get; private set; } = [];

    public override Task InitializeAsync(CancellationToken ct)
    {
        _pdh = new PdhQuery();
        _hpTemp = _pdh.Add(@"\Thermal Zone Information(*)\High Precision Temperature");
        _temp = _pdh.Add(@"\Thermal Zone Information(*)\Temperature");
        _passive = _pdh.Add(@"\Thermal Zone Information(*)\% Passive Limit");
        _pdh.Collect();
        var any = (_hpTemp ?? _temp)?.Instances().Count > 0;
        SetCapability("ACPI thermal zones", any, Source, any ? null : "This system's firmware does not expose ACPI thermal zones to Windows.");
        return Task.CompletedTask;
    }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var now = ctx.Now;
        if (_pdh is null || !_pdh.Collect()) return Task.CompletedTask;
        var hp = _hpTemp?.Instances(noCap: true) ?? [];
        var basic = _temp?.Instances(noCap: true) ?? [];
        var passive = _passive?.Instances(noCap: true) ?? [];
        var names = hp.Keys.Union(basic.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var sensors = new List<ThermalSensor>();
        foreach (var name in names)
        {
            // High Precision Temperature is in tenths of Kelvin; Temperature is whole Kelvin.
            double? c = hp.TryGetValue(name, out var t10) && t10 > 0 ? t10 / 10 - 273.15 : basic.TryGetValue(name, out var tk) && tk > 0 ? tk - 273.15 : null;
            if (c is not { } celsius || celsius is < -20 or > 150) continue;
            var id = "acpi-" + Slug(name);

            if (!_lastChange.TryGetValue(id, out var lc) || Math.Abs(lc.Value - celsius) > 0.05) _lastChange[id] = (celsius, now);
            var frozen = now - _lastChange[id].Since > FrozenAfter;
            var component = name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || name.Contains("PROC", StringComparison.OrdinalIgnoreCase)
                ? "CPU area (ACPI)" : "System (ACPI)";
            var throttling = passive.TryGetValue(name, out var pl) && pl is > 0 and < 100;
            var reading = frozen
                ? new Reading(celsius, Quality.Estimated, Source, now, Confidence.Low, "This zone's value has not changed for 10 minutes; firmware may report a fixed value.")
                : Good(celsius, Source, now);
            sensors.Add(new ThermalSensor(id, $"Thermal zone {name.Replace(@"\_TZ.", "", StringComparison.OrdinalIgnoreCase)}",
                component + (throttling ? " — passive cooling active" : ""), reading, null, null));

            var key = MetricKeys.Thermal(id);
            if (_defined.Add(key))
                ctx.Metrics.Define(new MetricDefinition(key, $"Thermal zone {name.Replace(@"\_TZ.", "", StringComparison.OrdinalIgnoreCase)}", MetricUnit.Celsius, "Thermals",
                    "Temperature of a firmware-defined ACPI thermal zone. It may represent the CPU area, chassis or another component."));
            ctx.Metrics.Record(key, celsius, now);
            if (passive.TryGetValue(name, out var limit)) ctx.Metrics.Record(MetricKeys.Thermal(id) + ".passive", limit, now);
        }
        Sensors = sensors;
        return Task.CompletedTask;
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.Length > 0 ? sb.ToString() : "zone";
    }

    public override void OnSystemResumed()
    {
        _pdh?.Rebuild();
        _lastChange.Clear();
    }

    public override void Dispose()
    {
        _pdh?.Dispose();
        base.Dispose();
    }
}
