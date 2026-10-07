using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sentinel.Platform.Windows.Services;

public sealed record ToolResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Runs a small allow-list of Windows' own diagnostic tools with fixed argument lists. No shell is involved
/// (UseShellExecute = false, arguments passed as a list), the executable is resolved from System32 only,
/// and output paths must live inside Sentinel's own temp folder. Only ever invoked by an explicit user action.
/// </summary>
public sealed class TrustedToolRunner(ILogger<TrustedToolRunner> log)
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "powercfg.exe" };

    public async Task<ToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (!Allowed.Contains(tool)) throw new InvalidOperationException($"'{tool}' is not an allowed diagnostic tool.");
        var path = Path.Combine(Environment.SystemDirectory, tool);
        if (!File.Exists(path)) throw new FileNotFoundException("Windows tool not found", path);

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        log.LogInformation("Running user-requested Windows tool {Tool} {Args}", tool, string.Join(' ', arguments.Select(a => a.StartsWith('/') ? a : "<arg>")));
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            return new ToolResult(-1, "", "", TimedOut: true);
        }
        return new ToolResult(process.ExitCode, Truncate(await stdout.ConfigureAwait(false)), Truncate(await stderr.ConfigureAwait(false)), false);
    }

    private static string Truncate(string s) => s.Length > 64_000 ? s[..64_000] : s;

    /// <summary>Validates that a path is inside the given root (no traversal) and returns its full form.</summary>
    public static string ConfinePath(string root, string fileName)
    {
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid file name", nameof(fileName));
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path escapes the Sentinel folder", nameof(fileName));
        return full;
    }
}
