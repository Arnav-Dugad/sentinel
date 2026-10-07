using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Sentinel.AI;
using Sentinel.Analytics;
using Sentinel.App.Services;
using Sentinel.App.Views;
using Sentinel.App.Windows;
using Sentinel.Core.Logging;
using Sentinel.Core.Metrics;
using Sentinel.Core.Providers;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Diagnostics;
using Sentinel.Diagnostics.Reports;
using Sentinel.Intelligence;
using Sentinel.Platform.Windows.Events;
using Sentinel.Platform.Windows.Providers;
using Sentinel.Platform.Windows.Services;
using Sentinel.Telemetry;
using Sentinel.Telemetry.Simulation;

namespace Sentinel.App;

public partial class App : Application
{
    private ServiceProvider? _provider;
    private TrayIcon? _tray;
    private QuickPanelWindow? _quick;
    private MiniMonitorWindow? _mini;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _exiting;
    private ILogger<App>? _log;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unhandled UI exception");
            // Keep the app alive for non-fatal UI errors; telemetry runs independently.
            e.Handled = true;
        };
    }

    public static new App? Current => Application.Current as App;

    public static IServiceProvider Services { get; private set; } = null!;

    public MainWindow? MainWindow { get; private set; }

    public bool IsSimulation { get; private set; }

    public DispatcherQueue Dispatcher { get; } = DispatcherQueue.GetForCurrentThread();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settingsPaths = new SentinelPaths();
        var settings = new JsonSettingsStore(settingsPaths);
        IsSimulation = settings.Current.DeveloperSimulation || Program.ForceSimulation;
        var paths = IsSimulation ? new SentinelPaths(simulation: true) : settingsPaths;

        _provider = ConfigureServices(paths, settings, IsSimulation);
        Services = _provider;
        _log = _provider.GetRequiredService<ILogger<App>>();
        _log.LogInformation("Sentinel starting (simulation: {Sim}, elevated: {Elevated})", IsSimulation, Environment.IsPrivilegedProcess);

        _provider.GetRequiredService<HistoryStore>().Initialize();
        MetricCatalog.RegisterCore(_provider.GetRequiredService<LiveMetricStore>());

        var notifications = _provider.GetRequiredService<AppNotificationService>();
        notifications.NavigateRequested += (_, page) => Dispatcher.TryEnqueue(() => ShowMainWindow(page));
        notifications.Register();

        CreateTray();

        var showWindow = !Program.StartInBackground || !settings.Current.FirstRunCompleted;
        if (showWindow) ShowMainWindow(settings.Current.FirstRunCompleted ? null : "Welcome");
        else _provider.GetRequiredService<TelemetryEngine>().SetVisible(false);

        _ = StartIntelligenceAsync();
        if (!IsSimulation) _provider.GetRequiredService<Updates.UpdateService>().Start(Program.JustUpdated);
    }

    private async Task StartIntelligenceAsync()
    {
        try
        {
            await Services.GetRequiredService<IntelligenceService>().StartAsync(_shutdown.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogCritical(ex, "Failed to start telemetry");
        }
    }

    private static ServiceProvider ConfigureServices(SentinelPaths paths, JsonSettingsStore settings, bool simulation)
    {
        var s = new ServiceCollection();
        var fileLogger = new RotatingFileLoggerProvider(paths.Logs);
        s.AddSingleton(fileLogger);
        s.AddLogging(b => b.ClearProviders().AddProvider(fileLogger).SetMinimumLevel(LogLevel.Information));
        s.AddSingleton(paths);
        s.AddSingleton<ISettingsStore>(settings);
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<UnitFormatter>();
        s.AddSingleton<LiveMetricStore>();
        s.AddSingleton(sp => new HistoryStore(paths.Database, sp.GetRequiredService<ILogger<HistoryStore>>()));
        s.AddSingleton<HistoryRecorder>();
        s.AddSingleton<IMetricSink>(sp => sp.GetRequiredService<HistoryRecorder>());
        s.AddSingleton<IEventSink>(sp => sp.GetRequiredService<HistoryRecorder>());

        if (simulation)
        {
            s.AddSingleton(sp => new SimulationWorld(SimulationWorld.Parse(settings.Current.SimulationScenario), TimeProvider.System));
            Provider<SimulatedSystemProvider, ISystemInventoryProvider>(s);
            Provider<SimulatedCpuProvider, ICpuTelemetryProvider>(s);
            Provider<SimulatedMemoryProvider, IMemoryTelemetryProvider>(s);
            Provider<SimulatedGpuProvider, IGpuTelemetryProvider>(s);
            Provider<SimulatedStorageProvider, IStorageTelemetryProvider>(s);
            Provider<SimulatedBatteryProvider, IBatteryTelemetryProvider>(s);
            Provider<SimulatedNetworkProvider, INetworkTelemetryProvider>(s);
            Provider<SimulatedThermalProvider, IThermalTelemetryProvider>(s);
            Provider<SimulatedPowerProvider, IPowerTelemetryProvider>(s);
            Provider<SimulatedProcessProvider, IProcessTelemetryProvider>(s);
            Provider<SimulatedEventProvider, IWindowsEventProvider>(s);
        }
        else
        {
            Provider<SystemInventoryProvider, ISystemInventoryProvider>(s);
            Provider<CpuProvider, ICpuTelemetryProvider>(s);
            Provider<MemoryProvider, IMemoryTelemetryProvider>(s);
            Provider<GpuProvider, IGpuTelemetryProvider>(s);
            Provider<StorageProvider, IStorageTelemetryProvider>(s);
            Provider<BatteryProvider, IBatteryTelemetryProvider>(s);
            Provider<NetworkProvider, INetworkTelemetryProvider>(s);
            Provider<ThermalProvider, IThermalTelemetryProvider>(s);
            Provider<PowerProvider, IPowerTelemetryProvider>(s);
            Provider<ProcessProvider, IProcessTelemetryProvider>(s);
            Provider<EventLogProvider, IWindowsEventProvider>(s);
        }
        // Inventory providers are read-only and always real (they are static in simulation and clearly labelled by the banner).
        Provider<DisplayProvider, IDisplayTelemetryProvider>(s);
        Provider<AudioProvider, IAudioTelemetryProvider>(s);
        Provider<DeviceProvider, IDeviceInventoryProvider>(s);
        Provider<DriverProvider, IDriverInventoryProvider>(s);
        Provider<SoftwareInventoryProvider, ISoftwareInventoryProvider>(s);
        Provider<StartupProvider, IStartupProvider>(s);
        Provider<UpdateHistoryProvider, IUpdateHistoryProvider>(s);
        Provider<OemProvider, IOemTelemetryProvider>(s);

        s.AddSingleton<TelemetryEngine>();
        s.AddSingleton<ProviderSet>();
        s.AddSingleton<BaselineEngine>();
        s.AddSingleton<IAnomalyDetector, BaselineDeviationDetector>();
        s.AddSingleton<IAnomalyDetector, EventClusterDetector>();
        s.AddSingleton<IAnomalyDetector, MemoryGrowthDetector>();
        s.AddSingleton<IAnomalyDetector, DeviceFlappingDetector>();
        s.AddSingleton<IAnomalyDetector, DiskWriteVolumeDetector>();
        s.AddSingleton<AnomalyEngine>();
        s.AddSingleton<ChangeTracker>();
        s.AddSingleton<SessionTracker>();
        s.AddSingleton<InsightEngine>();
        s.AddSingleton<HealthModel>();
        s.AddSingleton<CorrelationEngine>();
        s.AddSingleton<TimelineService>();
        s.AddSingleton<AppNotificationService>();
        s.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<AppNotificationService>());
        s.AddSingleton<NotificationPolicy>();
        s.AddSingleton<IntelligenceService>();
        s.AddSingleton<DiagnosticsEngine>();
        s.AddSingleton<PerformanceInvestigation>();
        s.AddSingleton<ReportBuilder>();
        s.AddSingleton<TrustedToolRunner>();
        s.AddSingleton<BatteryReportService>();
        s.AddSingleton<NetworkTestService>();
        s.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));
        s.AddSingleton<OllamaClient>();
        s.AddSingleton<AskSentinelService>();
        s.AddSingleton<Updates.UpdateService>();

        s.AddSingleton(_ => new UiClock(DispatcherQueue.GetForCurrentThread()));
        s.AddSingleton<SensitiveInfoState>();
        s.AddSingleton<MotionPolicy>();
        s.AddSingleton<ThemeService>();
        return s.BuildServiceProvider();
    }

    private static void Provider<TImpl, TInterface>(ServiceCollection s) where TImpl : class, TInterface where TInterface : class, ITelemetryProvider
    {
        s.AddSingleton<TImpl>();
        s.AddSingleton<TInterface>(sp => sp.GetRequiredService<TImpl>());
        s.AddSingleton<ITelemetryProvider>(sp => sp.GetRequiredService<TImpl>());
    }

    private void CreateTray()
    {
        try
        {
            _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Sentinel.ico"));
            var settings = Services.GetRequiredService<ISettingsStore>();
            _tray.MenuState = () => (settings.Current.RecordingPaused, settings.Current.PrivacyMode);
            var updates = Services.GetRequiredService<Updates.UpdateService>();
            _tray.PendingUpdate = () => updates.IsReady && updates.CanInstall ? updates.NewVersion : null;
            _tray.Command += (_, cmd) => Dispatcher.TryEnqueue(() => OnTrayCommand(cmd));
            string? balloonTarget = null;
            Services.GetRequiredService<AppNotificationService>().Fallback = n => Dispatcher.TryEnqueue(() =>
            {
                balloonTarget = n.NavigateTo;
                _tray?.ShowBalloon(n.Title, n.Body);
            });
            _tray.BalloonClicked += (_, _) => Dispatcher.TryEnqueue(() => ShowMainWindow(balloonTarget));
            var intelligence = Services.GetRequiredService<IntelligenceService>();
            intelligence.Updated += (_, _) =>
            {
                var headline = intelligence.Health?.Headline ?? "Sentinel";
                Dispatcher.TryEnqueue(() => _tray?.SetTooltip("Sentinel — " + headline));
            };
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Tray icon unavailable");
        }
    }

    private void OnTrayCommand(TrayCommand cmd)
    {
        var settings = Services.GetRequiredService<ISettingsStore>();
        switch (cmd)
        {
            case TrayCommand.Open:
                ShowMainWindow();
                break;
            case TrayCommand.QuickPanel:
                ToggleQuickPanel();
                break;
            case TrayCommand.MiniMonitor:
                ShowMiniMonitor();
                break;
            case TrayCommand.ToggleRecording:
                settings.Update(s => s.RecordingPaused = !s.RecordingPaused);
                break;
            case TrayCommand.TogglePrivacy:
                settings.Update(s => s.PrivacyMode = !s.PrivacyMode);
                break;
            case TrayCommand.Exit:
                _ = ExitAsync();
                break;
            case TrayCommand.InstallUpdate:
                Services.GetRequiredService<Updates.UpdateService>().RestartToUpdate();
                break;
        }
    }

    public void ToggleQuickPanel()
    {
        if (_quick is { IsOpen: true })
        {
            _quick.Close();
            return;
        }
        _quick = new QuickPanelWindow();
        _quick.Closed += (_, _) => _quick = null;
        _quick.ShowNearTray();
    }

    public void ShowMiniMonitor()
    {
        if (_mini is not null)
        {
            WindowHelper.BringToFront(_mini);
            return;
        }
        _mini = new MiniMonitorWindow();
        _mini.Closed += (_, _) => _mini = null;
        _mini.Activate();
    }

    public void ShowMainWindow(string? navigateTo = null)
    {
        if (MainWindow is null)
        {
            MainWindow = new MainWindow();
            MainWindow.Closed += (_, _) => MainWindow = null;
        }
        WindowHelper.BringToFront(MainWindow);
        if (navigateTo is not null) MainWindow.NavigateTo(navigateTo);
    }

    /// <summary>Called when the user launches Sentinel again while it is already running.</summary>
    public void OnRedirectedActivation() => Dispatcher.TryEnqueue(() => ShowMainWindow());

    /// <summary>The main window asks whether closing should hide to the tray instead of exiting.</summary>
    public bool ShouldHideOnClose => !_exiting && _tray is not null && Services.GetRequiredService<ISettingsStore>().Current.CloseToTray;

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _log?.LogInformation("Sentinel exiting");
        _quick?.Close();
        _mini?.Close();
        MainWindow?.Close();
        _tray?.Dispose();
        _tray = null;
        try
        {
            await _shutdown.CancelAsync();
            await Services.GetRequiredService<IntelligenceService>().DisposeAsync();
            Services.GetRequiredService<Updates.UpdateService>().Dispose();
            Services.GetRequiredService<AppNotificationService>().Dispose();
            Services.GetRequiredService<HistoryStore>().Dispose();
            _provider?.GetRequiredService<RotatingFileLoggerProvider>().Dispose();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Shutdown was not clean");
        }
        Exit();
    }
}
