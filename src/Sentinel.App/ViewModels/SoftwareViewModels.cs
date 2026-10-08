using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Intelligence;

namespace Sentinel.App.ViewModels;

// ---------------------------------------------------------------- Processes

/// <summary>One app or process, computed each refresh.</summary>
public sealed record ProcessData(string Key, int Pid, string Name, string Detail, string Cpu, string Memory, string Disk, string Gpu,
    double CpuValue, double MemValue, double DiskValue, double GpuValue);

/// <summary>
/// A positional row ("slot") in the process table. Row <c>i</c> always shows the <c>i</c>-th entry in the current sort
/// order; refreshes update slots in place and only grow or shrink the list at its end. Items are never moved or
/// replaced, so the ListView's virtualisation, scroll position and focus stay intact, and selection simply follows
/// its app to whichever slot now shows it.
/// </summary>
public sealed partial class ProcessRow : ObservableObject
{
    [ObservableProperty] public partial string Key { get; private set; } = "";
    [ObservableProperty] public partial int Pid { get; private set; }
    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial string Cpu { get; private set; } = "";
    [ObservableProperty] public partial string Memory { get; private set; } = "";
    [ObservableProperty] public partial string Disk { get; private set; } = "";
    [ObservableProperty] public partial string Gpu { get; private set; } = "";

    public void Show(ProcessData d)
    {
        // Property setters only raise change notifications when a value actually differs.
        Key = d.Key;
        Pid = d.Pid;
        Name = d.Name;
        Detail = d.Detail;
        Cpu = d.Cpu;
        Memory = d.Memory;
        Disk = d.Disk;
        Gpu = d.Gpu;
    }
}

public sealed partial class ProcessesViewModel : PageViewModel
{
    private readonly ISettingsStore _settings = App.Services.GetRequiredService<ISettingsStore>();
    private int _slow;

    public ObservableCollection<ProcessRow> Rows { get; } = [];
    public ObservableCollection<AppRow> History { get; } = [];

