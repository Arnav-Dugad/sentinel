using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.AI;
using Sentinel.App.Services;
using Sentinel.Core.Logging;
using Sentinel.Core.Providers;
using Sentinel.Core.Settings;
using Sentinel.Data;
using Sentinel.Telemetry;
using Sentinel.Telemetry.Simulation;

namespace Sentinel.App.ViewModels;

public sealed partial class ProviderRow : ObservableObject
{
    private readonly ISettingsStore _settings;

    public ProviderRow(ProviderState state, ISettingsStore settings)
    {
        _settings = settings;
        Id = state.Id;
        Name = state.Provider.Descriptor.Name;
        Category = state.Provider.Descriptor.Category;
        Sources = string.Join(" · ", state.Provider.Descriptor.DataSources);
        Detail = $"Access: {(state.Provider.Descriptor.Access == Domain.AccessRequirement.None ? "no administrator rights" : state.Provider.Descriptor.Access.ToString())} · Cost: {state.Provider.Descriptor.Cost} · Precision: {state.Provider.Descriptor.Precision}";
        Enabled = !settings.Current.DisabledProviders.Contains(Id, StringComparer.OrdinalIgnoreCase);
        Update(state);
    }

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public string Sources { get; }
    public string Detail { get; }

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Unavailable { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value) => _settings.Update(s =>
    {
        s.DisabledProviders.RemoveAll(id => id.Equals(Id, StringComparison.OrdinalIgnoreCase));
        if (!value) s.DisabledProviders.Add(Id);
    });

    public void Update(ProviderState state)
    {
        Status = state.Status.Health switch
        {
            ProviderHealth.Healthy => state.Status.LastDuration.TotalMilliseconds > 0 ? $"Working · last sample {state.Status.LastDuration.TotalMilliseconds:F0} ms" : "Working",
            ProviderHealth.Unavailable => "Not exposed by this system",
            ProviderHealth.Degraded => "Retrying: " + state.Status.Message,
            ProviderHealth.Failed => "Paused after repeated failures: " + state.Status.Message,
            ProviderHealth.Disabled => "Disabled",
            _ => "Starting…",
        };
        Unavailable = string.Join("\n", state.Provider.Capabilities.Where(c => !c.Available).Select(c => $"{c.Name}: {c.Reason ?? "not available"}"));
    }
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsStore _settings = App.Services.GetRequiredService<ISettingsStore>();
    private readonly HistoryStore _store = App.Services.GetRequiredService<HistoryStore>();
    private readonly OllamaClient _ollama = App.Services.GetRequiredService<OllamaClient>();
    private readonly TelemetryEngine _engine = App.Services.GetRequiredService<TelemetryEngine>();
    private bool _loading = true;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt;

    /// <summary>The update service, bound directly so its status updates live.</summary>
    public Updates.UpdateService Updates { get; } = App.Services.GetRequiredService<Updates.UpdateService>();

    protected override int RefreshEveryTicks => 3;

