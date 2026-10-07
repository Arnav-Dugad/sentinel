using System.Runtime.InteropServices;

namespace Sentinel.Platform.Windows.Interop;

/// <summary>
/// Thin wrapper over the Performance Data Helper API. Counters are added by English path so the code works
/// on every display language. Rate counters need two collections before they produce values.
/// Queries can be rebuilt after resume, when counter handles occasionally go stale.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_VALID_DATA = 0;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private readonly Lock _gate = new();
    private readonly List<PdhCounter> _counters = [];
    private IntPtr _query;

    public PdhQuery()
    {
        Open();
    }

    private void Open()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    public bool IsOpen => _query != IntPtr.Zero;

    /// <summary>Adds a counter. Returns null when the counter set does not exist on this system.</summary>
    public PdhCounter? Add(string englishPath)
    {
        lock (_gate)
        {
            if (_query == IntPtr.Zero) return null;
            if (PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var handle) != 0) return null;
            var c = new PdhCounter(this, englishPath, handle);
            _counters.Add(c);
            return c;
        }
    }

    public bool Collect()
    {
        lock (_gate)
        {
            return _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;
        }
    }

    /// <summary>Closes and re-opens the query, re-adding every counter (used after sleep/resume).</summary>
    public void Rebuild()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            Open();
            foreach (var c in _counters)
                c.Handle = _query != IntPtr.Zero && PdhAddEnglishCounterW(_query, c.Path, IntPtr.Zero, out var h) == 0 ? h : IntPtr.Zero;
        }
    }

    internal double? GetValue(PdhCounter c, bool noCap)
    {
        lock (_gate)
        {
            if (c.Handle == IntPtr.Zero) return null;
            var fmt = PDH_FMT_DOUBLE | (noCap ? PDH_FMT_NOCAP100 : 0);
            if (PdhGetFormattedCounterValue(c.Handle, fmt, out _, out var v) != 0) return null;
            return v.CStatus is PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA && !double.IsNaN(v.DoubleValue) ? v.DoubleValue : null;
        }
    }

    internal Dictionary<string, double> GetArray(PdhCounter c, bool noCap)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (c.Handle == IntPtr.Zero) return result;
            var fmt = PDH_FMT_DOUBLE | (noCap ? PDH_FMT_NOCAP100 : 0);
            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(c.Handle, fmt, ref size, out _, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return result;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(c.Handle, fmt, ref size, out var count, buffer) != 0) return result;
                var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                    if (item.Value.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) continue;
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (name is null) continue;
                    // Duplicate instance names (e.g. two processes) are summed, which is the correct aggregate for rates.
                    result[name] = result.TryGetValue(name, out var existing) ? existing + item.Value.DoubleValue : item.Value.DoubleValue;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _counters.Clear();
        }
    }
}

internal sealed class PdhCounter(PdhQuery owner, string path, IntPtr handle)
{
    public string Path { get; } = path;
    internal IntPtr Handle { get; set; } = handle;

    public double? Value(bool noCap = false) => owner.GetValue(this, noCap);

    public Dictionary<string, double> Instances(bool noCap = false) => owner.GetArray(this, noCap);
}
