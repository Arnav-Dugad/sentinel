using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Controls;
using Sentinel.Core.Logging;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Diagnostics;
using Sentinel.Diagnostics.Reports;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Sentinel.Platform.Windows.Providers;
using Sentinel.Platform.Windows.Services;

namespace Sentinel.App.ViewModels;

/// <summary>Shared shape for a guided diagnostic result shown on a page.</summary>
public sealed partial class DiagnosisState : ObservableObject
{
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<EvidenceRow> Findings { get; set; } = [];

    public void Show(DiagnosticResult r)
    {
        Title = $"{r.Title} · {r.Range.Label} · {r.Confidence.Label()}";
        Summary = r.Summary;
        Findings = r.Findings.Select(f => new EvidenceRow(f.Kind, f.Text, f.Source)).ToList();
        HasResult = true;
    }
}

// ---------------------------------------------------------------- Performance

public sealed record ContributionRow(string Name, string Cpu, string Memory, string Disk, string Gpu);

public sealed partial class PerformanceViewModel : PageViewModel
{
    private InvestigationResult? _result;

    protected override int RefreshEveryTicks => 3600;

    public DiagnosisState Diagnosis { get; } = new();
    public ObservableCollection<ContributionRow> Contributions { get; } = [];
    public IReadOnlyList<string> Periods { get; } = ["the last hour", "today", "yesterday", "the last 7 days"];

    [ObservableProperty] public partial bool Running { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Metrics { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<EvidenceRow> Findings { get; set; } = [];
    [ObservableProperty] public partial int PeriodIndex { get; set; }

    protected override void Refresh()
    {
    }

    [RelayCommand]
    private async Task InvestigateAsync()
    {
        if (Running) return;
        Running = true;
        HasResult = false;
        try
        {
            var progress = new Progress<InvestigationProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Status = p.Status;
            });
            _result = await Services.GetRequiredService<PerformanceInvestigation>().RunAsync(TimeSpan.FromSeconds(30), progress, CancellationToken.None);
            Metrics = _result.Metrics.Select(m => new InfoItem(m.Label, m.Value, m.Source)).ToList();
            Findings = _result.Findings.Select(f => new EvidenceRow(f.Kind, f.Text, f.Source)).ToList();
            Contributions.Clear();
            foreach (var p in _result.Processes)
                Contributions.Add(new ContributionRow(p.Name, $"{p.AvgCpu:F1}% avg · {p.PeakCpu:F0}% peak", U.Bytes(p.PeakMemoryBytes), U.DiskRate(p.AvgDiskBytesPerSec), $"{p.AvgGpu:F0}%"));
            Status = $"Captured {_result.Start:T}–{_result.End:T}. High-resolution sampling has stopped.";
            HasResult = true;
        }
        finally
        {
            Running = false;
        }
    }

    [RelayCommand]
    private void SaveReport()
    {
        if (_result is null) return;
        var builder = Services.GetRequiredService<ReportBuilder>();
        var path = builder.Save(builder.Build(ReportKind.PerformanceInvestigation, true, _result), ReportFormat.Html);
        Status = "Saved " + path;
        SettingsViewModel.Reveal(path);
    }

    [RelayCommand]
    private void WhySlow()
    {
        var q = QueryParser.Parse($"why was my pc slow {Periods[Math.Clamp(PeriodIndex, 0, Periods.Count - 1)]}".Replace("the last hour", "last 1 hour", StringComparison.Ordinal), DateTimeOffset.Now);
        Diagnosis.Show(Services.GetRequiredService<DiagnosticsEngine>().Run(q with { Intent = QueryIntent.Slowdown }));
    }
}

// ---------------------------------------------------------------- Power

