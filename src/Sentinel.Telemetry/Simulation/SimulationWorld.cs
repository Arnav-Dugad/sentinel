namespace Sentinel.Telemetry.Simulation;

public enum SimulationScenario
{
    Idle,
    Gaming,
    Overheating,
    StorageWarning,
    BatteryDegradation,
    WifiDropout,
    DriverCrash,
    BsodTimeline,
    MemoryPressure,
}

/// <summary>
/// Deterministic, seeded model of a fictional laptop used by the developer simulation providers.
/// All values are synthetic and are only ever shown under a "Developer Simulation" banner.
/// </summary>
public sealed class SimulationWorld(SimulationScenario scenario, TimeProvider time)
{
    private readonly Random _rng = new(20240917);
    private readonly DateTimeOffset _start = time.GetUtcNow();
    private readonly Lock _gate = new();

    public SimulationScenario Scenario { get; } = scenario;

    public static SimulationScenario Parse(string? name) =>
        Enum.TryParse<SimulationScenario>(name, true, out var s) ? s : SimulationScenario.Idle;

    public double Seconds => (time.GetUtcNow() - _start).TotalSeconds;

    public double Noise(double amplitude)
    {
        lock (_gate) return (_rng.NextDouble() * 2 - 1) * amplitude;
    }

    public bool Chance(double p)
    {
        lock (_gate) return _rng.NextDouble() < p;
    }

    private static double Ramp(double t, double seconds) => Math.Clamp(t / seconds, 0, 1);

    public bool IsHeavy => Scenario is SimulationScenario.Gaming or SimulationScenario.DriverCrash;

    public double CpuUtil => Scenario switch
    {
        SimulationScenario.Gaming or SimulationScenario.DriverCrash => 45 + 20 * Math.Sin(Seconds / 9) + Noise(6),
        SimulationScenario.MemoryPressure => 28 + Noise(8),
        _ => 3 + Math.Abs(Noise(2.5)),
    };

    public double GpuUtil => IsHeavy ? 96 + Noise(3) : 2 + Math.Abs(Noise(1.5));

    public double CpuTemp => Scenario switch
    {
        SimulationScenario.Overheating => 72 + Noise(1.5),
        SimulationScenario.Gaming or SimulationScenario.DriverCrash => 58 + 18 * Ramp(Seconds, 240) + Noise(1.5),
        _ => 50 + Noise(1.2),
    };

    public double GpuTemp => IsHeavy ? 45 + 34 * Ramp(Seconds, 300) + Noise(1) : 44 + Noise(0.8);

    public double GpuPower => IsHeavy ? 98 + Noise(6) : 6 + Math.Abs(Noise(1));

    public double MemoryUsedPercent => Scenario switch
    {
        SimulationScenario.MemoryPressure => Math.Min(97, 86 + 9 * Ramp(Seconds, 600) + Noise(0.5)),
        SimulationScenario.Gaming or SimulationScenario.DriverCrash => 74 + Noise(1),
        _ => 46 + Noise(0.6),
    };

    public double BatteryRateWatts => Scenario is SimulationScenario.Gaming or SimulationScenario.DriverCrash ? 0 : -(9.5 + Noise(1.2));

    public bool AcOnline => Scenario is SimulationScenario.Gaming or SimulationScenario.DriverCrash or SimulationScenario.StorageWarning;

    public double WifiSignal => Scenario == SimulationScenario.WifiDropout
        ? Math.Max(5, 70 - 55 * (0.5 + 0.5 * Math.Sin(Seconds / 30)) + Noise(4))
        : 88 + Noise(3);

    public bool WifiConnected => Scenario != SimulationScenario.WifiDropout || WifiSignal > 18;

    public double DiskTemp => Scenario == SimulationScenario.StorageWarning ? 71 + Noise(1) : 39 + Noise(0.8);

    public long DesignCapacity => 90_000;

    public long FullChargeCapacity => Scenario == SimulationScenario.BatteryDegradation ? 61_400 : 84_300;

    private double _charge = 0.82;

    public double ChargeFraction
    {
        get
        {
            lock (_gate)
            {
                var rate = BatteryRateWatts * 1000; // mW
                _charge = Math.Clamp(_charge + rate / 3600.0 / FullChargeCapacity, 0.05, 1.0);
                if (AcOnline) _charge = Math.Min(1.0, _charge + 0.0002);
                return _charge;
            }
        }
    }
}