    [ObservableProperty] public partial bool GroupByApp { get; set; } = true;
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial string SortBy { get; set; } = "CPU";
    [ObservableProperty] public partial ProcessRow? Selected { get; set; }
    [ObservableProperty] public partial string Impact { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Details { get; set; } = [];
    [ObservableProperty] public partial string Totals { get; set; } = "";
    [ObservableProperty] public partial bool Privacy { get; set; }

    partial void OnGroupByAppChanged(bool value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();
    partial void OnSortByChanged(string value) => Refresh();
    private bool _reconciling;
    private string? _describedKey;

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(ProcessRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        // Reordering makes the ListView briefly drop its selection; that transient null must not clear the details.
        if (_reconciling) return;
        Describe(value);
    }

    protected override int RefreshEveryTicks => 2;

    protected override void Refresh()
    {
        var snap = P.Processes.Latest;
        Privacy = _settings.Current.PrivacyMode;
        IEnumerable<ProcessData> rows = GroupByApp
            ? snap.Apps.Select(a => new ProcessData("app:" + a.AppKey, 0, a.DisplayName, $"{a.ProcessCount} process{(a.ProcessCount == 1 ? "" : "es")}" + (a.Publisher is null ? "" : " · " + a.Publisher),
                UnitFormatter.Percent(a.CpuPercent, 1), U.Bytes(a.PrivateBytes), U.DiskRate(a.DiskBytesPerSec), UnitFormatter.Percent(a.GpuPercent), a.CpuPercent, a.PrivateBytes, a.DiskBytesPerSec, a.GpuPercent))
            : snap.Processes.Select(p => new ProcessData($"pid:{p.Pid}:{p.Name}", p.Pid, p.Name, $"PID {p.Pid} · {p.Threads} threads · {p.Handles:N0} handles",
                UnitFormatter.Percent(p.CpuPercent, 1), U.Bytes(p.PrivateBytes), U.DiskRate(p.DiskBytesPerSec), UnitFormatter.Percent(p.GpuPercent), p.CpuPercent, p.PrivateBytes, p.DiskBytesPerSec, p.GpuPercent));
        if (!string.IsNullOrWhiteSpace(Search))
            rows = rows.Where(r => r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || r.Detail.Contains(Search, StringComparison.OrdinalIgnoreCase));
        rows = SortBy switch
        {
            "Memory" => rows.OrderByDescending(r => r.MemValue),
            "Disk" => rows.OrderByDescending(r => r.DiskValue),
            "GPU" => rows.OrderByDescending(r => r.GpuValue),
            "Name" => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderByDescending(r => r.CpuValue).ThenByDescending(r => r.MemValue).ThenBy(r => r.Key, StringComparer.Ordinal),
        };
        var list = rows.Take(150).ToList();
        var selectedKey = _describedKey;
        // A selected entry that drops out of the top 150 stays listed (at the end) rather than vanishing under the cursor.
        if (selectedKey is not null && !list.Exists(d => d.Key == selectedKey) && rows.FirstOrDefault(d => d.Key == selectedKey) is { } keep)
            list.Add(keep);

        _reconciling = true;
        try
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (i == Rows.Count) Rows.Add(new ProcessRow());
                Rows[i].Show(list[i]);
            }
            while (Rows.Count > list.Count) Rows.RemoveAt(Rows.Count - 1);
            // Selection follows its app to whichever slot shows it now.
            var target = selectedKey is null ? null : Rows.FirstOrDefault(r => r.Key == selectedKey);
            if (!ReferenceEquals(Selected, target)) Selected = target;
        }
        finally
        {
            _reconciling = false;
        }
        if (selectedKey is not null && Selected is null) Describe(null);
        Totals = $"{snap.Processes.Count} processes · {snap.Apps.Count} apps · CPU {UnitFormatter.Percent(snap.TotalCpuPercent)} total";

        if (_slow++ % 30 == 0 && !Privacy)
        {
            var now = DateTimeOffset.Now;
            History.Clear();
            foreach (var a in Services.GetRequiredService<HistoryStore>().TopApps(now.AddDays(-1), now, 8))
                History.Add(new AppRow(AppNames.Display(a.App, P), "", $"{a.AvgCpu:F1}% avg", U.Bytes(a.PeakMemoryBytes), "", $"Peak CPU {a.PeakCpu:F0}% · seen in {a.Minutes} min", a.AvgCpu));
        }
    }

    private void Describe(ProcessRow? row)
    {
        if (row is null)
        {
            _describedKey = null;
            Impact = "";
            Details = [];
            return;
        }
        if (row.Key == _describedKey) return;
        _describedKey = row.Key;
        var snap = P.Processes.Latest;
        if (GroupByApp && snap.Apps.FirstOrDefault(a => "app:" + a.AppKey == row.Key) is { } app)
        {
            Impact = $"{app.DisplayName} is currently responsible for approximately {app.CpuShareOfActive * 100:F0}% of active CPU consumption and {app.MemoryShareOfApps * 100:F0}% of committed application memory.";
            var first = snap.Processes.Where(p => p.Name.Equals(app.AppKey, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.StartTime).FirstOrDefault();
            Details = first is null ? [] : DetailsFor(first.Pid, app.ProcessCount);
        }
        else if (row.Pid > 0)
        {
            Impact = "";
            Details = DetailsFor(row.Pid, 1);
        }
    }

    private IReadOnlyList<InfoItem> DetailsFor(int pid, int count)
    {
        var d = P.Processes.GetDetails(pid);
        if (d is null) return [];
        return
        [
            new("Description", d.Description, "File version resource"),
            new("Publisher", d.Publisher, "File version resource", Tooltip: "From the executable's version information, not a verified signature."),
            new("Version", d.FileVersion, "File version resource"),
            new("Location", d.ImagePath, "QueryFullProcessImageName", Sensitive: d.ImagePath?.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) == true),
            new("Architecture", d.Architecture, "IsWow64Process2"),
            new("Integrity level", d.IntegrityLevel, "Process token (query only)"),
            new("Started", d.StartTime is { } st0 ? UnitFormatter.When(st0) : null, "Process creation time"),
            new("Running for", d.StartTime is { } s ? UnitFormatter.Duration(DateTimeOffset.Now - s) : null, "Process creation time"),
            new("Parent", d.ParentName is null ? $"PID {d.ParentPid}" : $"{d.ParentName} (PID {d.ParentPid})", "Process snapshot"),
            new("Processes in this app", count.ToString(CultureInfo.CurrentCulture), "Process snapshot"),
            new("Command line", null, "Not collected: command lines can contain private information."),
        ];
    }
}

