using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Telemetry;

namespace Sentinel.Intelligence;

/// <summary>
/// Detects system changes by diffing inventory snapshots against the last stored snapshot of each kind.
/// The first snapshot of a kind establishes a baseline and produces no changes.
/// </summary>
public sealed class ChangeTracker(HistoryStore store, ProviderSet providers, IEventSink events, ILogger<ChangeTracker> log)
{
    public async Task RunAsync(DateTimeOffset now, CancellationToken ct)
    {
        var changes = new List<ChangeRecord>();
        var p = providers;

        if (p.Drivers.Drivers.Count > 0)
            await Diff("drivers", p.Drivers.Drivers.GroupBy(d => d.DeviceInstanceId).ToDictionary(g => g.Key, g => $"{g.First().DeviceName}|{g.First().Version}"),
                (key, before, after) =>
                {
                    var (name, oldV) = Split(before);
                    var (newName, newV) = Split(after);
                    if (before is null) return new ChangeRecord(now, "Driver", $"Driver added: {newName} {newV}", null, newV, "Driver inventory");
                    if (after is null) return new ChangeRecord(now, "Driver", $"Driver removed: {name}", oldV, null, "Driver inventory");
                    return oldV == newV ? null : new ChangeRecord(now, "Driver", $"{newName} driver changed", oldV, newV, "Driver inventory");
                }, changes, now, ct, maxAddedReported: 10).ConfigureAwait(false);

        if (p.Software.Software.Count > 0)
            await Diff("software", p.Software.Software.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().Version ?? ""),
                (name, before, after) =>
                {
                    if (before is null)
                    {
                        events.Publish(new SystemEvent(now, EventCategory.SoftwareInstalled, Severity.Info, $"{name} installed", after, "Installed apps inventory", name, $"sw-add-{name}-{after}"));
                        return new ChangeRecord(now, "App", $"{name} installed", null, after, "Installed apps inventory");
                    }
                    if (after is null)
                    {
                        events.Publish(new SystemEvent(now, EventCategory.SoftwareRemoved, Severity.Info, $"{name} removed", before, "Installed apps inventory", name, $"sw-rm-{name}-{before}"));
                        return new ChangeRecord(now, "App", $"{name} removed", before, null, "Installed apps inventory");
                    }
                    return before == after ? null : new ChangeRecord(now, "App", $"{name} updated", before, after, "Installed apps inventory");
                }, changes, now, ct).ConfigureAwait(false);

        if (p.Software.Services.Count > 0)
            await Diff("services", p.Software.Services.ToDictionary(s => s.Name, s => $"{s.DisplayName}|{s.StartType}", StringComparer.OrdinalIgnoreCase),
                (name, before, after) =>
                {
                    if (before is null) return new ChangeRecord(now, "Service", $"New service: {Split(after).Name}", null, Split(after).Value, "Service Control Manager");
                    if (after is null) return new ChangeRecord(now, "Service", $"Service removed: {Split(before).Name}", Split(before).Value, null, "Service Control Manager");
                    var (n, oldStart) = Split(before);
                    var (_, newStart) = Split(after);
                    return oldStart == newStart ? null : new ChangeRecord(now, "Service", $"{n} start type changed", oldStart, newStart, "Service Control Manager");
                }, changes, now, ct).ConfigureAwait(false);

        await Diff("startup", p.Startup.Items.GroupBy(i => i.Name + " (" + i.Location + ")").ToDictionary(g => g.Key, g => g.First().Enabled ? "Enabled" : "Disabled"),
            (name, before, after) =>
            {
                if (before is null)
                {
                    events.Publish(new SystemEvent(now, EventCategory.StartupItemAdded, Severity.Info, $"Startup item appeared: {name}", null, "Startup inventory", name, $"st-add-{name}"));
                    return new ChangeRecord(now, "Startup", $"Startup item appeared: {name}", null, after, "Startup inventory");
                }
                if (after is null) return new ChangeRecord(now, "Startup", $"Startup item removed: {name}", before, null, "Startup inventory");
                return before == after ? null : new ChangeRecord(now, "Startup", $"Startup item {after!.ToLowerInvariant()}: {name}", before, after, "Startup inventory");
            }, changes, now, ct).ConfigureAwait(false);

        if (p.Devices.Devices.Count > 0)
            await Diff("devices", p.Devices.Devices.ToDictionary(d => d.ContainerId, d => d.Name, StringComparer.OrdinalIgnoreCase),
                (_, before, after) => before is null ? new ChangeRecord(now, "Device", $"New device detected: {after}", null, after, "Device inventory") : null,
                changes, now, ct, keepRemoved: true).ConfigureAwait(false);

