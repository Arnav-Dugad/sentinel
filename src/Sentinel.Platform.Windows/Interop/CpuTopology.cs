using System.Runtime.InteropServices;
using Sentinel.Domain;

namespace Sentinel.Platform.Windows.Interop;

internal sealed record CpuTopologyInfo(
    int PhysicalCores,
    int LogicalProcessors,
    int Groups,
    int NumaNodes,
    int PerformanceCores,
    int EfficiencyCores,
    bool IsHybrid,
    IReadOnlyList<CacheInfo> Caches,
    IReadOnlyList<int> EfficiencyClassByLogical);

/// <summary>Parses GetLogicalProcessorInformationEx. Efficiency classes are the documented way to identify P/E cores.</summary>
internal static class CpuTopology
{
    private const int RelationProcessorCore = 0;
    private const int RelationNumaNode = 1;
    private const int RelationCache = 2;
    private const int RelationAll = 0xFFFF;

    public static CpuTopologyInfo Read()
    {
        uint len = 0;
        Native.GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref len);
        if (len == 0) return Fallback();
        var buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(RelationAll, buffer, ref len)) return Fallback();
            var cores = new List<(byte EfficiencyClass, List<int> Logical)>();
            var caches = new List<CacheInfo>();
            var numa = 0;
            var groupOffsets = GroupOffsets();
            var offset = 0;
            while (offset < len)
            {
                var p = buffer + offset;
                var relationship = Marshal.ReadInt32(p);
                var size = Marshal.ReadInt32(p, 4);
                if (size <= 0) break;
                switch (relationship)
                {
                    case RelationProcessorCore:
                    {
                        var efficiency = Marshal.ReadByte(p, 9);
                        var groupCount = (ushort)Marshal.ReadInt16(p, 30);
                        cores.Add((efficiency, ReadGroupMasks(p + 32, groupCount, groupOffsets)));
                        break;
                    }
                    case RelationNumaNode:
                        numa++;
                        break;
                    case RelationCache:
                    {
                        var level = Marshal.ReadByte(p, 8);
                        var cacheSize = (uint)Marshal.ReadInt32(p, 12);
                        var type = Marshal.ReadInt32(p, 16);
                        var groupCount = (ushort)Marshal.ReadInt16(p, 38);
                        var shared = ReadGroupMasks(p + 40, Math.Max((ushort)1, groupCount), groupOffsets).Count;
                        caches.Add(new CacheInfo(level, type switch { 1 => "Instruction", 2 => "Data", 3 => "Trace", _ => "Unified" }, cacheSize, shared));
                        break;
                    }
                }
                offset += size;
            }

            var logicalCount = cores.Sum(c => c.Logical.Count);
            var classes = new int[Math.Max(logicalCount, Environment.ProcessorCount)];
            foreach (var (eff, logical) in cores)
                foreach (var l in logical)
                    if (l < classes.Length) classes[l] = eff;
            var distinct = cores.Select(c => c.EfficiencyClass).Distinct().Count();
            var maxClass = cores.Count > 0 ? cores.Max(c => c.EfficiencyClass) : 0;
            var hybrid = distinct > 1;
            var pCores = hybrid ? cores.Count(c => c.EfficiencyClass == maxClass) : cores.Count;
            var eCores = hybrid ? cores.Count - pCores : 0;

            // Collapse identical cache instances into one entry per (level, type) with instance count folded into SharedBy.
            var summarized = caches.GroupBy(c => (c.Level, c.Type))
                .Select(g => new CacheInfo(g.Key.Level, g.Key.Type, g.Sum(c => c.SizeBytes), g.Count()))
                .OrderBy(c => c.Level).ThenBy(c => c.Type).ToList();

            return new CpuTopologyInfo(cores.Count, logicalCount, Native.GetActiveProcessorGroupCount(), Math.Max(1, numa), pCores, eCores, hybrid, summarized, classes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int[] GroupOffsets()
    {
        var groups = Native.GetActiveProcessorGroupCount();
        var offsets = new int[Math.Max(1, (int)groups)];
        var running = 0;
        for (ushort g = 0; g < groups; g++)
        {
            offsets[g] = running;
            running += (int)Native.GetActiveProcessorCount(g);
        }
        return offsets;
    }

    // GROUP_AFFINITY is { KAFFINITY Mask; WORD Group; WORD Reserved[3]; } = 16 bytes on 64-bit.
    private static List<int> ReadGroupMasks(IntPtr p, int count, int[] groupOffsets)
    {
        var logical = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = p + i * 16;
            var mask = (ulong)Marshal.ReadInt64(entry);
            var group = (ushort)Marshal.ReadInt16(entry, 8);
            var baseIndex = group < groupOffsets.Length ? groupOffsets[group] : group * 64;
            for (var bit = 0; bit < 64; bit++)
                if ((mask & (1UL << bit)) != 0) logical.Add(baseIndex + bit);
        }
        return logical;
    }

    private static CpuTopologyInfo Fallback() =>
        new(Environment.ProcessorCount, Environment.ProcessorCount, 1, 1, 0, 0, false, [], new int[Environment.ProcessorCount]);
}
