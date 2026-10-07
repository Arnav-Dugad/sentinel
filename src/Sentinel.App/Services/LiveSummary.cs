using Microsoft.Extensions.DependencyInjection;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Sentinel.Telemetry;

namespace Sentinel.App.Services;

public sealed record SummaryRow(string Label, string Primary, string Secondary, string Glyph);

/// <summary>Compact current readings shared by the Home tiles, tray quick panel and mini monitor.</summary>
public static class LiveSummary
{
    public static (double? Temp, string Source)? CpuAreaTemperature(ProviderSet p)
    {
        if (p.Cpu.Latest.Temperature.HasValue) return (p.Cpu.Latest.Temperature.Value, p.Cpu.Latest.Temperature.Source);
        // Only firmware zones that identify themselves as the CPU area count; a generic system zone is not a CPU temperature.
        var zone = p.Thermal.Sensors.FirstOrDefault(s => s.Temperature.HasValue && s.Temperature.Quality == Quality.Good && s.Component.StartsWith("CPU", StringComparison.Ordinal));
        return zone is null ? null : (zone.Temperature.Value, zone.Name + " (ACPI, CPU area)");
    }

    public static IReadOnlyList<SummaryRow> Rows()
    {
        var p = App.Services.GetRequiredService<ProviderSet>();
        var u = App.Services.GetRequiredService<UnitFormatter>();
        var rows = new List<SummaryRow>();
        var cpu = p.Cpu.Latest;
        var t = CpuAreaTemperature(p);
        rows.Add(new("CPU", UnitFormatter.Percent(cpu.Utilization.Value), t is { } tt ? u.Temperature(tt.Temp) : "", ""));

        var gpus = p.Gpu.Latest.Where(g => p.Gpu.Adapters.FirstOrDefault(a => a.Id == g.AdapterId)?.Kind is GpuKind.Discrete or GpuKind.Integrated or GpuKind.External).ToList();
        var busiest = gpus.OrderByDescending(g => g.Utilization.Value ?? 0).FirstOrDefault();
        var gpuTemp = gpus.Where(g => g.Temperature.HasValue).Select(g => g.Temperature.Value).Max();
        if (busiest is not null)
            rows.Add(new("GPU", UnitFormatter.Percent(busiest.Utilization.Value), gpuTemp is { } gt ? u.Temperature(gt) : gpus.Any(g => g.InLowPowerState) ? "idle" : "", ""));

        var m = p.Memory.Latest;
        rows.Add(new("RAM", UnitFormatter.Percent(m.UsedPercent), m.TotalBytes > 0 ? $"{u.Bytes(m.UsedBytes)} / {u.Bytes(m.TotalBytes)}" : "", ""));

        var b = p.Battery.Latest;
        if (b.Present)
        {
            var rate = b.RateMilliwatts.Value is { } mw && Math.Abs(mw) > 100 ? $"{Math.Abs(mw) / 1000:F1} W" : "";
            var state = b.State switch { ChargeState.Charging => "Charging", ChargeState.Discharging => "On battery", ChargeState.Full => "Full", _ => "Plugged in" };
            rows.Add(new("Battery", UnitFormatter.Percent(b.Percent.Value), string.Join(" • ", new[] { state, rate }.Where(s => s.Length > 0)), ""));
        }

        var n = p.Network.Latest;
        rows.Add(new("Network", "↓ " + u.Throughput(n.TotalRxBytesPerSec), "↑ " + u.Throughput(n.TotalTxBytesPerSec), ""));
        return rows;
    }

    public static string StatusLine()
    {
        var i = App.Services.GetRequiredService<IntelligenceService>();
        return i.Health is { } h ? (h.Overall is HealthStatus.Excellent or HealthStatus.Good ? "All systems normal" : h.Headline) : "Collecting telemetry…";
    }
}
