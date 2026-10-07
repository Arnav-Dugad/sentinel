using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Audio endpoints and active sessions (which apps are producing sound) through Core Audio.
/// Session peak meters are read, never audio data; capture endpoints are only listed, never opened.
/// </summary>
public sealed class AudioProvider(ILogger<AudioProvider> log) : WindowsProvider(log), IAudioTelemetryProvider
{
    private const string Source = "Windows Core Audio (MMDevice API)";
    private string? _lastDefaultRender;
    private readonly Dictionary<uint, string?> _processNames = [];

    public override ProviderDescriptor Descriptor { get; } = Describe("audio", "Audio", "Devices", SamplingCost.Low, "2 s", Source);

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2));

    public IReadOnlyList<AudioDevice> Devices { get; private set; } = [];
    public IReadOnlyList<AudioSession> Sessions { get; private set; } = [];

    public override Task InitializeAsync(CancellationToken ct)
    {
        var ok = Read(includeSessions: false, null);
        SetCapability("Audio endpoints", ok, Source, ok ? null : "The Windows audio service is not available.");
        SetCapability("Audio sessions", ok, Source);
        return Task.CompletedTask;
    }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        Read(includeSessions: ctx.Mode == SamplingMode.Detail, ctx);
        return Task.CompletedTask;
    }

    private bool Read(bool includeSessions, SampleContext? ctx)
    {
        var result = CoreAudio.Query(includeSessions);
        if (result is not { } r) return false;
        var d = r.Defaults;
        Devices = r.Endpoints.Select(e => new AudioDevice(e.Id, e.Name, e.IsCapture,
            e.Id == d.GetValueOrDefault("render") || e.Id == d.GetValueOrDefault("capture"),
            e.Id == d.GetValueOrDefault("render-comms") || e.Id == d.GetValueOrDefault("capture-comms"),
            e.State switch { 1 => "Active", 2 => "Disabled", 8 => "Unplugged", _ => "Not present" }, e.SampleRate, e.Channels)).ToList();

        if (includeSessions)
        {
            Sessions = r.Sessions.Select(s => new AudioSession((int)s.Session.ProcessId,
                !string.IsNullOrWhiteSpace(s.Session.DisplayName) && !s.Session.DisplayName.StartsWith('@')
                    ? Core.Privacy.Redactor.SanitizeUntrusted(s.Session.DisplayName, 80)
                    : s.Session.IsSystem ? "System sounds" : ProcessName(s.Session.ProcessId) ?? $"Process {s.Session.ProcessId}",
                s.Session.Peak, s.Session.State == 1, s.Device)).OrderByDescending(s => s.PeakLevel).ToList();
        }

        var defaultRender = d.GetValueOrDefault("render");
        if (ctx is not null && _lastDefaultRender is not null && defaultRender != _lastDefaultRender)
        {
            var newName = Devices.FirstOrDefault(x => x.Id == defaultRender)?.Name ?? "none";
            ctx.Events.Publish(new SystemEvent(ctx.Now, EventCategory.Other, Severity.Info, $"Default playback device changed to {newName}", null, Source, newName,
                $"audio-default-{ctx.Now.ToUnixTimeSeconds()}"));
        }
        _lastDefaultRender = defaultRender;
        return true;
    }

    private string? ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var cached)) return cached;
        string? name = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
        if (_processNames.Count > 256) _processNames.Clear();
        _processNames[pid] = name;
        return name;
    }
}