// ---------------------------------------------------------------- Startup

public sealed record StartupRow(string Name, string Publisher, string Location, string Status, string Impact, string Evidence, string Target)
{
    public bool Enabled => Status == "Enabled";
}

public sealed partial class StartupViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 10;

    public ObservableCollection<StartupRow> Items { get; } = [];
    public ObservableCollection<ChangeItemRow> Changes { get; } = [];

    [ObservableProperty] public partial string Summary { get; set; } = "";

    protected override void Refresh()
    {
        var items = P.Startup.Items.Select(i => new StartupRow(i.Name, i.Publisher ?? "Unknown publisher", i.Location, i.Enabled ? "Enabled" : "Disabled",
            i.Impact switch { StartupImpact.High => "High impact", StartupImpact.Moderate => "Moderate impact", StartupImpact.Low => "Low impact", _ => "Impact unknown" },
            i.ImpactEvidence ?? "", i.Target ?? "")).ToList();
        if (!Items.SequenceEqual(items))
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
        }
        var enabled = P.Startup.Items.Count(i => i.Enabled);
        var known = P.Startup.Items.Count(i => i.Impact != StartupImpact.Unknown);
        Summary = $"{enabled} of {P.Startup.Items.Count} startup apps are enabled." + (known == 0
            ? " Boot-impact measurements come from Windows' boot diagnostics, which this PC does not expose without administrator rights, so impact is shown as unknown rather than guessed."
            : "");
        var now = DateTimeOffset.Now;
        var changes = Services.GetRequiredService<HistoryStore>().QueryChanges(now.AddDays(-30), now).Where(c => c.Kind == "Startup")
            .Select(c => new ChangeItemRow(UnitFormatter.When(c.Timestamp), c.Title, "", c.Source)).ToList();
        if (!Changes.SequenceEqual(changes))
        {
            Changes.Clear();
            foreach (var c in changes) Changes.Add(c);
        }
    }
}

// ---------------------------------------------------------------- Drivers

public sealed record DriverRow(string Device, string Provider, string Version, string Date, string Signer, string Group);

public sealed record DriverGroupRow(string Name, int Count, IReadOnlyList<DriverRow> Drivers);

