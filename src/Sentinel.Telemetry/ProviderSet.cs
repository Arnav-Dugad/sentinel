using Sentinel.Core.Providers;

namespace Sentinel.Telemetry;

/// <summary>Typed access to every provider in the current composition (real or developer simulation).</summary>
public sealed class ProviderSet(
    ISystemInventoryProvider system,
    ICpuTelemetryProvider cpu,
    IMemoryTelemetryProvider memory,
    IGpuTelemetryProvider gpu,
    IStorageTelemetryProvider storage,
    IBatteryTelemetryProvider battery,
    INetworkTelemetryProvider network,
    IThermalTelemetryProvider thermal,
    IPowerTelemetryProvider power,
    IProcessTelemetryProvider processes,
    IWindowsEventProvider events,
    IDisplayTelemetryProvider display,
    IAudioTelemetryProvider audio,
    IDeviceInventoryProvider devices,
    IDriverInventoryProvider drivers,
    ISoftwareInventoryProvider software,
    IStartupProvider startup,
    IUpdateHistoryProvider updates,
    IOemTelemetryProvider oem)
{
    public ISystemInventoryProvider System { get; } = system;
    public ICpuTelemetryProvider Cpu { get; } = cpu;
    public IMemoryTelemetryProvider Memory { get; } = memory;
    public IGpuTelemetryProvider Gpu { get; } = gpu;
    public IStorageTelemetryProvider Storage { get; } = storage;
    public IBatteryTelemetryProvider Battery { get; } = battery;
    public INetworkTelemetryProvider Network { get; } = network;
    public IThermalTelemetryProvider Thermal { get; } = thermal;
    public IPowerTelemetryProvider Power { get; } = power;
    public IProcessTelemetryProvider Processes { get; } = processes;
    public IWindowsEventProvider Events { get; } = events;
    public IDisplayTelemetryProvider Display { get; } = display;
    public IAudioTelemetryProvider Audio { get; } = audio;
    public IDeviceInventoryProvider Devices { get; } = devices;
    public IDriverInventoryProvider Drivers { get; } = drivers;
    public ISoftwareInventoryProvider Software { get; } = software;
    public IStartupProvider Startup { get; } = startup;
    public IUpdateHistoryProvider Updates { get; } = updates;
    public IOemTelemetryProvider Oem { get; } = oem;

    public bool HasBattery => Battery.Latest.Present;
}
