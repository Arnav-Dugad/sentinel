namespace Sentinel.App.Views;

public sealed record PageInfo(string Tag, string Title, string Glyph, Type PageType, string Group, string? DetailCategory, string Description, params string[] Keywords);

/// <summary>Single source of truth for navigation, the sidebar and the command palette.</summary>
public static class PageRegistry
{
    public static readonly IReadOnlyList<PageInfo> All =
    [
        new("Home", "Home", "", typeof(HomePage), "", null, "How your PC is doing right now", "overview", "dashboard", "status"),

        new("CPU", "CPU", "", typeof(CpuPage), "System", "CPU", "Processor load, clocks, cores and limits", "processor", "cores", "clock", "frequency", "utilization", "dpc"),
        new("GPU", "GPU", "", typeof(GpuPage), "System", "GPU", "Graphics adapters, engines, temperature and power", "graphics", "nvidia", "amd", "intel", "vram", "video"),
        new("Memory", "Memory", "", typeof(MemoryPage), "System", "Memory", "Physical memory, commit, cache and modules", "ram", "commit", "page file", "dimm", "leak"),
        new("Storage", "Storage", "", typeof(StoragePage), "System", "Storage", "Drives, health, temperature and I/O", "ssd", "nvme", "disk", "drive health", "smart", "volume"),
        new("Battery", "Battery", "", typeof(BatteryPage), "System", "Battery", "Battery Lab: health, sessions and drain", "battery health", "charge", "drain", "capacity", "cycles"),
        new("Network", "Network", "", typeof(NetworkPage), "System", "Network", "Adapters, Wi-Fi and throughput", "wifi", "wi-fi", "ethernet", "throughput", "signal", "ip"),
        new("Thermals", "Thermals", "", typeof(ThermalsPage), "System", "Thermals", "Temperatures and their causes", "temperature", "heat", "hot", "fan", "thermal"),
        new("Devices", "Devices", "", typeof(DevicesPage), "System", "Devices", "Devices, displays, audio and this computer", "usb", "bluetooth", "display", "monitor", "audio", "bios", "computer"),

        new("Health", "Health", "", typeof(HealthPage), "Intelligence", null, "Transparent system health assessment", "score", "status"),
        new("Timeline", "Timeline", "", typeof(TimelinePage), "Intelligence", null, "System timeline and what changed", "what changed", "events", "history", "changes"),
        new("Anomalies", "Anomalies", "", typeof(AnomaliesPage), "Intelligence", null, "Unusual behavior compared with this PC's baseline", "unusual", "baseline"),
        new("Reliability", "Reliability", "", typeof(ReliabilityPage), "Intelligence", null, "Crashes, restarts and stop errors", "crash", "bsod", "bugcheck", "restart", "blue screen"),
        new("Ask", "Ask Sentinel", "", typeof(AskPage), "Intelligence", null, "Ask questions about your PC", "ai", "question", "why"),
        new("Compare", "Compare", "", typeof(ComparePage), "Intelligence", null, "Compare two periods", "versus", "before after"),
        new("TimeMachine", "Time Machine", "", typeof(TimeMachinePage), "Intelligence", null, "Reconstruct your PC at a past moment", "past", "history", "rewind"),

        new("Processes", "Processes", "", typeof(ProcessesPage), "Software", "Processes", "Apps and processes using resources", "task manager", "apps", "process"),
        new("Startup", "Startup", "", typeof(StartupPage), "Software", "Startup", "Apps that start with Windows", "boot", "startup apps"),
        new("Drivers", "Drivers", "", typeof(DriversPage), "Software", null, "Driver inventory and change history", "driver", "version"),
        new("Updates", "Updates", "", typeof(UpdatesPage), "Software", "Updates", "Windows Update history", "windows update", "kb", "patch"),

        new("Performance", "Performance", "", typeof(PerformancePage), "Diagnostics", null, "Investigate slowdowns", "slow", "investigation", "lag"),
        new("Power", "Power", "", typeof(PowerPage), "Diagnostics", "Power", "Power state and battery drain", "power plan", "ac", "battery saver"),
        new("Sleep", "Sleep", "", typeof(SleepPage), "Diagnostics", null, "Sleep, wake and standby drain", "standby", "wake", "hibernate"),
        new("NetworkTest", "Network test", "", typeof(NetworkTestPage), "Diagnostics", null, "Latency and DNS test (on demand)", "ping", "latency", "dns"),
        new("Reports", "Reports", "", typeof(ReportsPage), "Diagnostics", null, "Create local reports and support bundles", "export", "report", "support bundle"),

        new("Settings", "Settings", "", typeof(SettingsPage), "", null, "Preferences, privacy, providers and local AI", "preferences", "ollama", "privacy", "units"),
        new("Welcome", "Welcome", "", typeof(WelcomePage), "Hidden", null, "Welcome"),
    ];

    public static readonly IReadOnlyList<string> Groups = ["System", "Intelligence", "Software", "Diagnostics"];

    public static PageInfo? Find(string tag) => All.FirstOrDefault(p => p.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

    public static PageInfo? Find(Type type) => All.FirstOrDefault(p => p.PageType == type);
}
