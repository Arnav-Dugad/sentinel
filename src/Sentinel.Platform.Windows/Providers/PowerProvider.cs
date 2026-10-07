using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Power state observer: suspend/resume and power-setting notifications (callback-based, no window),
/// user idle time (GetLastInputInfo) and the current Windows power mode. Never changes any power setting.
/// </summary>
public sealed class PowerProvider(ILogger<PowerProvider> log) : WindowsProvider(log), IPowerTelemetryProvider
{
    private const string Source = "Windows power notifications";

    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private Native.DeviceNotifyCallback? _callback;
    private IntPtr _suspendHandle;
    private readonly List<IntPtr> _settingHandles = [];
    private GCHandle _self;

    public override ProviderDescriptor Descriptor { get; } = Describe("power", "Power state", "Power", SamplingCost.Negligible, "Event-driven",
        Source, "GetLastInputInfo", "PowerGetEffectiveOverlaySchemeId");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    public bool AcOnline { get; private set; } = true;
    public bool BatterySaverOn { get; private set; }
    public bool DisplayOn { get; private set; } = true;
    public TimeSpan UserIdle { get; private set; }
    public string? PowerMode { get; private set; }
    public string? PowerPlan { get; private set; }

    public event EventHandler? Suspending;
    public event EventHandler? Resumed;
    public event EventHandler? PowerSourceChanged;

    public override Task InitializeAsync(CancellationToken ct)
    {
        if (Native.GetSystemPowerStatus(out var ps))
        {
            AcOnline = ps.ACLineStatus != 0;
            BatterySaverOn = ps.SystemStatusFlag == 1;
        }
        _callback = OnNotify;
        _self = GCHandle.Alloc(this);
        var p = new Native.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { Callback = _callback, Context = GCHandle.ToIntPtr(_self) };
        var ok = Native.PowerRegisterSuspendResumeNotification(Native.DEVICE_NOTIFY_CALLBACK, ref p, out _suspendHandle) == 0;
        foreach (var g in new[] { Native.GUID_ACDC_POWER_SOURCE, Native.GUID_POWER_SAVING_STATUS, Native.GUID_CONSOLE_DISPLAY_STATE })
        {
            var guid = g;
            if (Native.PowerSettingRegisterNotification(ref guid, Native.DEVICE_NOTIFY_CALLBACK, ref p, out var h) == 0) _settingHandles.Add(h);
        }
        ReadPowerMode();
        SetCapability("Sleep/resume notifications", ok, Source);
        SetCapability("User idle time", true, "GetLastInputInfo");
        return Task.CompletedTask;
    }

    private uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            switch (type)
            {
                case Native.PBT_APMSUSPEND:
                    Suspending?.Invoke(this, EventArgs.Empty);
                    break;
                case Native.PBT_APMRESUMEAUTOMATIC:
                    Resumed?.Invoke(this, EventArgs.Empty);
                    break;
                case Native.PBT_POWERSETTINGCHANGE when setting != IntPtr.Zero:
                    var s = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(setting);
                    if (s.PowerSetting == Native.GUID_ACDC_POWER_SOURCE)
                    {
                        var ac = s.Data == 0; // PoAc = 0
                        if (ac != AcOnline)
                        {
                            AcOnline = ac;
                            PowerSourceChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else if (s.PowerSetting == Native.GUID_POWER_SAVING_STATUS)
                    {
                        BatterySaverOn = s.Data != 0;
                    }
                    else if (s.PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        DisplayOn = s.Data != 0;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the power manager's callback.
            Log.LogWarning(ex, "Power notification handler failed");
        }
        return 0;
    }

    private unsafe void ReadPowerMode()
    {
        // The overlay (power mode) query is not exported by every Windows build, so resolve it dynamically.
        if (NativeLibrary.TryLoad("powrprof.dll", typeof(PowerProvider).Assembly, DllImportSearchPath.System32, out var lib))
        {
            try
            {
                if (NativeLibrary.TryGetExport(lib, "PowerGetEffectiveOverlaySchemeId", out var fn))
                {
                    Guid overlay;
                    if (((delegate* unmanaged[Stdcall]<Guid*, uint>)fn)(&overlay) == 0)
                    {
                        PowerMode = overlay == BestEfficiency ? "Best power efficiency"
                            : overlay == BestPerformance ? "Best performance"
                            : overlay == Guid.Empty ? "Balanced"
                            : "Custom";
                    }
                }
            }
            finally
            {
                NativeLibrary.Free(lib);
            }
        }
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) == 0 && schemePtr != IntPtr.Zero)
        {
            try
            {
                var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
                uint size = 0;
                Native.PowerReadFriendlyName(IntPtr.Zero, &scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
                if (size is > 0 and < 1024)
                {
                    var buf = stackalloc byte[(int)size];
                    if (Native.PowerReadFriendlyName(IntPtr.Zero, &scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0)
                        PowerPlan = Core.Privacy.Redactor.SanitizeUntrusted(new string((char*)buf).Trim(), 64);
                }
            }
            finally
            {
                Native.LocalFree(schemePtr);
            }
        }
    }

    private DateTimeOffset _lastModeRead = DateTimeOffset.MinValue;

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (Native.GetLastInputInfo(ref info)) UserIdle = TimeSpan.FromMilliseconds(unchecked(Native.GetTickCount() - info.dwTime));
        if (ctx.Now - _lastModeRead > TimeSpan.FromSeconds(30))
        {
            ReadPowerMode();
            _lastModeRead = ctx.Now;
        }
        ctx.Metrics.Record(MetricKeys.UserIdleSeconds, UserIdle.TotalSeconds, ctx.Now);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        if (_suspendHandle != IntPtr.Zero) Native.PowerUnregisterSuspendResumeNotification(_suspendHandle);
        foreach (var h in _settingHandles) Native.PowerSettingUnregisterNotification(h);
        _settingHandles.Clear();
        if (_self.IsAllocated) _self.Free();
        base.Dispose();
    }
}
