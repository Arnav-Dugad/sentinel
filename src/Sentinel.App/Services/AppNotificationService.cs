using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Sentinel.Intelligence;

namespace Sentinel.App.Services;

/// <summary>Shows the rare notifications the policy allows, as standard Windows notifications. Clicking one opens the relevant page.</summary>
public sealed class AppNotificationService(ILogger<AppNotificationService> log) : INotificationSink, IDisposable
{
    private bool _registered;

    public event EventHandler<string>? NavigateRequested;

    /// <summary>Used when Windows App SDK notifications are unavailable (e.g. some unpackaged deployments).</summary>
    public Action<SentinelNotification>? Fallback { get; set; }

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                if (args.Arguments.TryGetValue("navigate", out var page)) NavigateRequested?.Invoke(this, page);
            };
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            log.LogInformation(ex, "Windows App SDK notifications are unavailable; using notification-area balloons instead");
        }
    }

    public void Show(SentinelNotification n)
    {
        if (!_registered)
        {
            Fallback?.Invoke(n);
            return;
        }
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(n.Title)
                .AddText(n.Body)
                .AddArgument("navigate", n.NavigateTo ?? "Home");
            AppNotificationManager.Default.Show(builder.BuildNotification());
            log.LogInformation("Notification shown: {Key}", n.Key);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            log.LogWarning(ex, "Failed to show a notification");
        }
    }

    public void Dispose()
    {
        if (!_registered) return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            log.LogDebug(ex, "Notification unregister failed");
        }
    }
}