public sealed partial class PowerViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 2;

    public DiagnosisState Diagnosis { get; } = new();
    public ObservableCollection<EventRow> Events { get; } = [];
    private int _slow;

    [ObservableProperty] public partial IReadOnlyList<InfoItem> State { get; set; } = [];

    protected override void Refresh()
    {
        var power = P.Power;
        var b = P.Battery.Latest;
        var mode = power is PowerProvider pp ? pp.PowerMode : null;
        var plan = power is PowerProvider pp2 ? pp2.PowerPlan : null;
        State =
        [
            new("Power source", power.AcOnline ? "Plugged in" : "Battery", "Windows power notifications"),
            new("Battery Saver", power.BatterySaverOn ? "On" : "Off", "Windows power notifications"),
            new("Display", power.DisplayOn ? "On" : "Off", "Windows power notifications"),
            new("Power mode", mode, "PowerGetEffectiveOverlaySchemeId", Tooltip: "Settings › System › Power & battery › Power mode."),
            new("Power plan", plan, "PowerGetActiveScheme"),
            new("Time since last input", UnitFormatter.Duration(power.UserIdle), "GetLastInputInfo"),
            new("Battery", b.Present ? $"{b.Percent.Value:F0}% · {(b.RateMilliwatts.Value is { } r ? $"{r / 1000:+0.0;-0.0} W" : "rate not reported")}" : "No battery", "Battery firmware"),
        ];
        if (_slow++ % 15 == 0)
        {
            var now = DateTimeOffset.Now;
            var events = Services.GetRequiredService<HistoryStore>().QueryEvents(now.AddDays(-3), now,
                [EventCategory.PowerSource, EventCategory.Sleep, EventCategory.Wake, EventCategory.Boot, EventCategory.Shutdown, EventCategory.UnexpectedShutdown], 120).Select(e => new EventRow(e)).ToList();
            if (!Events.SequenceEqual(events))
            {
                Events.Clear();
                foreach (var e in events) Events.Add(e);
            }
        }
    }

    [RelayCommand]
    private void WhyDrain() =>
        Diagnosis.Show(Services.GetRequiredService<DiagnosticsEngine>().Run(QueryParser.Parse("why is my battery draining today", DateTimeOffset.Now)));
}

// ---------------------------------------------------------------- Sleep

public sealed partial class SleepViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 30;

    public DiagnosisState Diagnosis { get; } = new();
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];

    [ObservableProperty] public partial string Summary { get; set; } = "";

    protected override void Refresh()
    {
        var now = DateTimeOffset.Now;
        var store = Services.GetRequiredService<HistoryStore>();
        var sleeps = store.QueryPowerSessions(now.AddDays(-30), now, PowerSessionKind.Sleep, 100);
        Sessions.Clear();
        foreach (var s in sleeps)
        {
            var rate = s.PercentDelta is { } d && s.Duration.TotalHours > 0.2 ? $"{-d / s.Duration.TotalHours:F1}% per hour" : "";
            Sessions.Add(new SessionRow("Asleep", "", UnitFormatter.When(s.Start), UnitFormatter.Duration(s.Duration),
                s.StartPercent is { } a && s.EndPercent is { } e ? $"{a:F0}% → {e:F0}%" : "—", rate, s.Notes ?? ""));
        }
        var events = store.QueryEvents(now.AddDays(-7), now, [EventCategory.Sleep, EventCategory.Wake], 200).Select(e => new EventRow(e)).ToList();
        Events.Clear();
        foreach (var e in events) Events.Add(e);
        var drains = sleeps.Where(s => s.PercentDelta is < 0 && s.Duration.TotalHours >= 1).Select(s => -s.PercentDelta!.Value / s.Duration.TotalHours).ToList();
        Summary = drains.Count == 0
            ? "Sleep drain appears here after Sentinel observes the PC going to sleep and waking up on battery."
            : $"Across {drains.Count} sleep session(s) on battery, the median drain was {Analytics.Statistics.Median(drains):F1}% per hour.";
    }

    [RelayCommand]
    private void Analyse() =>
        Diagnosis.Show(Services.GetRequiredService<DiagnosticsEngine>().Run(QueryParser.Parse("what happened during sleep this week", DateTimeOffset.Now) with { Intent = QueryIntent.Sleep }));
}

