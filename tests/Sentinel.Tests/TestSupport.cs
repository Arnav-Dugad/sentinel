using Microsoft.Extensions.Logging.Abstractions;
using Sentinel.Data;

namespace Sentinel.Tests;

/// <summary>A history database in a unique temporary file, deleted on dispose.</summary>
public sealed class TempStore : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sentinel-tests", Guid.NewGuid().ToString("N"));

    public TempStore()
    {
        Directory.CreateDirectory(_dir);
        Store = new HistoryStore(Path.Combine(_dir, "test.db"), NullLogger<HistoryStore>.Instance);
        Store.Initialize();
    }

    public HistoryStore Store { get; }

    public string Directory2 => _dir;

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