public sealed partial class DriversViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 30;

    public ObservableCollection<DriverGroupRow> Groups { get; } = [];
    public ObservableCollection<EventRow> Timeline { get; } = [];

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool HideMicrosoft { get; set; } = true;
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Correlation { get; set; } = "";
    [ObservableProperty] public partial bool Refreshing { get; set; }

    partial void OnSearchChanged(string value) => Build();
    partial void OnHideMicrosoftChanged(bool value) => Build();

    protected override void OnActivated(object? parameter) => Build();

    protected override void Refresh()
    {
        var now = DateTimeOffset.Now;
        var store = Services.GetRequiredService<HistoryStore>();
        var events = store.QueryEvents(now.AddDays(-90), now, [EventCategory.DriverChanged], 200).ToList();
        events.AddRange(store.QueryChanges(now.AddDays(-90), now).Where(c => c.Kind == "Driver" && c.Before is not null && c.After is not null)
            .Select(c => new SystemEvent(c.Timestamp, EventCategory.DriverChanged, Severity.Info, $"{c.Title}: {c.Before} → {c.After}", null, c.Source)));
        Timeline.Clear();
        foreach (var e in events.OrderByDescending(e => e.Timestamp).Take(60)) Timeline.Add(new EventRow(e));

        // Reliability after the most recent graphics driver change, versus the 30 days before it.
        var gfx = events.Where(e => e.Title.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || e.Title.Contains("Display", StringComparison.OrdinalIgnoreCase)
                                    || e.Title.Contains("Graphics", StringComparison.OrdinalIgnoreCase) || e.Title.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp).FirstOrDefault();
        if (gfx is not null)
        {
            var after = store.QueryEvents(gfx.Timestamp, now, [EventCategory.DisplayDriverReset], 200).Count;
            var before = store.QueryEvents(gfx.Timestamp.AddDays(-30), gfx.Timestamp, [EventCategory.DisplayDriverReset], 200).Count;
            Correlation = after == 0 && before == 0
                ? $"No display driver resets before or after the graphics driver change on {gfx.Timestamp:MMM d}."
                : $"{after} display driver reset(s) since the graphics driver change on {gfx.Timestamp:MMM d}, compared with {before} in the preceding 30 days." +
                  (after >= 3 && before == 0 ? " This is a strong correlation, though timing alone does not prove the driver is the cause." : "");
        }
        else
        {
            Correlation = "";
        }
    }

    private void Build()
    {
        var drivers = P.Drivers.Drivers.AsEnumerable();
        if (HideMicrosoft) drivers = drivers.Where(d => d.Provider is not "Microsoft" || d.Group is "Graphics" or "Networking" or "Audio" or "Storage");
        if (!string.IsNullOrWhiteSpace(Search))
            drivers = drivers.Where(d => d.DeviceName.Contains(Search, StringComparison.OrdinalIgnoreCase) || (d.Provider?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
                                         || (d.Version?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false));
        var order = new[] { "Graphics", "Chipset", "Audio", "Networking", "Storage", "Bluetooth", "USB", "Peripherals", "Firmware", "System" };
        Groups.Clear();
        foreach (var g in drivers.GroupBy(d => d.Group).OrderBy(g => Array.IndexOf(order, g.Key) is var i && i < 0 ? 99 : i))
            Groups.Add(new DriverGroupRow(g.Key, g.Count(), g.OrderBy(d => d.DeviceName).Select(d => new DriverRow(d.DeviceName, d.Provider ?? "—", d.Version ?? "—",
                d.Date?.ToString("d", CultureInfo.CurrentCulture) ?? "—", d.Signer ?? (d.IsSigned == true ? "Signed" : "—"), d.Group)).ToList()));
        Summary = $"{P.Drivers.Drivers.Count} drivers installed · inventory from {P.Drivers.LastRefreshed?.ToString("t", CultureInfo.CurrentCulture) ?? "—"}. Sentinel never installs, updates or rolls back drivers.";
    }

    [RelayCommand]
    private async Task RefreshInventoryAsync()
    {
        Refreshing = true;
        try
        {
            await P.Drivers.RefreshAsync(CancellationToken.None);
            Build();
        }
        finally
        {
            Refreshing = false;
        }
    }
}

// ---------------------------------------------------------------- Updates

public sealed record UpdateRow(string Date, string Title, string Kind, string Result, bool Failed);

