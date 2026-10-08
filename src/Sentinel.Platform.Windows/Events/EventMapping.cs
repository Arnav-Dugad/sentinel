using System.Globalization;
using Sentinel.Core.Privacy;
using Sentinel.Core.Knowledge;
using Sentinel.Core.Units;
using Sentinel.Domain;

namespace Sentinel.Platform.Windows.Events;

/// <summary>A raw event record reduced to the fields Sentinel needs. Data values are untrusted.</summary>
public sealed record RawEvent(string Channel, string Provider, int Id, long RecordId, DateTimeOffset Time, byte Level,
    IReadOnlyDictionary<string, string> Named, IReadOnlyList<string> Positional)
{
    public string? Get(string name, int index = -1)
    {
        if (Named.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return index >= 0 && index < Positional.Count && !string.IsNullOrWhiteSpace(Positional[index]) ? Positional[index] : null;
    }
}

public sealed record EventSpec(string Channel, string Provider, int[] Ids);

/// <summary>
/// Pure mapping from Windows event records to Sentinel events. Kept free of I/O so it can be unit-tested
/// against recorded event data.
/// </summary>
public static class EventMapping
{
    public const string SystemLog = "System";
    public const string ApplicationLog = "Application";
    public const string WlanLog = "Microsoft-Windows-WLAN-AutoConfig/Operational";
    public const string PnpLog = "Microsoft-Windows-Kernel-PnP/Configuration";
    public const string DiagPerfLog = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public static readonly IReadOnlyList<EventSpec> Specs =
    [
        new(SystemLog, "Microsoft-Windows-Kernel-Power", [41, 42, 105, 506, 507]),
        new(SystemLog, "EventLog", [6008]),
        new(SystemLog, "Microsoft-Windows-WER-SystemErrorReporting", [1001]),
        new(SystemLog, "Microsoft-Windows-Kernel-General", [12, 13]),
        new(SystemLog, "User32", [1074]),
        new(SystemLog, "Microsoft-Windows-Power-Troubleshooter", [1]),
        new(SystemLog, "Display", [4101]),
        new(SystemLog, "Microsoft-Windows-WHEA-Logger", [1, 17, 18, 19, 47]),
        new(SystemLog, "disk", [7, 11, 51, 153]),
        new(SystemLog, "Microsoft-Windows-Ntfs", [55]),
        new(SystemLog, "Service Control Manager", [7000, 7009, 7023, 7031, 7034]),
        new(SystemLog, "Microsoft-Windows-WindowsUpdateClient", [19, 20]),
        new(ApplicationLog, "Application Error", [1000]),
        new(ApplicationLog, "Application Hang", [1002]),
        new(ApplicationLog, "MsiInstaller", [11707, 11724]),
        new(WlanLog, "Microsoft-Windows-WLAN-AutoConfig", [8001, 8003]),
        new(PnpLog, "Microsoft-Windows-Kernel-PnP", [400]),
        new(DiagPerfLog, "Microsoft-Windows-Diagnostics-Performance", [100]),
    ];

    public static SystemEvent? Map(RawEvent e)
    {
        var dedupe = $"{e.Channel}#{e.RecordId}";
        string Src() => $"Windows event log ({e.Provider} {e.Id})";
        SystemEvent Make(EventCategory cat, Severity sev, string title, string? detail = null, string? subject = null, string? code = null, string? key = null) =>
            new(e.Time, cat, sev, Clean(title, 200), detail is null ? null : Redactor.Redact(Clean(detail, 600)), Src(), subject is null ? null : Clean(subject, 96),
                key ?? dedupe, 0, code);

        switch (e.Provider, e.Id)
        {
            case ("Microsoft-Windows-Kernel-Power", 41):
            {
                var bc = ParseLong(e.Get("BugcheckCode"));
                var powerButton = ParseLong(e.Get("PowerButtonTimestamp")) is > 0;
                var detail = "Windows restarted without shutting down cleanly first.";
                if (bc is > 0) detail += $" A stop error (bugcheck 0x{bc:X8}) was recorded for this restart.";
                if (powerButton) detail += " The power button was held down.";
                if (bc is 0 or null && !powerButton) detail += " This is typical of a power loss, a forced power-off or a system hang.";
                return Make(EventCategory.UnexpectedShutdown, Severity.Warning, "Unexpected shutdown", detail, null, "41", UnexpectedKey(e.Time));
            }
            case ("EventLog", 6008):
                return Make(EventCategory.UnexpectedShutdown, Severity.Warning, "Unexpected shutdown", "The previous system shutdown was unexpected.", null, "6008", UnexpectedKey(e.Time));
            case ("Microsoft-Windows-WER-SystemErrorReporting", 1001):
            {
                var raw = e.Get("param1", 0) ?? "";
                var code = NormalizeBugcheck(raw);
                var name = code is null ? null : BugcheckCatalog.Name(code);
                return Make(EventCategory.Bugcheck, Severity.Critical,
                    code is null ? "Windows stopped unexpectedly (stop error)" : $"Windows stopped unexpectedly ({name ?? "stop code"} {code})",
                    $"Bugcheck parameters: {raw}", null, code);
            }
            case ("Microsoft-Windows-Kernel-General", 12):
                return Make(EventCategory.Boot, Severity.Info, "Windows started");
            case ("Microsoft-Windows-Kernel-General", 13):
                return Make(EventCategory.Shutdown, Severity.Info, "Windows shut down");
            case ("User32", 1074):
            {
                var process = e.Get("param1", 0);
                var exe = process is null ? null : Path.GetFileName(process.Split(' ')[0]);
                var type = e.Get("param5", 4) ?? "shutdown";
                var reason = e.Get("param3", 2);
                return Make(EventCategory.Shutdown, Severity.Info, $"{Capitalize(type)} requested by {exe ?? "a process"}", reason, exe);
            }
            case ("Microsoft-Windows-Kernel-Power", 42):
            {
                var target = ParseLong(e.Get("TargetState"));
                return Make(EventCategory.Sleep, Severity.Info, target == 5 ? "Entered hibernation" : "Entered sleep");
            }
            case ("Microsoft-Windows-Power-Troubleshooter", 1):
            {
                var source = e.Get("WakeSourceText");
                var sleepTime = e.Get("SleepTime");
                var detail = source is null ? null : $"Wake source: {source}";
                if (sleepTime is not null && DateTimeOffset.TryParse(sleepTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var slept))
                    detail = $"Slept since {UnitFormatter.Absolute(slept)}. " + detail;
                return Make(EventCategory.Wake, Severity.Info, "Woke from sleep", detail, source);
            }
            case ("Microsoft-Windows-Kernel-Power", 506):
                return Make(EventCategory.Sleep, Severity.Info, "Entered Modern Standby");
            case ("Microsoft-Windows-Kernel-Power", 507):
                return Make(EventCategory.Wake, Severity.Info, "Exited Modern Standby");
            case ("Microsoft-Windows-Kernel-Power", 105):
            {
                var ac = e.Get("AcOnline");
                if (ac is null) return null;
                var online = ac.Equals("true", StringComparison.OrdinalIgnoreCase);
                return Make(EventCategory.PowerSource, Severity.Info, online ? "Plugged in" : "Running on battery", null, null, null, $"acdc-{e.Time.ToUnixTimeSeconds() / 60}");
            }
            case ("Display", 4101):
            {
                var driver = e.Get("param1", 0) ?? "display driver";
                return Make(EventCategory.DisplayDriverReset, Severity.Warning, "Display driver stopped responding and recovered",
                    $"Driver: {driver}. Windows reset the graphics driver after a timeout (TDR).", FriendlyDisplayDriver(driver), "4101");
            }
            case ("Microsoft-Windows-WHEA-Logger", var id):
            {
                var fatal = id is 1 or 18;
                return Make(EventCategory.HardwareError, fatal ? Severity.Critical : Severity.Notice,
                    fatal ? "Fatal hardware error reported" : "Corrected hardware error reported",
                    "Windows Hardware Error Architecture recorded an error" + (e.Get("ErrorSource") is { } es ? $" (source {es})." : ".") +
                    (fatal ? "" : " Corrected errors were handled automatically; occasional ones are not unusual."), null, id.ToString(CultureInfo.InvariantCulture));
            }
            case ("disk", var id):
            {
                var dev = e.Get("param1", 0);
                var what = id switch
                {
                    7 => "A bad block was reported",
                    11 => "A disk controller error was reported",
                    51 => "A paging I/O error was reported",
                    _ => "An I/O operation was retried",
                };
                return Make(EventCategory.StorageError, id == 153 ? Severity.Notice : Severity.Warning, $"Storage: {what}", dev is null ? null : $"Device: {dev}", dev,
                    id.ToString(CultureInfo.InvariantCulture));
            }
            case ("Microsoft-Windows-Ntfs", 55):
                return Make(EventCategory.StorageError, Severity.Warning, "File system corruption detected",
                    "NTFS detected a corruption on a volume. Windows can usually repair this with a disk check.", e.Get("DriveName"), "55");
            case ("Service Control Manager", var id):
            {
                var svc = e.Get("param1", 0) ?? "A service";
                var what = id switch
                {
                    7000 => "failed to start",
                    7009 => "timed out while starting",
                    7023 => "stopped with an error",
                    7031 or 7034 => "terminated unexpectedly",
                    _ => "failed",
                };
                return Make(EventCategory.ServiceFailure, Severity.Notice, $"{svc} {what}", null, svc, id.ToString(CultureInfo.InvariantCulture));
            }
            case ("Microsoft-Windows-WindowsUpdateClient", 19):
            {
                var title = e.Get("updateTitle") ?? "Update installed";
                // Windows logs one record per package architecture; collapse identical installs within the same minute.
                return StoreApp(title) is { } app
                    ? Make(EventCategory.SoftwareInstalled, Severity.Info, $"{app} updated (Microsoft Store)", null, app, null, $"store-{title}-{e.Time.ToUnixTimeSeconds() / 60}")
                    : Make(EventCategory.UpdateInstalled, Severity.Info, title, null, "Windows Update", null, $"wu-{title}-{e.Time.ToUnixTimeSeconds() / 60}");
            }
            case ("Microsoft-Windows-WindowsUpdateClient", 20):
            {
                var title = e.Get("updateTitle") ?? "unknown update";
                return Make(EventCategory.UpdateFailed, Severity.Notice, "Update failed: " + (StoreApp(title) is { } app ? $"{app} (Microsoft Store)" : title),
                    e.Get("errorCode") is { } err ? $"Error {err}" : null, "Windows Update", e.Get("errorCode"));
            }
            case ("Application Error", 1000):
            {
                var app = e.Get("AppName", 0) ?? "An application";
                var module = e.Get("ModuleName", 3);
                var code = e.Get("ExceptionCode", 6);
                var detail = $"Faulting module: {module ?? "unknown"}" + (code is null ? "" : $", exception code 0x{TrimHex(code)}") + ".";
                return Make(EventCategory.AppCrash, Severity.Notice, $"{app} crashed", detail, app, code is null ? null : "0x" + TrimHex(code));
            }
            case ("Application Hang", 1002):
            {
                var app = e.Get("AppName", 0) ?? "An application";
                return Make(EventCategory.AppHang, Severity.Notice, $"{app} stopped responding", null, app);
            }
            case ("MsiInstaller", 11707 or 11724):
            {
                var text = e.Get("param1", 0) ?? "";
                var product = ParseMsiProduct(text) ?? "An application";
                return e.Id == 11707
                    ? Make(EventCategory.SoftwareInstalled, Severity.Info, $"{product} installed", null, product)
                    : Make(EventCategory.SoftwareRemoved, Severity.Info, $"{product} removed", null, product);
            }
            case ("Microsoft-Windows-WLAN-AutoConfig", 8001):
                return Make(EventCategory.NetworkConnected, Severity.Info, "Wi-Fi connected", e.Get("PHYType") is { } phy ? $"PHY: {phy}" : null, "Wi-Fi");
            case ("Microsoft-Windows-WLAN-AutoConfig", 8003):
            {
                var reason = e.Get("Reason");
                return Make(EventCategory.NetworkDisconnected, Severity.Notice, "Wi-Fi disconnected", reason is null ? null : $"Reason: {reason}", "Wi-Fi");
            }
            case ("Microsoft-Windows-Kernel-PnP", 400):
            {
                var updated = e.Get("DeviceUpdated");
                if (!string.Equals(updated, "true", StringComparison.OrdinalIgnoreCase)) return null;
                var provider = e.Get("DriverProvider") ?? "Unknown provider";
                var version = e.Get("DriverVersion") ?? "unknown version";
                var instance = e.Get("DeviceInstanceId");
                return Make(EventCategory.DriverChanged, Severity.Info, $"{provider} driver {version} installed",
                    $"Driver package {e.Get("DriverName") ?? "unknown"}, dated {e.Get("DriverDate") ?? "unknown"}.", provider, instance);
            }
            case ("Microsoft-Windows-Diagnostics-Performance", 100):
            {
                var ms = ParseLong(e.Get("BootTime"));
                if (ms is not > 0) return null;
                return Make(EventCategory.Boot, Severity.Info, $"Boot measured: {ms / 1000.0:F1} s",
                    $"Main path {ParseLong(e.Get("MainPathBootTime")) / 1000.0:F1} s, post-boot {ParseLong(e.Get("BootPostBootTime")) / 1000.0:F1} s.", null,
                    "boottime:" + ms.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        return null;
    }

    /// <summary>Microsoft Store app updates are titled "&lt;12-char product id&gt;-&lt;package name&gt;".</summary>
    public static string? StoreApp(string title)
    {
        var t = title.Trim();
        if (t.Length < 14 || t[12] != '-' || !t[..12].All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)) || t.Contains(' ', StringComparison.Ordinal)) return null;
        // "Microsoft.WindowsAppRuntime.2" → "WindowsAppRuntime 2": drop the publisher prefix, keep the rest readable.
        var segments = t[13..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 ? string.Join(' ', segments[1..]) : segments.FirstOrDefault() ?? t[13..];
    }

    private static string UnexpectedKey(DateTimeOffset t) => $"unexpected-{t.ToUnixTimeSeconds() / 600}";

    public static string? NormalizeBugcheck(string raw)
    {
        var token = raw.Trim().Split([' ', '(', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null) return null;
        var hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v > 0 ? $"0x{v:X8}" : null;
    }

    private static string TrimHex(string code) => code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? code[2..] : code;

    private static long? ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
        : s is not null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h
        : null;

    public static string? ParseMsiProduct(string text)
    {
        const string prefix = "Product: ";
        var i = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = text[(i + prefix.Length)..];
        var end = rest.IndexOf(" -- ", StringComparison.Ordinal);
        return (end > 0 ? rest[..end] : rest).Trim();
    }

    public static string FriendlyDisplayDriver(string driver) => driver.ToLowerInvariant() switch
    {
        "nvlddmkm" => "NVIDIA display driver",
        "amdkmdag" or "amdkmdap" or "atikmdag" or "atikmpag" => "AMD display driver",
        "igfx" or "igdkmd64" or "igdkmdn64" or "igdkmdnd64" => "Intel display driver",
        _ => driver,
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Clean(string s, int max) => Redactor.SanitizeUntrusted(s, max);
}
