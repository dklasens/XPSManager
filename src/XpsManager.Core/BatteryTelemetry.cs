using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XpsManager.Core;

public sealed record BatterySnapshot(string State, double? Watts, double? Volts,
    double? RemainingWh, double? FullWh, uint? Cycles, string? Note = null, double? DesignWh = null)
{
    // Capacity estimate, not state of charge. New batteries can report above design capacity.
    public double? HealthPercent => FullWh is > 0 && DesignWh is > 0 ? FullWh / DesignWh * 100 : null;
}

/// <summary>Read-only Windows battery IOCTLs; no BIOS or charging settings are written.</summary>
public static class BatteryTelemetry
{
    // Windows SDK: GUID_DEVINTERFACE_BATTERY and IOCTL_BATTERY_QUERY_*.
    static readonly Guid BatteryGuid = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    const uint QueryTag = 0x294040, QueryInformation = 0x294044, QueryStatus = 0x29404c;

    public static BatterySnapshot Read()
    {
        try
        {
            var guid = BatteryGuid;
            var devices = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (devices == new IntPtr(-1)) throw new Win32Exception();
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                    if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, index, ref data))
                    {
                        if (Marshal.GetLastWin32Error() == 259) break;
                        throw new Win32Exception();
                    }
                    SetupDiGetDeviceInterfaceDetail(devices, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                    if (size == 0) throw new Win32Exception();
                    var detail = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(devices, ref data, detail, size, out _, IntPtr.Zero))
                            throw new Win32Exception();
                        var path = Marshal.PtrToStringUni(detail + 4)!;
                        using var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (handle.IsInvalid) throw new Win32Exception();
                        var tag = Query(handle, QueryTag, new byte[4], 4);
                        if (BitConverter.ToUInt32(tag) == 0) continue;
                        var request = new byte[12]; // BATTERY_QUERY_INFORMATION, BatteryInformation = 0
                        tag.CopyTo(request, 0);
                        var info = Query(handle, QueryInformation, request, 36);
                        var capabilities = BitConverter.ToUInt32(info);
                        if ((capabilities & 0x80000000) == 0 || (capabilities & 0x20000000) != 0) continue;
                        var wait = new byte[20]; // BATTERY_WAIT_STATUS; zero timeout returns immediately
                        tag.CopyTo(wait, 0);
                        var status = Query(handle, QueryStatus, wait, 16);
                        return Decode(capabilities, BitConverter.ToUInt32(status), BitConverter.ToInt32(status, 12),
                            BitConverter.ToUInt32(status, 8), BitConverter.ToUInt32(status, 4),
                            BitConverter.ToUInt32(info, 16), BitConverter.ToUInt32(info, 32), BitConverter.ToUInt32(info, 12));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
                return new("Unavailable", null, null, null, null, null, "No system battery reported.");
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }
        }
        catch (Win32Exception)
        {
            return new("Unavailable", null, null, null, null, null, "Battery telemetry unavailable. Retrying automatically.");
        }
    }

    public static BatterySnapshot Decode(uint capabilities, uint state, int rate, uint voltage,
        uint remaining, uint full, uint cycles, uint design = uint.MaxValue)
    {
        bool absolute = (capabilities & 0x40000000) == 0;
        string label = (state & 6) switch { 4 => "Charging", 2 => "Discharging", 0 => "Idle", _ => "Unknown" };
        return new(label, absolute && rate != int.MinValue ? rate / 1000d : null,
            voltage != uint.MaxValue ? voltage / 1000d : null,
            absolute && remaining != uint.MaxValue ? remaining / 1000d : null,
            absolute && full != uint.MaxValue && full != 0 ? full / 1000d : null,
            cycles is > 0 and < uint.MaxValue ? cycles : null,
            absolute ? null : "Battery reports relative units; watts and Wh are unavailable.",
            absolute && design is > 0 and < uint.MaxValue ? design / 1000d : null);
    }

    static byte[] Query(SafeFileHandle handle, uint code, byte[] input, int length)
    {
        var output = new byte[length];
        if (!DeviceIoControl(handle, code, input, input.Length, output, length, out var read, IntPtr.Zero))
            throw new Win32Exception();
        if (read < length) throw new Win32Exception(13);
        return output;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