public sealed partial class UpdatesViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 30;

    public ObservableCollection<UpdateRow> Updates { get; } = [];
    public IReadOnlyList<string> Kinds { get; } = ["All", "Windows (cumulative & feature)", "Drivers", "Defender definitions", ".NET", "Microsoft Store apps", "Failed"];

    [ObservableProperty] public partial int KindIndex { get; set; }
    [ObservableProperty] public partial bool RebootRequired { get; set; }
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Counts { get; set; } = [];
    [ObservableProperty] public partial string Correlation { get; set; } = "";

    partial void OnKindIndexChanged(int value) => Refresh();

    protected override void Refresh()
    {
        var all = P.Updates.Updates;
        RebootRequired = P.Updates.RebootRequired == true;
        IEnumerable<UpdateRecord> list = KindIndex switch
        {
            1 => all.Where(u => u.Kind is UpdateKind.Cumulative or UpdateKind.Feature),
            2 => all.Where(u => u.Kind == UpdateKind.Driver),
            3 => all.Where(u => u.Kind == UpdateKind.Definition),
            4 => all.Where(u => u.Kind == UpdateKind.DotNet),
            5 => all.Where(u => u.Kind == UpdateKind.StoreApp),
            6 => all.Where(u => u.Result.Contains("Failed", StringComparison.Ordinal)),
            _ => all,
        };
        var rows = list.Take(200).Select(u => new UpdateRow(UnitFormatter.When(u.Date),
            u.Kind == UpdateKind.StoreApp ? Platform.Windows.Events.EventMapping.StoreApp(u.Title) + " (Microsoft Store)" : u.Title,
            u.Kind switch { UpdateKind.Cumulative => "Cumulative", UpdateKind.Feature => "Feature", UpdateKind.Driver => "Driver", UpdateKind.Definition => "Definitions", UpdateKind.DotNet => ".NET", UpdateKind.StoreApp => "Store app", _ => "Other" },
            u.Result, u.Result.Contains("Failed", StringComparison.Ordinal))).ToList();
        if (!Updates.SequenceEqual(rows))
        {
            Updates.Clear();
            foreach (var r in rows) Updates.Add(r);
        }
        var since = DateTimeOffset.Now.AddDays(-30);
        int C(Func<UpdateRecord, bool> f) => all.Count(u => u.Date >= since && f(u));
        Counts =
        [
            new("Windows cumulative / feature", C(u => u.Kind is UpdateKind.Cumulative or UpdateKind.Feature).ToString(CultureInfo.CurrentCulture), "Windows Update history (30 days)"),
            new("Drivers", C(u => u.Kind == UpdateKind.Driver).ToString(CultureInfo.CurrentCulture), "Windows Update history (30 days)"),
            new("Defender definitions", C(u => u.Kind == UpdateKind.Definition).ToString(CultureInfo.CurrentCulture), "Windows Update history (30 days)"),
            new("Microsoft Store apps", C(u => u.Kind == UpdateKind.StoreApp).ToString(CultureInfo.CurrentCulture), "Windows Update history (30 days)"),
            new("Failed installations", C(u => u.Result.Contains("Failed", StringComparison.Ordinal)).ToString(CultureInfo.CurrentCulture), "Windows Update history (30 days)"),
        ];

        var latest = all.FirstOrDefault(u => u.Kind is UpdateKind.Cumulative or UpdateKind.Feature && u.Result.StartsWith("Succeeded", StringComparison.Ordinal));
        if (latest is not null)
        {
            var store = Services.GetRequiredService<HistoryStore>();
            var cats = new[] { EventCategory.AppCrash, EventCategory.Bugcheck, EventCategory.UnexpectedShutdown, EventCategory.DisplayDriverReset };
            var days = Math.Max(1, Math.Min(7, (DateTimeOffset.Now - latest.Date).TotalDays));
            var after = store.QueryEvents(latest.Date, latest.Date.AddDays(days), cats, 1000).Count;
            var before = store.QueryEvents(latest.Date.AddDays(-days), latest.Date, cats, 1000).Count;
            Correlation = $"After the latest Windows update ({latest.Date:MMM d}): {after} crash/stability event(s) in {days:F0} day(s), versus {before} in the {days:F0} day(s) before. " +
                          (after > before * 2 && after >= 3 ? "Stability changed after the update; this is a correlation, not proof." : "No meaningful change in stability.");
        }
        else
        {
            Correlation = "No Windows cumulative or feature update appears in the update history Windows keeps, so there is nothing to compare yet. " +
                          "When one installs, Sentinel compares crashes and restarts in the days before and after it.";
        }
    }
}
