using System.Globalization;
using System.IO.Compression;
using System.Text;
using Sentinel.Core.Privacy;
using Sentinel.Core.Providers;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Sentinel.Telemetry;

namespace Sentinel.Diagnostics.Reports;

/// <summary>Builds local reports from current inventory, telemetry and history. Nothing is uploaded.</summary>
public sealed class ReportBuilder(ProviderSet p, HistoryStore store, IntelligenceService intelligence, TelemetryEngine engine, UnitFormatter units, SentinelPaths paths)
{
    private static string D(DateTimeOffset? t) => t is { } v ? v.ToString("g", CultureInfo.CurrentCulture) : "—";
    private static string S(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    public ReportDocument Build(ReportKind kind, bool privacySafe, InvestigationResult? investigation = null)
    {
        var doc = new ReportDocument
        {
            Title = kind switch
            {
                ReportKind.SystemHealth => "System Health Report",
                ReportKind.HardwareInventory => "Hardware Inventory",
                ReportKind.Battery => "Battery Report",
                ReportKind.Reliability => "Reliability Report",
                ReportKind.Crash => "Crash Report",
                ReportKind.Drivers => "Driver Report",
                ReportKind.PerformanceInvestigation => "Performance Investigation",
                _ => "Support Bundle Summary",
            },
            Kind = kind,
            PrivacySafe = privacySafe,
        };
        switch (kind)
        {
            case ReportKind.SystemHealth:
                Health(doc);
                Hardware(doc, privacySafe, brief: true);
                Reliability(doc, 30);
                break;
            case ReportKind.HardwareInventory:
                Hardware(doc, privacySafe, brief: false);
                break;
            case ReportKind.Battery:
                Battery(doc, privacySafe);
                break;
            case ReportKind.Reliability:
                Reliability(doc, 90);
                break;
            case ReportKind.Crash:
                Crashes(doc);
                break;
            case ReportKind.Drivers:
                Drivers(doc);
                break;
            case ReportKind.PerformanceInvestigation when investigation is not null:
                Investigation(doc, investigation);
                break;
            default:
                Health(doc);
                Hardware(doc, privacySafe, brief: false);
                Reliability(doc, 30);
                Drivers(doc);
                Providers(doc);
                break;
        }
        return doc;
    }

    private void Health(ReportDocument doc)
    {
        var h = intelligence.Health;
        var s = doc.Add("Health summary");
        if (h is null)
        {
            s.Paragraphs.Add("Health has not been evaluated yet.");
            return;
        }
        s.Paragraphs.Add($"{h.Headline} {h.Detail}");
        var t = new ReportTable { Title = "Categories", Columns = ["Category", "Status", "Confidence", "Why"] };
        foreach (var c in h.Categories) t.Rows.Add([c.Name, c.Status.Label(), c.Confidence.Label(), c.Summary]);
        s.Tables.Add(t);
        var f = new ReportTable { Title = "Evidence", Columns = ["Category", "Kind", "Statement"] };
        foreach (var c in h.Categories)
            foreach (var x in c.Factors) f.Rows.Add([c.Name, x.Kind.Label(), x.Text]);
        s.Tables.Add(f);
        if (intelligence.Insights.Count > 0)
        {
            var i = doc.Add("Insights");
            foreach (var ins in intelligence.Insights) i.Paragraphs.Add($"{ins.Title}: {ins.Summary} ({ins.Confidence.Label()})");
        }
    }

    private void Hardware(ReportDocument doc, bool privacySafe, bool brief)
    {
        var inv = p.System.Inventory;
        var sys = doc.Add("Computer");
        sys.Facts.AddRange(
        [
            ("Manufacturer", inv.Manufacturer), ("Model", inv.Model), ("Family", S(inv.ProductFamily)), ("Motherboard", S(inv.Baseboard)),
            ("BIOS", $"{S(inv.BiosVendor)} {S(inv.BiosVersion)} ({D(inv.BiosDate)})"), ("Firmware", inv.FirmwareType),
            ("Secure Boot", inv.SecureBoot switch { true => "On", false => "Off", _ => "Unknown" }),
            ("Windows", $"{inv.OsName} {inv.OsVersion} (build {inv.OsBuild}, {inv.OsArchitecture})"),
            ("Last boot", D(inv.BootTime)),
        ]);
        if (!privacySafe) sys.Facts.Add(("Serial number", S(inv.SerialNumber)));

        var cpu = p.Cpu.Inventory;
        var c = doc.Add("Processor");
        c.Facts.AddRange([("Model", cpu.Name), ("Cores / threads", $"{cpu.PhysicalCores} / {cpu.LogicalProcessors}"),
            ("Hybrid", cpu.IsHybrid ? $"Yes ({cpu.PerformanceCores} performance + {cpu.EfficiencyCores} efficiency cores)" : "No"),
            ("Base clock", UnitFormatter.Frequency(cpu.BaseMhz))]);

        var mem = p.Memory.Inventory;
        var m = doc.Add("Memory");
        m.Facts.Add(("Installed", units.Bytes(mem.InstalledBytes)));
        if (!brief && mem.Modules.Count > 0)
        {
            var t = new ReportTable { Title = "Modules", Columns = ["Slot", "Capacity", "Type", "Speed", "Manufacturer", "Part number"] };
            foreach (var mod in mem.Modules) t.Rows.Add([S(mod.Slot), units.Bytes(mod.CapacityBytes), S(mod.MemoryType), mod.ConfiguredSpeedMts is { } sp ? $"{sp} MT/s" : "—", S(mod.Manufacturer), S(mod.PartNumber)]);
            m.Tables.Add(t);
        }

        var g = doc.Add("Graphics");
        var gt = new ReportTable { Title = "Adapters", Columns = ["Name", "Type", "Dedicated memory", "Driver", "Driver date"] };
        foreach (var a in p.Gpu.Adapters.Where(a => a.Kind != GpuKind.Software)) gt.Rows.Add([a.Name, a.Kind.ToString(), units.Bytes(a.DedicatedVideoMemory), S(a.DriverVersion), D(a.DriverDate)]);
        g.Tables.Add(gt);

        var st = doc.Add("Storage");
        var dt = new ReportTable { Title = "Drives", Columns = ["Model", "Bus", "Media", "Capacity", "Health", "Percentage used", "Temperature"] };
        foreach (var d in p.Storage.Disks)
        {
            p.Storage.Health.TryGetValue(d.Id, out var h);
            dt.Rows.Add([d.Model, d.BusType, d.MediaType, units.Capacity(d.SizeBytes), S(h?.HealthStatus), h?.PercentageUsed.Value is { } pu ? $"{pu:F0}%" : "—",
                units.Temperature(h?.Temperature.Value)]);
        }
        st.Tables.Add(dt);

        if (!brief)
        {
            var disp = doc.Add("Displays");
            var t = new ReportTable { Title = "Displays", Columns = ["Name", "Resolution", "Refresh", "Scale", "Connection", "HDR"] };
            foreach (var d in p.Display.Displays) t.Rows.Add([d.FriendlyName, $"{d.Width}×{d.Height}", $"{d.RefreshHz:F0} Hz", $"{d.ScalePercent:F0}%", d.Connection, d.HdrEnabled == true ? "On" : d.HdrSupported == true ? "Supported" : "—"]);
            disp.Tables.Add(t);
        }
    }

    private void Battery(ReportDocument doc, bool privacySafe)
    {
        var b = p.Battery.Latest;
        var s = doc.Add("Battery");
        if (!b.Present)
        {
            s.Paragraphs.Add("No battery detected.");
            return;
        }
        foreach (var info in b.Batteries)
        {
            s.Facts.AddRange([("Name", S(info.Name)), ("Manufacturer", S(info.Manufacturer)), ("Chemistry", S(info.Chemistry)),
                ("Design capacity", info.DesignCapacityMWh is { } dc ? $"{dc / 1000.0:F1} Wh" : "—"),
                ("Full-charge capacity", info.FullChargeCapacityMWh is { } fc ? $"{fc / 1000.0:F1} Wh" : "—"),
                ("Estimated health", info.EstimatedHealthPercent is { } hp ? $"{hp:F1}% (full-charge ÷ design, as reported by the battery)" : "—"),
                ("Cycle count", info.CycleCount?.ToString(CultureInfo.CurrentCulture) ?? "Not reported")]);
            if (!privacySafe) s.Facts.Add(("Serial number", S(info.SerialNumber)));
        }
        var hist = store.QueryCapacity();
        if (hist.Count > 0)
        {
            var t = new ReportTable { Title = "Capacity history", Columns = ["Date", "Full-charge capacity", "Design capacity", "Source"] };
            foreach (var h in hist) t.Rows.Add([h.Date.ToString("d", CultureInfo.CurrentCulture), $"{h.FullChargeMWh / 1000:F2} Wh", h.DesignMWh is { } d ? $"{d / 1000:F2} Wh" : "—", h.Source]);
            s.Tables.Add(t);
        }
        var sessions = store.QueryPowerSessions(DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now);
        if (sessions.Count > 0)
        {
            var t = new ReportTable { Title = "Sessions (30 days)", Columns = ["Kind", "Start", "Duration", "Charge change", "Average power"] };
            foreach (var x in sessions)
                t.Rows.Add([x.Kind.ToString(), D(x.Start), UnitFormatter.Duration(x.Duration), x.PercentDelta is { } pd ? $"{pd:+0;-0}%" : "—", x.AverageWatts is { } w ? $"{w:F1} W" : "—"]);
            s.Tables.Add(t);
        }
    }

    private void Reliability(ReportDocument doc, int days)
    {
        var events = store.QueryEvents(DateTimeOffset.Now.AddDays(-days), DateTimeOffset.Now, null, 5000).Where(e => e.IsReliabilityRelevant).ToList();
        var s = doc.Add($"Reliability (last {days} days)");
        var counts = events.GroupBy(e => e.Category).OrderByDescending(g => g.Count());
        foreach (var g in counts) s.Facts.Add((SystemEvent.CategoryLabel(g.Key), g.Count().ToString(CultureInfo.CurrentCulture)));
        if (events.Count == 0) s.Paragraphs.Add("No reliability events were recorded.");
        var t = new ReportTable { Title = "Events", Columns = ["Time", "Type", "Event", "Details"] };
        foreach (var e in events.Take(500)) t.Rows.Add([D(e.Timestamp), SystemEvent.CategoryLabel(e.Category), e.Title, S(e.Detail)]);
        s.Tables.Add(t);
    }

    private void Crashes(ReportDocument doc)
    {
        var events = store.QueryEvents(DateTimeOffset.Now.AddDays(-90), DateTimeOffset.Now,
            [EventCategory.Bugcheck, EventCategory.UnexpectedShutdown, EventCategory.AppCrash, EventCategory.AppHang, EventCategory.DisplayDriverReset], 5000);
        var s = doc.Add("Crashes (last 90 days)");
        var bc = events.Where(e => e.Category == EventCategory.Bugcheck).ToList();
        s.Paragraphs.Add($"{bc.Count} stop error(s), {events.Count(e => e.Category == EventCategory.UnexpectedShutdown)} unexpected shutdown(s), " +
                         $"{events.Count(e => e.Category is EventCategory.AppCrash or EventCategory.AppHang)} app crash/hang event(s), " +
                         $"{events.Count(e => e.Category == EventCategory.DisplayDriverReset)} display driver reset(s).");
        if (bc.Count > 0)
        {
            var t = new ReportTable { Title = "Stop errors", Columns = ["Time", "Stop code", "Name", "Category", "Explanation"] };
            foreach (var e in bc)
            {
                var entry = e.Code is null ? null : Core.Knowledge.BugcheckCatalog.Lookup(e.Code);
                t.Rows.Add([D(e.Timestamp), S(e.Code), S(entry?.Name), S(entry?.Category), S(entry?.Explanation)]);
            }
            s.Tables.Add(t);
        }
        var apps = new ReportTable { Title = "Applications", Columns = ["Application", "Crashes", "Hangs", "Last seen"] };
        foreach (var g in events.Where(e => e.Category is EventCategory.AppCrash or EventCategory.AppHang).GroupBy(e => e.Subject ?? "Unknown").OrderByDescending(g => g.Count()))
            apps.Rows.Add([g.Key, g.Count(e => e.Category == EventCategory.AppCrash).ToString(CultureInfo.CurrentCulture),
                g.Count(e => e.Category == EventCategory.AppHang).ToString(CultureInfo.CurrentCulture), D(g.Max(e => e.Timestamp))]);
        s.Tables.Add(apps);
    }

    private void Drivers(ReportDocument doc)
    {
        var s = doc.Add("Drivers");
        s.Facts.Add(("Inventory refreshed", D(p.Drivers.LastRefreshed)));
        var t = new ReportTable { Title = "Installed drivers", Columns = ["Group", "Device", "Provider", "Version", "Date", "Signer"] };
        foreach (var d in p.Drivers.Drivers.Where(d => d.Group != "System" || d.Provider is not "Microsoft"))
            t.Rows.Add([d.Group, d.DeviceName, S(d.Provider), S(d.Version), D(d.Date), S(d.Signer)]);
        s.Tables.Add(t);
        var changes = store.QueryEvents(DateTimeOffset.Now.AddDays(-90), DateTimeOffset.Now, [EventCategory.DriverChanged], 500);
        var ct = new ReportTable { Title = "Driver changes (90 days)", Columns = ["Time", "Change", "Details"] };
        foreach (var c in changes) ct.Rows.Add([D(c.Timestamp), c.Title, S(c.Detail)]);
        s.Tables.Add(ct);
    }

    private static void Investigation(ReportDocument doc, InvestigationResult r)
    {
        var s = doc.Add($"Capture {r.Start:T}–{r.End:T}");
        foreach (var f in r.Findings) s.Paragraphs.Add($"{f.Kind.Label()}: {f.Text}");
        foreach (var m in r.Metrics) s.Facts.Add((m.Label, m.Value));
        var t = new ReportTable { Title = "Processes", Columns = ["App", "Avg CPU", "Peak CPU", "Peak memory", "Avg disk", "Avg GPU"] };
        foreach (var p in r.Processes)
            t.Rows.Add([p.Name, $"{p.AvgCpu:F1}%", $"{p.PeakCpu:F0}%", $"{p.PeakMemoryBytes / 1048576:F0} MiB", $"{p.AvgDiskBytesPerSec / 1048576:F2} MiB/s", $"{p.AvgGpu:F0}%"]);
        s.Tables.Add(t);
    }

    private void Providers(ReportDocument doc)
    {
        var s = doc.Add("Telemetry providers");
        var t = new ReportTable { Title = "Providers", Columns = ["Provider", "State", "Sources", "Unavailable capabilities"] };
        foreach (var st in engine.Providers)
            t.Rows.Add([st.Provider.Descriptor.Name, st.Status.Health.ToString(), string.Join("; ", st.Provider.Descriptor.DataSources),
                string.Join("; ", st.Provider.Capabilities.Where(c => !c.Available).Select(c => $"{c.Name}: {c.Reason}"))]);
        s.Tables.Add(t);
        s.Paragraphs.Add("Safety contract: " + string.Join(" ", SafetyContract.Clauses));
    }

    /// <summary>Writes a report file into the reports folder and returns its path.</summary>
    public string Save(ReportDocument doc, ReportFormat format)
    {
        var ext = format switch { ReportFormat.Json => "json", ReportFormat.Csv => "csv", _ => "html" };
        var name = $"Sentinel-{doc.Kind}-{doc.Generated:yyyyMMdd-HHmmss}.{ext}";
        var path = Path.Combine(paths.Reports, name);
        File.WriteAllText(path, ReportRenderer.Render(doc, format), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Creates a zip with the summary (HTML + JSON) and the diagnostic log, redacted when privacy-safe.</summary>
    public string SaveSupportBundle(bool privacySafe, string logText)
    {
        var doc = Build(ReportKind.SupportBundle, privacySafe);
        var path = Path.Combine(paths.Reports, $"Sentinel-SupportBundle-{doc.Generated:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string content)
        {
            var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("summary.html", ReportRenderer.Html(doc));
        Add("summary.json", ReportRenderer.Json(doc));
        Add("sentinel-log.txt", privacySafe ? Redactor.Redact(logText) : logText);
        Add("README.txt", "This bundle was created locally by Sentinel at the user's request. " +
                          (privacySafe ? "Serial numbers, user names, IP and MAC addresses, personal paths and GUIDs were removed." : "It may contain identifiers."));
        return path;
    }
}
