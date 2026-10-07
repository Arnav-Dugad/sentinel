namespace Sentinel.Core.Settings;

/// <summary>All on-disk locations. Everything lives under the user's local app data; nothing is written elsewhere.</summary>
public sealed class SentinelPaths
{
    public SentinelPaths(string? root = null, bool simulation = false)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sentinel");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Reports);
        Directory.CreateDirectory(Temp);
        Simulation = simulation;
    }

    public string Root { get; }
    public bool Simulation { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Simulation data is kept in a separate database so it can never be mistaken for real history.</summary>
    public string Database => Path.Combine(Root, Simulation ? "sentinel-simulation.db" : "sentinel.db");

    public string Logs => Path.Combine(Root, "logs");
    public string Reports => Path.Combine(Root, "reports");
    public string Temp => Path.Combine(Root, "temp");
}
