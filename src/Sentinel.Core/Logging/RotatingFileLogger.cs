using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Privacy;

namespace Sentinel.Core.Logging;

/// <summary>
/// Local, size-limited, rotating, privacy-aware log. Messages are redacted before they touch disk.
/// Writes are batched on a background thread so logging never blocks callers.
/// </summary>
public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 5;

    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _writer;
    private readonly LogLevel _minLevel;

    public RotatingFileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Sentinel log writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public string CurrentFile => Path.Combine(_directory, "sentinel.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    internal void Enqueue(string line)
    {
        // Dropping log lines under extreme pressure is preferable to unbounded memory growth.
        _queue.TryAdd(line);
    }

    internal bool IsEnabled(LogLevel level) => level >= _minLevel && level != LogLevel.None;

    private static string ShortCategory(string category)
    {
        var i = category.LastIndexOf('.');
        return i >= 0 ? category[(i + 1)..] : category;
    }

    private void WriteLoop()
    {
        var sb = new StringBuilder();
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            sb.Clear().AppendLine(first);
            while (sb.Length < 64 * 1024 && _queue.TryTake(out var more)) sb.AppendLine(more);
            try
            {
                RotateIfNeeded();
                File.AppendAllText(CurrentFile, sb.ToString());
            }
            catch (IOException)
            {
                // Log failures must never surface to the application.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RotateIfNeeded()
    {
        var fi = new FileInfo(CurrentFile);
        if (!fi.Exists || fi.Length < MaxFileBytes) return;
        for (var i = MaxFiles - 1; i >= 1; i--)
        {
            var src = Path.Combine(_directory, i == 1 ? "sentinel.log" : $"sentinel.{i - 1}.log");
            var dst = Path.Combine(_directory, $"sentinel.{i}.log");
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
    }

    /// <summary>Concatenates the current logs, redacted again, for user export.</summary>
    public string ExportRedacted()
    {
        var sb = new StringBuilder();
        for (var i = MaxFiles - 1; i >= 0; i--)
        {
            var p = Path.Combine(_directory, i == 0 ? "sentinel.log" : $"sentinel.{i}.log");
            if (!File.Exists(p)) continue;
            try
            {
                using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var r = new StreamReader(fs);
                sb.Append(Redactor.Redact(r.ReadToEnd()));
            }
            catch (IOException)
            {
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger(RotatingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += " | " + exception.GetType().Name + ": " + exception.Message;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "CRT",
            };
            owner.Enqueue($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{category}] {Redactor.Redact(msg)}");
        }
    }
}