// ---------------------------------------------------------------- Network test

public sealed record LatencyRow(string Label, string Target, string Summary, string Loss);

public sealed partial class NetworkTestViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 3600;

    public ObservableCollection<LatencyRow> Results { get; } = [];

    [ObservableProperty] public partial bool Running { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Nothing runs until you press Start.";

    protected override void Refresh()
    {
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Running) return;
        Running = true;
        Status = "Testing… (about 10 seconds)";
        Results.Clear();
        try
        {
            var r = await Services.GetRequiredService<NetworkTestService>().RunAsync(10, CancellationToken.None);
            foreach (var l in r.Latency)
                Results.Add(new LatencyRow(l.Label, Core.Privacy.Redactor.Mask(l.Target),
                    l.Received == 0 ? "No replies" : $"min {l.MinMs:F0} ms · avg {l.AvgMs:F0} ms · max {l.MaxMs:F0} ms" + (l.JitterMs is { } j ? $" · jitter {j:F1} ms" : ""),
                    $"{l.LossPercent:F0}% loss"));
            Results.Add(new LatencyRow("DNS lookup", NetworkTestService.InternetHost, r.DnsMs is { } d ? $"{d:F0} ms" : "Failed: " + r.DnsError, ""));
            Status = $"Completed at {r.Timestamp:T}. Results are not stored.";
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
        {
            Status = "The test could not run: " + ex.Message;
        }
        finally
        {
            Running = false;
        }
    }
}

// ---------------------------------------------------------------- Reports

public sealed partial class ReportsViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 3600;

    public IReadOnlyList<string> Types { get; } = ["System health report", "Hardware inventory", "Battery report", "Reliability report", "Crash report", "Driver report"];
    public IReadOnlyList<string> Formats { get; } = ["HTML", "JSON", "CSV"];
    public ObservableCollection<string> Recent { get; } = [];

    [ObservableProperty] public partial int TypeIndex { get; set; }
    [ObservableProperty] public partial int FormatIndex { get; set; }
    [ObservableProperty] public partial bool PrivacySafe { get; set; } = true;
    [ObservableProperty] public partial string Status { get; set; } = "";

    protected override void OnActivated(object? parameter) => LoadRecent();

    protected override void Refresh()
    {
    }

    private void LoadRecent()
    {
        Recent.Clear();
        var dir = Services.GetRequiredService<SentinelPaths>().Reports;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles().OrderByDescending(f => f.LastWriteTime).Take(12))
            Recent.Add($"{f.Name}  ·  {UnitFormatter.When(f.LastWriteTime)}");
    }

    [RelayCommand]
    private void Generate()
    {
        var kind = TypeIndex switch
        {
            1 => ReportKind.HardwareInventory,
            2 => ReportKind.Battery,
            3 => ReportKind.Reliability,
            4 => ReportKind.Crash,
            5 => ReportKind.Drivers,
            _ => ReportKind.SystemHealth,
        };
        var format = (ReportFormat)Math.Clamp(FormatIndex, 0, 2);
        var builder = Services.GetRequiredService<ReportBuilder>();
        var path = builder.Save(builder.Build(kind, PrivacySafe), format);
        Status = "Saved to " + path;
        LoadRecent();
        SettingsViewModel.Reveal(path);
    }

    [RelayCommand]
    private void SupportBundle()
    {
        var log = Services.GetRequiredService<RotatingFileLoggerProvider>().ExportRedacted();
        var path = Services.GetRequiredService<ReportBuilder>().SaveSupportBundle(PrivacySafe, log);
        Status = "Saved support bundle to " + path;
        LoadRecent();
        SettingsViewModel.Reveal(path);
    }

    [RelayCommand]
    private void OpenFolder() => SettingsViewModel.Reveal(Services.GetRequiredService<SentinelPaths>().Reports);
}
