using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Sentinel.Core.Settings;
using Sentinel.Core.Updates;
using Sentinel.Domain;
using Sentinel.Intelligence;

namespace Sentinel.App.Updates;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Failed,
    Off,
}

/// <summary>
/// Checks GitHub Releases on a slow schedule (shortly after start, then every 6 hours), downloads and verifies new
/// versions in the background when allowed, and installs them on restart. All state changes are raised on the UI thread.
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateClient _client;
    private readonly ISettingsStore _settings;
    private readonly INotificationSink _notifications;
    private readonly ILogger<UpdateService> _log;
    private readonly DispatcherQueue _ui;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private ReleaseInfo? _latest;
    private StagedUpdate? _staged;

    public UpdateService(ISettingsStore settings, INotificationSink notifications, ILogger<UpdateService> log, ILogger<UpdateClient> clientLog)
    {
        _settings = settings;
        _notifications = notifications;
        _log = log;
        _ui = DispatcherQueue.GetForCurrentThread();
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Sentinel/{CurrentVersion}");
        _client = new UpdateClient(http, UpdateEnvironment.UpdatesRoot, UpdateSignature.OfficialPublicKeyPem, clientLog);
        CanInstall = UpdateEnvironment.CanSelfUpdate(out var reason);
        InstallBlockedReason = reason;
        KeyFingerprint = UpdateSignature.Fingerprint(UpdateSignature.OfficialPublicKeyPem);
        LastChecked = settings.Current.LastUpdateCheck;
        settings.Changed += (_, _) => _wake.Release();
    }

    public AppVersion CurrentVersion { get; } = UpdateEnvironment.CurrentVersion;

    public bool CanInstall { get; }

    public string InstallBlockedReason { get; }

    public string KeyFingerprint { get; }

    public Uri ReleasesPage => _latest?.Page ?? _staged?.Page ?? UpdateFeed.ReleasesPage;

    [ObservableProperty] public partial UpdateState State { get; private set; } = UpdateState.Idle;
    [ObservableProperty] public partial string Status { get; private set; } = "Updates have not been checked yet.";
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial double Progress { get; private set; }
    [ObservableProperty] public partial string NewVersion { get; private set; } = "";
    [ObservableProperty] public partial string Notes { get; private set; } = "";
    [ObservableProperty] public partial DateTimeOffset? LastChecked { get; private set; }
    [ObservableProperty] public partial InstallResult? LastInstall { get; private set; }

    public bool IsReady => State == UpdateState.Ready;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;
    public bool IsChecking => State == UpdateState.Checking;
    public bool IsDownloading => State == UpdateState.Downloading;
    public bool CanDownload => State == UpdateState.Available && CanInstall;
    public bool CanRestart => IsReady && CanInstall;

    /// <summary>"Updated from 0.9.0 to 0.9.1 on 7 Oct" - shown for a week after an update.</summary>
    public string LastInstallSummary => LastInstall is { } r
        ? (r.Succeeded ? $"Updated from {r.From} to {r.To} on {r.Time.LocalDateTime:d MMM, HH:mm}." : $"Last update attempt ({r.To}) on {r.Time.LocalDateTime:d MMM, HH:mm}: {r.Message}")
        : "";

    public string Glyph => State switch
    {
        UpdateState.UpToDate => "\uE930",
        UpdateState.Ready => "\uE777",
        UpdateState.Downloading => "\uE896",
        UpdateState.Failed => "\uE7BA",
        UpdateState.Off => "\uE8D8",
        _ => "\uE895",
    };

    partial void OnLastInstallChanged(InstallResult? value) => OnPropertyChanged(nameof(LastInstallSummary));

    partial void OnStateChanged(UpdateState value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(Glyph));
        ReadyChanged?.Invoke(this, value == UpdateState.Ready);
    }

    public event EventHandler<bool>? ReadyChanged;

    public void Start(bool justUpdated)
    {
        LoadLastInstall(justUpdated);
        if (justUpdated)
        {
            // Give the old process time to release files, then remove the previous installation and old packages.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token).ConfigureAwait(false);
                CleanUpAfterUpdate();
            });
        }
        else
        {
            _client.CleanUp(CurrentVersion);
        }
        _staged = _client.FindStaged(CurrentVersion);
        if (_staged is not null) SetReady(_staged, notify: false);
        else if (!_settings.Current.CheckForUpdates) Set(UpdateState.Off, "Automatic update checks are off.", "");
        else Set(UpdateState.Idle, $"You have Sentinel {CurrentVersion}.", LastChecked is { } t ? $"Last checked for updates {Ago(t)}." : "Sentinel will check for updates shortly.");
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            var due = LastChecked is { } last && DateTimeOffset.Now - last < CheckInterval ? last + CheckInterval : DateTimeOffset.Now + FirstCheckDelay;
            while (!ct.IsCancellationRequested)
            {
                var wait = due - DateTimeOffset.Now;
                if (wait > TimeSpan.Zero)
                {
                    // Wakes early on settings changes so turning checks on/off is reflected immediately.
                    await _wake.WaitAsync(wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait, ct).ConfigureAwait(false);
                    if (!_settings.Current.CheckForUpdates && State is UpdateState.Idle or UpdateState.UpToDate)
                        Post(() => Set(UpdateState.Off, "Automatic update checks are off.", ""));
                    else if (_settings.Current.CheckForUpdates && State == UpdateState.Off)
                        Post(() => Set(UpdateState.Idle, "Sentinel will check for updates shortly.", ""));
                    if (DateTimeOffset.Now < due) continue;
                }
                if (_settings.Current.CheckForUpdates && State != UpdateState.Ready) await CheckAsync(manual: false, ct).ConfigureAwait(false);
                due = DateTimeOffset.Now + CheckInterval;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Checks now; downloads automatically if allowed. Safe to call from the UI.</summary>
    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            Post(() => Set(UpdateState.Checking, "Checking for updates…", ""));
            var latest = await _client.GetLatestAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.Now;
            _settings.Update(s => s.LastUpdateCheck = now);
            Post(() => LastChecked = now);
            _latest = latest;
            if (latest is null || latest.Version <= CurrentVersion)
            {
                Post(() => Set(UpdateState.UpToDate, $"Sentinel {CurrentVersion} is up to date.", $"Checked {Ago(now)}."));
                return;
            }
            Post(() =>
            {
                NewVersion = latest.Version.ToString();
                Notes = latest.Notes;
            });
            if (latest.PackageFor(UpdateEnvironment.Runtime) is null)
            {
                Post(() => Set(UpdateState.Available, $"Sentinel {latest.Version} is available.", $"No signed package for {UpdateEnvironment.Runtime} yet. Download it from GitHub."));
                return;
            }
            if (CanInstall && (manual || _settings.Current.InstallUpdatesAutomatically))
            {
                await DownloadCoreAsync(latest, ct).ConfigureAwait(false);
                return;
            }
            Post(() => Set(UpdateState.Available, $"Sentinel {latest.Version} is available.",
                CanInstall ? "Select Download to get it. It will be verified and installed when Sentinel restarts." : InstallBlockedReason));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            if (ct.IsCancellationRequested) return;
            _log.LogInformation(ex, "Update check failed");
            var why = ex switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "GitHub's rate limit was reached. Sentinel will try again later.",
                HttpRequestException or TaskCanceledException => "Couldn't reach GitHub. Check your internet connection.",
                InvalidDataException => ex.Message,
                _ => "Something went wrong: " + ex.Message,
            };
            Post(() => Set(UpdateState.Failed, "Couldn't check for updates.", why));
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>User-initiated download of an available update.</summary>
    public async Task DownloadAsync()
    {
        if (_latest is null || !CanInstall) return;
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            await DownloadCoreAsync(_latest, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Post(() => Set(UpdateState.Failed, "The update couldn't be downloaded.", ex is InvalidDataException ? ex.Message : "Couldn't reach GitHub. Check your internet connection."));
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task DownloadCoreAsync(ReleaseInfo release, CancellationToken ct)
    {
        Post(() =>
        {
            Progress = 0;
            Set(UpdateState.Downloading, $"Downloading Sentinel {release.Version}…", "The package's signature is verified before anything is installed.");
        });
        var progress = new Progress<double>(p => Post(() =>
        {
            Progress = p * 100;
            Detail = $"{p * 100:F0}% · the package's signature is verified before anything is installed.";
        }));
        try
        {
            var staged = await _client.DownloadAndStageAsync(release, UpdateEnvironment.Runtime, progress, ct).ConfigureAwait(false);
            _staged = staged;
            Post(() => SetReady(staged, notify: true));
        }
        catch (InvalidDataException ex)
        {
            Post(() => Set(UpdateState.Failed, "The update was rejected.", ex.Message));
        }
    }

    private void SetReady(StagedUpdate staged, bool notify)
    {
        NewVersion = staged.Version.ToString();
        Notes = staged.Notes;
        Set(UpdateState.Ready, $"Sentinel {staged.Version} is ready to install.", "It installs the next time Sentinel starts, or restart now. Your history and settings are kept.");
        if (notify)
        {
            _notifications.Show(new SentinelNotification($"update-{staged.Version}", $"Sentinel {staged.Version} is ready",
                "Restart Sentinel to finish updating. Your history and settings are kept.", Severity.Info, "Settings"));
        }
    }

    /// <summary>Starts the staged installer and exits this instance. Returns false if nothing is staged.</summary>
    public bool RestartToUpdate()
    {
        var staged = _staged ?? _client.FindStaged(CurrentVersion);
        if (staged is null || !CanInstall) return false;
        _log.LogInformation("Restarting to install update {Version}", staged.Version);
        if (!UpdateInstaller.HandOff(staged, background: false)) return false;
        _ = App.Current!.ExitAsync();
        return true;
    }

    /// <summary>Used at startup: if a verified update is staged, hand off to its installer instead of starting.</summary>
    public static bool TryInstallStagedAtStartup(bool background)
    {
        try
        {
            var settings = new JsonSettingsStore(new SentinelPaths());
            if (!settings.Current.InstallUpdatesAutomatically || !UpdateEnvironment.CanSelfUpdate(out _)) return false;
            var client = new UpdateClient(new HttpClient(), UpdateEnvironment.UpdatesRoot, UpdateSignature.OfficialPublicKeyPem,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateClient>.Instance);
            var staged = client.FindStaged(UpdateEnvironment.CurrentVersion);
            return staged is not null && UpdateInstaller.HandOff(staged, background);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private void LoadLastInstall(bool justUpdated)
    {
        try
        {
            if (!File.Exists(UpdateEnvironment.ResultFile)) return;
            var r = JsonSerializer.Deserialize<InstallResult>(File.ReadAllText(UpdateEnvironment.ResultFile));
            if (r is not null && DateTimeOffset.Now - r.Time < TimeSpan.FromDays(7)) LastInstall = r;
            if (r is { Succeeded: true } && justUpdated)
            {
                _notifications.Show(new SentinelNotification($"updated-{r.To}", $"Sentinel updated to {r.To}", "See what's new in Settings › Updates.", Severity.Info, "Settings"));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void CleanUpAfterUpdate()
    {
        try
        {
            var previous = UpdateEnvironment.InstallDirectory + ".previous";
            if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation(ex, "Previous installation could not be removed yet");
        }
        try
        {
            _client.CleanUp(CurrentVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation(ex, "Old update packages could not be removed yet");
        }
    }

    private void Set(UpdateState state, string status, string detail)
    {
        State = state;
        Status = status;
        Detail = detail;
    }

    private void Post(Action a)
    {
        if (_ui.HasThreadAccess) a();
        else _ui.TryEnqueue(() => a());
    }

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        return t.LocalDateTime.ToString("d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _wake.Dispose();
        _busy.Dispose();
    }
}
