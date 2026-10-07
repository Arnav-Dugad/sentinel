using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Process telemetry from one SystemProcessInformation snapshot per tick (CPU time, memory, I/O, threads, handles).
/// Image paths use PROCESS_QUERY_LIMITED_INFORMATION. Command-line arguments are never read.
/// </summary>
public sealed class ProcessProvider(ILogger<ProcessProvider> log, IGpuTelemetryProvider gpu) : WindowsProvider(log), IProcessTelemetryProvider
{
    private const string Source = "NtQuerySystemInformation (SystemProcessInformation)";
    private const int MaxCache = 4096;

    private IntPtr _buffer;
    private int _bufferSize = 1 << 20;
    private readonly Dictionary<(int Pid, long Create), (long Cpu, long Io, long Ticks)> _previous = [];
    private readonly Dictionary<(int Pid, long Create), string?> _paths = [];
    private readonly Dictionary<string, (string? Publisher, string? Description, string? Version)> _fileInfo = new(StringComparer.OrdinalIgnoreCase);

    public override ProviderDescriptor Descriptor { get; } = Describe("processes", "Processes", "Processes", SamplingCost.Moderate, "1–10 s", Source,
        "QueryFullProcessImageName", "File version resources");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    public ProcessSnapshot Latest { get; private set; } = ProcessSnapshot.Empty;

    public override Task InitializeAsync(CancellationToken ct)
    {
        _buffer = Marshal.AllocHGlobal(_bufferSize);
        SetCapability("Per-process CPU, memory and disk", true, Source);
        SetCapability("Per-process GPU", true, "Windows GPU engine counters");
        SetCapability("Per-process network", false, "None", "Per-process network usage requires ETW kernel tracing, which needs elevation. It is not collected.");
        SetCapability("Command-line arguments", false, "None", "Never collected (privacy).");
        return Task.CompletedTask;
    }

