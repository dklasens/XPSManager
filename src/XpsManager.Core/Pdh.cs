using System.Runtime.InteropServices;

namespace XpsManager.Core;

/// <summary>
/// Minimal Performance Data Helper query. Counters are added by English path so they work on any
/// display language. Rate counters need two collections before they report a value.
/// </summary>
sealed class PdhQuery : IDisposable
{
    const uint FormatDouble = 0x200, FormatNoCap100 = 0x8000, MoreData = 0x800007D2;
    IntPtr _query;

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    /// <summary>Returns IntPtr.Zero when the counter doesn't exist on this machine.</summary>
    public IntPtr Add(string path) =>
        _query != IntPtr.Zero && PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    public void Collect() { if (_query != IntPtr.Zero) PdhCollectQueryData(_query); }

    public double? Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        if (PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap100, out _, out var value) != 0) return null;
        return value.Status <= 1 ? value.Double : null;
    }

    /// <summary>Every instance of a wildcard counter, keyed by instance name (case-insensitive).</summary>
    public Dictionary<string, double> Values(IntPtr counter)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter == IntPtr.Zero) return result;
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out _, IntPtr.Zero) != MoreData) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out var count, buffer) != 0) return result;
            int itemSize = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
                if (item.Value.Status > 1) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                // Duplicate instance names (rare) are summed, which is right for every counter used here.
                if (name is not null) result[name] = result.GetValueOrDefault(name) + item.Value.Double;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CounterItem { public IntPtr Name; public CounterValue Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);
}
