using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sentinel.Core.Providers;
using Sentinel.Domain;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Windows Update history through the Windows Update Agent API (IUpdateSearcher.QueryHistory), read-only.
/// Sentinel never searches for, downloads or installs updates.
/// </summary>
public sealed partial class UpdateHistoryProvider(ILogger<UpdateHistoryProvider> log) : WindowsProvider(log), IUpdateHistoryProvider
{
    private const string Source = "Windows Update Agent API (history)";

    public override ProviderDescriptor Descriptor { get; } = Describe("updates", "Windows Update history", "Updates", SamplingCost.Moderate, "Every 6 hours",
        Source, "Registry (reboot-required flag)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromHours(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(6));

    public IReadOnlyList<UpdateRecord> Updates { get; private set; } = [];
    public bool? RebootRequired { get; private set; }

    [GeneratedRegex(@"KB\d{6,8}", RegexOptions.IgnoreCase)]
    private static partial Regex Kb();

    public override Task InitializeAsync(CancellationToken ct) => Task.Run(Read, ct);

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct) => Task.Run(Read, ct);

    private void Read()
    {
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            RebootRequired = k is not null;

        var list = new List<UpdateRecord>();
        object? session = null;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: false);
            if (type is null)
            {
                SetCapability("Update history", false, Source, "Windows Update Agent is not available");
                return;
            }
            session = Activator.CreateInstance(type);
            dynamic s = session!;
            dynamic searcher = s.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();
            if (total > 0)
            {
                dynamic history = searcher.QueryHistory(0, Math.Min(total, 400));
                int count = history.Count;
                for (var i = 0; i < count; i++)
                {
                    dynamic e = history.Item(i);
                    string? title = e.Title;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    DateTime date = e.Date;
                    int result = e.ResultCode;
                    int operation = e.Operation;
                    string? description = e.Description;
                    list.Add(new UpdateRecord(new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToLocalTime(),
                        Core.Privacy.Redactor.SanitizeUntrusted(title, 200), Classify(title),
                        (operation == 2 ? "Uninstall: " : "") + result switch { 2 => "Succeeded", 3 => "Succeeded with errors", 4 => "Failed", 5 => "Cancelled", 1 => "In progress", _ => "Unknown" },
                        Kb().Match(title) is { Success: true } m ? m.Value.ToUpperInvariant() : null,
                        description is null ? null : Core.Privacy.Redactor.SanitizeUntrusted(description, 400)));
                }
            }
            // The history contains one row per package architecture for many updates; keep one per title and minute.
            Updates = list.DistinctBy(u => (u.Title, u.Result, u.Date.ToUnixTimeSeconds() / 60)).OrderByDescending(u => u.Date).ToList();
            SetCapability("Update history", true, Source);
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or UnauthorizedAccessException)
        {
            SetCapability("Update history", false, Source, "The Windows Update Agent did not return history: " + ex.Message);
            Log.LogInformation(ex, "Update history unavailable");
        }
        finally
        {
            if (session is not null && Marshal.IsComObject(session)) Marshal.FinalReleaseComObject(session);
        }
    }

    internal static UpdateKind Classify(string title)
    {
        if (Events.EventMapping.StoreApp(title) is not null) return UpdateKind.StoreApp;
        if (title.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase) || title.Contains("Definition Update", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Antimalware", StringComparison.OrdinalIgnoreCase))
            return UpdateKind.Definition;
        if (title.Contains("Feature update", StringComparison.OrdinalIgnoreCase)) return UpdateKind.Feature;
        if (title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase)) return title.Contains(".NET", StringComparison.OrdinalIgnoreCase) ? UpdateKind.DotNet : UpdateKind.Cumulative;
        if (title.Contains(".NET", StringComparison.OrdinalIgnoreCase)) return UpdateKind.DotNet;
        // Driver updates delivered by Windows Update are titled "Vendor - Class - Version".
        var parts = title.Split(" - ");
        if (parts.Length >= 3 && Version.TryParse(parts[^1].Trim(), out _)) return UpdateKind.Driver;
        return UpdateKind.Other;
    }
}