    public ObservableCollection<ProviderRow> Providers { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public IReadOnlyList<string> Themes { get; } = ["Use Windows setting", "Light", "Dark"];
    public IReadOnlyList<string> Retentions { get; } = ["7 days", "30 days", "90 days", "1 year", "Unlimited"];
    public IReadOnlyList<string> Scenarios { get; } = Enum.GetNames<SimulationScenario>();
    public IReadOnlyList<string> SafetyClauses { get; } = SafetyContract.Clauses;

    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial bool Fahrenheit { get; set; }
    [ObservableProperty] public partial bool Bits { get; set; }
    [ObservableProperty] public partial bool DecimalBytes { get; set; }
    [ObservableProperty] public partial bool ReduceMotion { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool Notifications { get; set; }
    [ObservableProperty] public partial int RetentionIndex { get; set; }
    [ObservableProperty] public partial bool RecordingPaused { get; set; }
    [ObservableProperty] public partial bool RecordAppHistory { get; set; }
    [ObservableProperty] public partial bool PrivacyMode { get; set; }
    [ObservableProperty] public partial bool OllamaEnabled { get; set; }
    [ObservableProperty] public partial string OllamaEndpoint { get; set; } = "";
    [ObservableProperty] public partial string? OllamaModel { get; set; }
    [ObservableProperty] public partial string OllamaStatus { get; set; } = "";
    [ObservableProperty] public partial bool Simulation { get; set; }
    [ObservableProperty] public partial int ScenarioIndex { get; set; }
    [ObservableProperty] public partial string DatabaseSize { get; set; } = "";
    [ObservableProperty] public partial string DatabasePath { get; set; } = "";
    [ObservableProperty] public partial bool CheckForUpdates { get; set; }
    [ObservableProperty] public partial bool InstallUpdatesAutomatically { get; set; }
    [ObservableProperty] public partial string FootprintCpu { get; set; } = "—";
    [ObservableProperty] public partial string FootprintMemory { get; set; } = "—";
    [ObservableProperty] public partial string FootprintDetail { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; set; } = "";

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    [ObservableProperty] public partial bool RestartNeeded { get; set; }

    public string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.9.0";

    protected override void OnActivated(object? parameter)
    {
        _loading = true;
        var s = _settings.Current;
        ThemeIndex = (int)s.Theme;
        Fahrenheit = s.Temperature == TemperatureUnit.Fahrenheit;
        Bits = s.Throughput == ThroughputUnit.BitsPerSecond;
        DecimalBytes = s.Bytes == ByteUnitSystem.Decimal;
        ReduceMotion = s.ReduceMotion;
        CloseToTray = s.CloseToTray;
        StartWithWindows = StartupRegistration.IsEnabled();
        Notifications = s.NotificationsEnabled;
        RetentionIndex = s.RetentionDays switch { 7 => 0, 30 => 1, 90 => 2, 365 => 3, _ => 4 };
        RecordingPaused = s.RecordingPaused;
        RecordAppHistory = s.RecordAppHistory;
        PrivacyMode = s.PrivacyMode;
        OllamaEnabled = s.OllamaEnabled;
        OllamaEndpoint = s.OllamaEndpoint;
        OllamaModel = s.OllamaModel;
        if (s.OllamaModel is { } m && !Models.Contains(m)) Models.Add(m);
        Simulation = s.DeveloperSimulation;
        ScenarioIndex = Math.Max(0, Array.IndexOf(Enum.GetNames<SimulationScenario>(), s.SimulationScenario));
        DatabasePath = _store.Path;
        CheckForUpdates = s.CheckForUpdates;
        InstallUpdatesAutomatically = s.InstallUpdatesAutomatically;
        Providers.Clear();
        foreach (var p in _engine.Providers.OrderBy(p => p.Provider.Descriptor.Category)) Providers.Add(new ProviderRow(p, _settings));
        _loading = false;
    }

    protected override void Refresh()
    {
        DatabaseSize = U.Bytes(_store.DatabaseSizeBytes());
        UpdateFootprint();
        foreach (var row in Providers)
            if (_engine.Find(row.Id) is { } st) row.Update(st);
    }

    private void Save(Action<SentinelSettings> change)
    {
        if (_loading) return;
        _settings.Update(change);
    }

    partial void OnThemeIndexChanged(int value) => Save(s => s.Theme = (ThemePreference)Math.Clamp(value, 0, 2));
    partial void OnFahrenheitChanged(bool value) => Save(s => s.Temperature = value ? TemperatureUnit.Fahrenheit : TemperatureUnit.Celsius);
    partial void OnBitsChanged(bool value) => Save(s => s.Throughput = value ? ThroughputUnit.BitsPerSecond : ThroughputUnit.BytesPerSecond);
    partial void OnDecimalBytesChanged(bool value) => Save(s => s.Bytes = value ? ByteUnitSystem.Decimal : ByteUnitSystem.Binary);
    partial void OnReduceMotionChanged(bool value) => Save(s => s.ReduceMotion = value);
    partial void OnCloseToTrayChanged(bool value) => Save(s => s.CloseToTray = value);
    partial void OnNotificationsChanged(bool value) => Save(s => s.NotificationsEnabled = value);
    partial void OnRetentionIndexChanged(int value) => Save(s => s.RetentionDays = value switch { 0 => 7, 1 => 30, 2 => 90, 3 => 365, _ => 0 });
    partial void OnRecordingPausedChanged(bool value) => Save(s => s.RecordingPaused = value);
    partial void OnRecordAppHistoryChanged(bool value) => Save(s => s.RecordAppHistory = value);
    partial void OnPrivacyModeChanged(bool value) => Save(s => s.PrivacyMode = value);
    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s.CheckForUpdates = value);
    partial void OnInstallUpdatesAutomaticallyChanged(bool value) => Save(s => s.InstallUpdatesAutomatically = value);

    [RelayCommand]
    private Task CheckUpdatesAsync() => Updates.CheckAsync(manual: true);

    [RelayCommand]
    private Task DownloadUpdateAsync() => Updates.DownloadAsync();

    [RelayCommand]
    private void InstallUpdate()
    {
        if (!Updates.RestartToUpdate()) Message = "The update could not be started. Try Check for updates again.";
    }

    /// <summary>Sentinel's own cost, measured the same way it measures every other app — shown so the budget is verifiable.</summary>
    private void UpdateFootprint()
    {
        using var self = Process.GetCurrentProcess();
        var now = DateTimeOffset.Now;
        var cpu = self.TotalProcessorTime;
        if (_lastCpuAt != default)
        {
            var pct = (cpu - _lastCpu).TotalMilliseconds / (now - _lastCpuAt).TotalMilliseconds / Environment.ProcessorCount * 100;
            FootprintCpu = $"{Math.Max(0, pct):F2}%";
        }
        _lastCpu = cpu;
        _lastCpuAt = now;
        var gc = GC.GetGCMemoryInfo();
        FootprintMemory = U.Bytes(self.PrivateMemorySize64);
        FootprintDetail = $"Managed heap {U.Bytes(GC.GetTotalMemory(false))} (committed {U.Bytes(gc.TotalCommittedBytes)}) · working set {U.Bytes(self.WorkingSet64)}, " +
                          "which includes shared graphics-driver and system libraries · " +
                          $"{_engine.Providers.Count} providers · window open, so sampling is faster than in the tray.";
    }

    [RelayCommand]
    private void OpenReleases() => _ = global::Windows.System.Launcher.LaunchUriAsync(Updates.ReleasesPage);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        try
        {
            StartupRegistration.Set(value);
            Save(s => s.StartWithWindows = value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Message = "Could not change the sign-in setting: " + ex.Message;
        }
    }

    partial void OnOllamaEnabledChanged(bool value) => Save(s => s.OllamaEnabled = value);

    partial void OnOllamaModelChanged(string? value) => Save(s => s.OllamaModel = value);

    partial void OnSimulationChanged(bool value)
    {
        Save(s => s.DeveloperSimulation = value);
        if (!_loading) RestartNeeded = true;
    }

    partial void OnScenarioIndexChanged(int value)
    {
        Save(s => s.SimulationScenario = Enum.GetNames<SimulationScenario>()[Math.Clamp(value, 0, Scenarios.Count - 1)]);
        if (!_loading && Simulation) RestartNeeded = true;
    }

    [RelayCommand]
    private async Task TestOllamaAsync()
    {
        var endpoint = OllamaEndpoint.Trim();
        if (!OllamaClient.IsLoopback(endpoint, out _))
        {
            OllamaStatus = "Only a local endpoint on this PC (for example http://localhost:11434) is allowed, so your data never leaves this computer.";
            return;
        }
        Save(s => s.OllamaEndpoint = endpoint);
        OllamaStatus = "Connecting…";
        try
        {
            var models = await _ollama.ListModelsAsync(endpoint, CancellationToken.None);
            Models.Clear();
            foreach (var m in models) Models.Add(m.Name);
            OllamaStatus = models.Count == 0
                ? "Connected, but no models are installed. Install one with Ollama yourself; Sentinel never downloads models."
                : $"Connected. {models.Count} model(s) found: " + string.Join(", ", models.Select(m => $"{m.Name} ({m.TierLabel})"));
            if (OllamaModel is null && models.FirstOrDefault(m => m.Tier == ModelTier.Balanced) is { } pick) OllamaModel = pick.Name;
        }
        catch (OllamaException ex)
        {
            OllamaStatus = ex.Message;
        }
    }

    [RelayCommand]
    private void ExportLog()
    {
        var logs = Services.GetRequiredService<RotatingFileLoggerProvider>().ExportRedacted();
        var paths = Services.GetRequiredService<SentinelPaths>();
        var file = Path.Combine(paths.Reports, $"Sentinel-DiagnosticLog-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(file, logs);
        Message = "Saved a redacted diagnostic log to " + file;
        Reveal(file);
    }

    [RelayCommand]
    private void OpenDataFolder() => Reveal(Services.GetRequiredService<SentinelPaths>().Root);

    public async Task ClearHistoryAsync()
    {
        await _store.ClearAllHistoryAsync();
        Message = "All recorded history was deleted.";
        Refresh();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { "--after-restart" } });
        if (App.Current is { } app) await app.ExitAsync();
    }

    /// <summary>Opens Explorer at a file or folder Sentinel created. Fixed executable, quoted path argument, no shell.</summary>
    public static void Reveal(string path)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        psi.ArgumentList.Add(File.Exists(path) ? "/select," + path : path);
        Process.Start(psi);
    }
}
