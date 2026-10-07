using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.Core.Settings;

public enum TemperatureUnit { Celsius, Fahrenheit }
public enum ThroughputUnit { BytesPerSecond, BitsPerSecond }
public enum ByteUnitSystem { Binary, Decimal }
public enum ThemePreference { System, Light, Dark }

public sealed class SentinelSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool FirstRunCompleted { get; set; }

    // Appearance & units
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public TemperatureUnit Temperature { get; set; } = TemperatureUnit.Celsius;
    public ThroughputUnit Throughput { get; set; } = ThroughputUnit.BytesPerSecond;
    public ByteUnitSystem Bytes { get; set; } = ByteUnitSystem.Binary;
    public bool ReduceMotion { get; set; }

    // Behaviour
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool NotificationsEnabled { get; set; } = true;

    // History
    public int RetentionDays { get; set; } = 90; // 0 = unlimited
    public bool RecordingPaused { get; set; }
    public bool PrivacyMode { get; set; }
    public bool RecordAppHistory { get; set; } = true;

    // Providers the user switched off (by provider id).
    public List<string> DisabledProviders { get; set; } = [];

    // Local AI
    public bool OllamaEnabled { get; set; }
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string? OllamaModel { get; set; }

    // Updates (GitHub Releases; packages are signature-verified before installation)
    public bool CheckForUpdates { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }

    // Developer
    public bool DeveloperSimulation { get; set; }
    public string SimulationScenario { get; set; } = "Idle";

    public SentinelSettings Clone() => JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJsonContext.Default.SentinelSettings), SettingsJsonContext.Default.SentinelSettings)!;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SentinelSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public interface ISettingsStore
{
    SentinelSettings Current { get; }
    event EventHandler<SentinelSettings>? Changed;
    void Update(Action<SentinelSettings> mutate);
}

/// <summary>JSON settings file in the Sentinel data folder. Writes are atomic (temp file + replace).</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private SentinelSettings _current;

    public JsonSettingsStore(SentinelPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.SettingsFile;
        _current = Load(_path);
    }

    public SentinelSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<SentinelSettings>? Changed;

    public void Update(Action<SentinelSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        SentinelSettings snapshot;
        lock (_gate)
        {
            var next = _current.Clone();
            mutate(next);
            Sanitize(next);
            _current = next;
            snapshot = next;
            Save(_path, next);
        }
        Changed?.Invoke(this, snapshot);
    }

    private static SentinelSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.SentinelSettings);
                if (s is not null)
                {
                    Sanitize(s);
                    return s;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file must never prevent startup; fall back to defaults.
        }
        return new SentinelSettings();
    }

    private static void Sanitize(SentinelSettings s)
    {
        s.RetentionDays = s.RetentionDays is 0 or 7 or 30 or 90 or 365 ? s.RetentionDays : 90;
        s.OllamaEndpoint = string.IsNullOrWhiteSpace(s.OllamaEndpoint) ? "http://localhost:11434" : s.OllamaEndpoint.Trim();
        s.DisabledProviders ??= [];
        s.SimulationScenario ??= "Idle";
    }

    private static void Save(string path, SentinelSettings s)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, SettingsJsonContext.Default.SentinelSettings));
        File.Move(tmp, path, overwrite: true);
    }
}