    private bool Snapshot()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buffer, (uint)_bufferSize, out var needed);
            if (status == 0) return true;
            if (status != Native.STATUS_INFO_LENGTH_MISMATCH) return false;
            var newSize = Math.Max(_bufferSize * 2, (int)needed + (64 << 10));
            if (newSize > 64 << 20) return false;
            Marshal.FreeHGlobal(_buffer);
            _bufferSize = newSize;
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }
        return false;
    }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Process snapshot layout is implemented for 64-bit processes.");
        if (!Snapshot()) throw new InvalidOperationException("Process snapshot failed");
        var nowTicks = Stopwatch.GetTimestamp();
        var cpuCount = Environment.ProcessorCount;
        var gpuByPid = gpu.ProcessUtilization;
        var samples = new List<ProcessSample>(512);
        var seen = new HashSet<(int, long)>();

        var offset = 0;
        while (true)
        {
            var p = _buffer + offset;
            var next = Marshal.ReadInt32(p);
            var pid = (int)Marshal.ReadInt64(p, 80);
            var parent = (int)Marshal.ReadInt64(p, 88);
            var create = Marshal.ReadInt64(p, 32);
            var user = Marshal.ReadInt64(p, 40);
            var kernel = Marshal.ReadInt64(p, 48);
            var nameLen = (ushort)Marshal.ReadInt16(p, 56);
            var namePtr = Marshal.ReadIntPtr(p, 64);
            var threads = Marshal.ReadInt32(p, 4);
            var handles = Marshal.ReadInt32(p, 96);
            var session = Marshal.ReadInt32(p, 100);
            var workingSet = Marshal.ReadInt64(p, 144);
            var privateBytes = Marshal.ReadInt64(p, 184);
            var io = Marshal.ReadInt64(p, 232) + Marshal.ReadInt64(p, 240);

            if (pid != 0)
            {
                var name = pid == 4 ? "System" : namePtr != IntPtr.Zero && nameLen > 0 ? Marshal.PtrToStringUni(namePtr, nameLen / 2) : $"PID {pid}";
                var key = (pid, create);
                seen.Add(key);
                double cpu = 0, ioRate = 0;
                if (_previous.TryGetValue(key, out var prev))
                {
                    var elapsed = (nowTicks - prev.Ticks) / (double)Stopwatch.Frequency;
                    if (elapsed > 0.05)
                    {
                        cpu = Math.Clamp((user + kernel - prev.Cpu) / 1e7 / elapsed / cpuCount * 100, 0, 100);
                        ioRate = Math.Max(0, (io - prev.Io) / elapsed);
                    }
                }
                _previous[key] = (user + kernel, io, nowTicks);
                samples.Add(new ProcessSample(pid, parent, Core.Privacy.Redactor.SanitizeUntrusted(name, 128), cpu, workingSet, privateBytes, ioRate,
                    gpuByPid.TryGetValue(pid, out var g) ? Math.Clamp(g, 0, 100) : 0, threads, handles,
                    create > 0 ? DateTimeOffset.FromFileTime(create) : DateTimeOffset.MinValue, session, ImagePath(pid, create)));
            }
            if (next == 0 || offset + next >= _bufferSize) break;
            offset += next;
        }

        foreach (var k in _previous.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _previous.Remove(k);
            _paths.Remove(k);
        }
        Latest = new ProcessSnapshot(ctx.Now, samples, GroupApps(samples), samples.Sum(s => s.CpuPercent));
        return Task.CompletedTask;
    }

    private List<AppUsage> GroupApps(List<ProcessSample> samples)
    {
        var totalCpu = Math.Max(samples.Sum(s => s.CpuPercent), 1e-9);
        var totalPrivate = Math.Max(samples.Sum(s => (double)s.PrivateBytes), 1);
        return samples
            .GroupBy(s => s.Name.ToLowerInvariant())
            .Select(g =>
            {
                var path = g.Select(s => s.ImagePath).FirstOrDefault(p => p is not null);
                var info = path is null ? default : FileInfo(path);
                var display = !string.IsNullOrWhiteSpace(info.Description) ? info.Description! : Path.GetFileNameWithoutExtension(g.First().Name);
                var cpu = g.Sum(s => s.CpuPercent);
                var priv = g.Sum(s => s.PrivateBytes);
                return new AppUsage(g.Key, Core.Privacy.Redactor.SanitizeUntrusted(display, 80), info.Publisher, g.Count(), cpu, priv, g.Sum(s => s.WorkingSetBytes),
                    g.Sum(s => s.DiskBytesPerSec), g.Max(s => s.GpuPercent), cpu / totalCpu, priv / totalPrivate);
            })
            .OrderByDescending(a => a.CpuPercent).ThenByDescending(a => a.PrivateBytes)
            .ToList();
    }

    private string? ImagePath(int pid, long create)
    {
        if (_paths.TryGetValue((pid, create), out var cached)) return cached;
        string? path = null;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var buf = new char[1024];
                var size = (uint)buf.Length;
                if (Native.QueryFullProcessImageNameW(h, 0, buf, ref size)) path = new string(buf, 0, (int)size);
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }
        if (_paths.Count < MaxCache) _paths[(pid, create)] = path;
        return path;
    }

    private (string? Publisher, string? Description, string? Version) FileInfo(string path)
    {
        lock (_fileInfo)
        {
            if (_fileInfo.TryGetValue(path, out var cached)) return cached;
        }
        (string? Publisher, string? Description, string? Version) info;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            info = (Clean(v.CompanyName), Clean(v.FileDescription), Clean(v.FileVersion));
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            info = default;
        }
        lock (_fileInfo)
        {
            if (_fileInfo.Count > MaxCache) _fileInfo.Clear();
            _fileInfo[path] = info;
        }
        return info;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : Core.Privacy.Redactor.SanitizeUntrusted(s.Trim(), 96);

    public ProcessDetails? GetDetails(int pid)
    {
        var s = Latest.Processes.FirstOrDefault(p => p.Pid == pid);
        if (s is null) return null;
        var info = s.ImagePath is null ? default : FileInfo(s.ImagePath);
        string? arch = null, integrity = null;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                if (Native.IsWow64Process2(h, out var procMachine, out var nativeMachine))
                    arch = MachineName(procMachine == 0 ? nativeMachine : procMachine) + (procMachine != 0 ? " (emulated/WOW64)" : "");
                integrity = IntegrityLevel(h);
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }
        var parent = Latest.Processes.FirstOrDefault(p => p.Pid == s.ParentPid && p.StartTime <= s.StartTime);
        return new ProcessDetails(pid, s.Name, s.ImagePath, info.Publisher, info.Description, info.Version, arch, integrity,
            s.StartTime == DateTimeOffset.MinValue ? null : s.StartTime, s.ParentPid, parent?.Name);
    }

    private static string MachineName(ushort m) => m switch
    {
        0x8664 => "x64",
        0xAA64 => "ARM64",
        0x014C => "x86",
        0x01C4 => "ARM",
        _ => $"0x{m:X4}",
    };

    private static string? IntegrityLevel(IntPtr process)
    {
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token)) return null;
        try
        {
            Native.GetTokenInformation(token, Native.TokenIntegrityLevel, IntPtr.Zero, 0, out var len);
            if (len == 0 || len > 1024) return null;
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buf, len, out _)) return null;
                var sid = Marshal.ReadIntPtr(buf);
                var count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                var rid = (uint)Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
                return rid switch
                {
                    < 0x1000 => "Untrusted",
                    < 0x2000 => "Low",
                    < 0x3000 => "Medium",
                    < 0x4000 => "High",
                    _ => "System",
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    public override void Dispose()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        base.Dispose();
    }
}
