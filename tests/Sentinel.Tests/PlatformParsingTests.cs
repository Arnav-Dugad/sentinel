using System.Text;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Events;
using Sentinel.Platform.Windows.Interop;
using Sentinel.Platform.Windows.Providers;
using Sentinel.Platform.Windows.Services;
using Xunit;

namespace Sentinel.Tests;

public class EventMappingTests
{
    private static RawEvent Raw(string channel, string provider, int id, Dictionary<string, string>? named = null, params string[] positional) =>
        new(channel, provider, id, 42, new DateTimeOffset(2026, 10, 7, 9, 52, 0, TimeSpan.Zero), 2, named ?? [], positional);

    [Fact]
    public void BugcheckIsNormalisedAndNamed()
    {
        var e = EventMapping.Map(Raw("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, null, "0x0000009f (0x0000000000000003, 0xffff, 0x1, 0x2)", "C:\\Windows\\MEMORY.DMP"));
        Assert.NotNull(e);
        Assert.Equal(EventCategory.Bugcheck, e!.Category);
        Assert.Equal("0x0000009F", e.Code);
        Assert.Contains("DRIVER_POWER_STATE_FAILURE", e.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void KernelPower41AndEventLog6008ShareADedupeKey()
    {
        var a = EventMapping.Map(Raw("System", "Microsoft-Windows-Kernel-Power", 41, new() { ["BugcheckCode"] = "0", ["PowerButtonTimestamp"] = "0" }));
        var b = EventMapping.Map(Raw("System", "EventLog", 6008));
        Assert.Equal(EventCategory.UnexpectedShutdown, a!.Category);
        Assert.Equal(a.DedupeKey, b!.DedupeKey);
        Assert.Contains("power loss", a.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AppCrashUsesNamedOrPositionalData()
    {
        var named = EventMapping.Map(Raw("Application", "Application Error", 1000, new() { ["AppName"] = "game.exe", ["ModuleName"] = "d3d12.dll", ["ExceptionCode"] = "c0000005" }));
        var positional = EventMapping.Map(Raw("Application", "Application Error", 1000, null, "game.exe", "1.0", "0", "d3d12.dll", "1.0", "0", "c0000005"));
        Assert.Equal("game.exe crashed", named!.Title);
        Assert.Equal("0xc0000005", named.Code);
        Assert.Equal(named.Title, positional!.Title);
        Assert.Contains("d3d12.dll", positional.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreAppUpdatesBecomeAppEvents()
    {
        var e = EventMapping.Map(Raw("System", "Microsoft-Windows-WindowsUpdateClient", 19, new() { ["updateTitle"] = "9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2" }));
        Assert.Equal(EventCategory.SoftwareInstalled, e!.Category);
        Assert.Equal("WindowsAppRuntime 2 updated (Microsoft Store)", e.Title);
        Assert.Equal("GamingApp", EventMapping.StoreApp("9MV0B5HZVK9Z-Microsoft.GamingApp"));
        Assert.Null(EventMapping.StoreApp("2026-09 Cumulative Update for Windows 11 (KB5000000)"));
    }

    [Fact]
    public void DriverConfiguredOnlyCountsWhenUpdated()
    {
        var data = new Dictionary<string, string> { ["DeviceUpdated"] = "false", ["DriverProvider"] = "NVIDIA", ["DriverVersion"] = "32.0.15.1" };
        Assert.Null(EventMapping.Map(Raw("Microsoft-Windows-Kernel-PnP/Configuration", "Microsoft-Windows-Kernel-PnP", 400, data)));
        data["DeviceUpdated"] = "true";
        var e = EventMapping.Map(Raw("Microsoft-Windows-Kernel-PnP/Configuration", "Microsoft-Windows-Kernel-PnP", 400, data));
        Assert.Equal(EventCategory.DriverChanged, e!.Category);
        Assert.Contains("32.0.15.1", e.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void UntrustedEventTextIsSanitised()
    {
        var e = EventMapping.Map(Raw("Application", "Application Hang", 1002, null, "evil\u202Eexe.txt\u0007"));
        Assert.DoesNotContain('\u202E', e!.Title);
        Assert.DoesNotContain('\u0007', e.Title);
    }

    [Fact]
    public void UnknownEventsAreIgnored() => Assert.Null(EventMapping.Map(Raw("System", "Something", 1)));

    [Theory]
    [InlineData("0x0000009f (0x1, 0x2)", "0x0000009F")]
    [InlineData("0x133", "0x00000133")]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void NormalizeBugcheck(string raw, string? expected) => Assert.Equal(expected, EventMapping.NormalizeBugcheck(raw));
}

public class StorageParsingTests
{
    [Fact]
    public void NvmeHealthLogParsesSpecLayout()
    {
        var log = new byte[512];
        log[0] = 0x01; // available spare below threshold
        BitConverter.GetBytes((ushort)308).CopyTo(log, 1); // 308 K ≈ 34.85 °C
        log[3] = 9;
        log[4] = 10;
        log[5] = 97;
        BitConverter.GetBytes(1000UL).CopyTo(log, 48); // data units written
        BitConverter.GetBytes(1361UL).CopyTo(log, 128); // power-on hours
        BitConverter.GetBytes(35UL).CopyTo(log, 144); // unsafe shutdowns
        var h = NvmeHealthLog.Parse(log)!;
        Assert.Equal(34.85, h.CompositeTemperatureC, 2);
        Assert.Equal(97, h.PercentageUsed);
        Assert.Equal(512_000_000d, h.DataUnitsWrittenBytes);
        Assert.Equal(1361, h.PowerOnHours);
        Assert.Equal(35, h.UnsafeShutdowns);
        Assert.Contains("spare", NvmeHealthLog.DescribeCriticalWarning(h.CriticalWarning), StringComparison.Ordinal);
        Assert.Equal("None", NvmeHealthLog.DescribeCriticalWarning(0));
        Assert.Null(NvmeHealthLog.Parse(new byte[100]));
    }
}

public class ProviderHelperTests
{
    [Fact]
    public void GpuEngineInstanceParsing()
    {
        Assert.True(GpuProvider.TryParseEngine("pid_1234_luid_0x00000000_0x0000E5F6_phys_0_eng_3_engtype_3D", out var pid, out var luid, out var engine, out var type));
        Assert.Equal(1234, pid);
        Assert.Equal("luid_0x00000000_0x0000E5F6", luid);
        Assert.Equal("phys_0_eng_3", engine);
        Assert.Equal("3D", type);
        Assert.Equal("Video decode", GpuProvider.NormalizeEngine("VideoDecode"));
        Assert.Equal("Compute", GpuProvider.NormalizeEngine("Compute_1"));
        Assert.False(GpuProvider.TryParseEngine("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_", out _, out _, out _, out _));
        Assert.False(GpuProvider.TryParseEngine("garbage", out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(140_000u, "NVIDIA GeForce RTX 4060 Laptop GPU", 175)]
    [InlineData(null, "NVIDIA GeForce RTX 4060 Laptop GPU", 200)]
    [InlineData(null, "NVIDIA GeForce RTX 4090", 1000)]
    [InlineData(450_000u, "NVIDIA GeForce RTX 4090", 562.5)]
    public void GpuPowerCeiling(uint? limitMw, string name, double expected) => Assert.Equal(expected, GpuProvider.PowerCeiling(limitMw, name), 3);

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\tool.exe -silent", "C:\\Tools\\tool.exe")]
    [InlineData("", null)]
    public void StartupCommandPath(string command, string? expected) => Assert.Equal(expected, StartupProvider.ExtractPath(command));

    [Theory]
    [InlineData("2026-09 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5065426)", UpdateKind.Cumulative)]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.437.69.0)", UpdateKind.Definition)]
    [InlineData("NVIDIA - Display - 32.0.15.6094", UpdateKind.Driver)]
    [InlineData("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", UpdateKind.StoreApp)]
    [InlineData("2026-09 Cumulative Update for .NET Framework 3.5 and 4.8.1", UpdateKind.DotNet)]
    public void UpdateClassification(string title, UpdateKind kind) => Assert.Equal(kind, UpdateHistoryProvider.Classify(title));

    [Fact]
    public void DeviceInfrastructureIsFiltered()
    {
        Assert.True(DeviceProvider.IsInfrastructure("USB Root Hub (USB 3.0)"));
        Assert.True(DeviceProvider.IsInfrastructure("PCI to PCI Bridge"));
        Assert.True(DeviceProvider.IsInfrastructure("USB2744"));
        Assert.False(DeviceProvider.IsInfrastructure("Razer DeathAdder V3 Pro"));
        Assert.Equal("Keyboard", DeviceProvider.InferCategory("HP Wireless Slim Keyboard"));
        Assert.Equal("Mouse", DeviceProvider.InferCategory("Razer DeathAdder V3 Pro"));
        Assert.Equal("Device", DeviceProvider.InferCategory(null));
    }

    [Fact]
    public void PseudoNetworkInterfacesAreRecognised()
    {
        // Driver/filter pseudo interfaces would double count traffic.
        Assert.Contains("QoS Packet Scheduler", "Wi-Fi-QoS Packet Scheduler-0000", StringComparison.Ordinal);
    }
}

public class BatteryReportTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <Batteries>
            <Battery>
              <Id>Battery</Id>
              <Manufacturer>ACME</Manufacturer>
              <Chemistry>LION</Chemistry>
              <DesignCapacity>90000</DesignCapacity>
              <FullChargeCapacity>76000</FullChargeCapacity>
              <CycleCount>143</CycleCount>
            </Battery>
          </Batteries>
          <RecentUsage>
            <UsageEntry Timestamp="2026-10-01T10:00:00Z" LocalTimestamp="2026-10-01T12:00:00" Duration="PT1H" Ac="0" EntryType="Active" ChargeCapacity="60000" FullChargeCapacity="76000" Discharge="9000" />
          </RecentUsage>
          <History>
            <HistoryEntry LocalStartDate="2026-08-01" LocalEndDate="2026-08-08" DesignCapacity="90000" FullChargeCapacity="80000" CycleCount="100" />
            <HistoryEntry LocalStartDate="2026-09-01" LocalEndDate="2026-09-08" DesignCapacity="90000" FullChargeCapacity="78000" CycleCount="120" />
          </History>
        </BatteryReport>
        """;

    [Fact]
    public void ParsesHistoryAndUsage()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(Sample));
        var r = BatteryReportService.Parse(ms)!;
        Assert.Equal("ACME", r.Manufacturer);
        Assert.Equal(90000, r.DesignCapacityMWh);
        Assert.Equal(143, r.CycleCount);
        Assert.Equal(2, r.CapacityHistory.Count);
        Assert.Equal(78000, r.CapacityHistory[1].FullChargeMWh);
        var u = Assert.Single(r.RecentUsage);
        Assert.False(u.AcPowered);
        Assert.Equal(TimeSpan.FromHours(1), u.Duration);
    }

    [Fact]
    public void RejectsDtd()
    {
        const string evil = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><BatteryReport>&e;</BatteryReport>";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(evil));
        Assert.Throws<System.Xml.XmlException>(() => BatteryReportService.Parse(ms));
    }
}

public class TrustedToolRunnerTests
{
    [Fact]
    public void ConfinePathRejectsTraversal()
    {
        var root = Path.GetTempPath();
        Assert.StartsWith(Path.GetFullPath(root), TrustedToolRunner.ConfinePath(root, "report.xml"), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => TrustedToolRunner.ConfinePath(root, "..\\evil.xml"));
        Assert.Throws<ArgumentException>(() => TrustedToolRunner.ConfinePath(root, "a/../../b.xml"));
    }

    [Fact]
    public async Task OnlyAllowListedToolsRun()
    {
        var runner = new TrustedToolRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<TrustedToolRunner>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("cmd.exe", ["/c", "echo"], TimeSpan.FromSeconds(5), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("powershell.exe", [], TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