        var inv = p.System.Inventory;
        if (inv.BiosVersion is not null)
            await Diff("bios", new Dictionary<string, string> { ["bios"] = inv.BiosVersion },
                (_, before, after) => before is not null && after is not null && before != after
                    ? new ChangeRecord(now, "Firmware", "BIOS/UEFI version changed", before, after, "SMBIOS") : null, changes, now, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(inv.OsBuild))
            await Diff("os", new Dictionary<string, string> { ["build"] = inv.OsBuild },
                (_, before, after) => before is not null && after is not null && before != after
                    ? new ChangeRecord(now, "Windows", "Windows build changed", before, after, "Registry") : null, changes, now, ct).ConfigureAwait(false);

        var bat = p.Battery.Latest;
        if (bat.Present && bat.FullChargeMWh.Value is { } full && bat.DesignMWh.Value is { } design and > 0)
        {
            await store.UpsertCapacityAsync([new CapacityPoint(now, full, design, "Sentinel")], ct).ConfigureAwait(false);
            var bucket = Math.Floor(full / design * 100).ToString(CultureInfo.InvariantCulture);
            await Diff("battery-capacity", new Dictionary<string, string> { ["health"] = bucket },
                (_, before, after) => before is not null && after is not null && before != after
                    ? new ChangeRecord(now, "Battery", "Battery full-charge capacity changed", before + "% of design", after + "% of design", "Battery firmware") : null,
                changes, now, ct).ConfigureAwait(false);
        }

        foreach (var disk in p.Storage.Disks)
        {
            if (!p.Storage.Health.TryGetValue(disk.Id, out var h)) continue;
            await store.UpsertDiskHealthAsync(new DiskHealthDay(disk.Id, TimeRange.StartOfDay(now), h.PercentageUsed.Value, h.AvailableSpare.Value, h.Temperature.Value,
                h.DataWrittenBytes.Value, h.DataReadBytes.Value, h.PowerOnHours.Value, h.MediaErrors.Value, h.UnsafeShutdowns.Value, h.HealthStatus), ct).ConfigureAwait(false);
            var state = $"{h.HealthStatus ?? "?"}|{h.PercentageUsed.Value?.ToString(CultureInfo.InvariantCulture) ?? "?"}|{h.CriticalWarning?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
            await Diff("disk-health-" + disk.Id, new Dictionary<string, string> { ["state"] = state },
                (_, before, after) => before is not null && after is not null && before != after
                    ? new ChangeRecord(now, "Storage", $"{disk.Model} health data changed", Describe(before), Describe(after), "Drive health") : null,
                changes, now, ct).ConfigureAwait(false);
        }

        if (changes.Count > 0)
        {
            await store.InsertChangesAsync(changes, ct).ConfigureAwait(false);
            log.LogInformation("Recorded {Count} system changes", changes.Count);
        }

        static string Describe(string s)
        {
            var parts = s.Split('|');
            return parts.Length == 3 ? $"{parts[0]}, {parts[1]}% used, critical warning {parts[2]}" : s;
        }
    }

    private async Task Diff(string kind, Dictionary<string, string> current, Func<string, string?, string?, ChangeRecord?> describe,
        List<ChangeRecord> sink, DateTimeOffset now, CancellationToken ct, int maxAddedReported = 50, bool keepRemoved = false)
    {
        var json = JsonSerializer.Serialize(current.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var previous = store.GetInventory(kind);
        if (previous is { } prev && prev.Hash == hash) return;

        if (previous is { } old)
        {
            var before = JsonSerializer.Deserialize<Dictionary<string, string>>(old.Json) ?? [];
            var added = 0;
            foreach (var (key, value) in current)
            {
                before.TryGetValue(key, out var was);
                if (was is null && ++added > maxAddedReported) continue;
                if (describe(key, was, value) is { } change) sink.Add(change);
            }
            foreach (var (key, value) in before)
            {
                if (current.ContainsKey(key)) continue;
                if (keepRemoved)
                {
                    // Devices that are merely disconnected stay part of the known set.
                    current[key] = value;
                    continue;
                }
                if (describe(key, value, null) is { } change) sink.Add(change);
            }
            if (keepRemoved)
            {
                json = JsonSerializer.Serialize(current.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
                hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            }
        }
        await store.SetInventoryAsync(kind, hash, json, now, ct).ConfigureAwait(false);
    }

    private static (string Name, string Value) Split(string? s)
    {
        if (s is null) return ("", "");
        var i = s.LastIndexOf('|');
        return i < 0 ? (s, "") : (s[..i], s[(i + 1)..]);
    }
}
