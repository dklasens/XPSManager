using System.Management;
using System.Runtime.InteropServices;

namespace XpsManager.Core;

public sealed record DeviceInfo(string Model, string BiosVersion);

/// <param name="OnAc">Null when Windows can't tell.</param>
/// <param name="BatteryPercent">Null when there's no battery or it's unknown.</param>
/// <param name="WindowsEstimate">Windows' own time-left estimate; null on AC or while it's still calculating.</param>
/// <param name="EnergySaver">Windows energy saver (battery saver) is on.</param>
public sealed record PowerStatus(bool? OnAc, int? BatteryPercent, TimeSpan? WindowsEstimate = null, bool EnergySaver = false);

/// <param name="RefreshHz">The refresh rate selected in Windows (Settings, Display, Advanced display).</param>
/// <param name="Dynamic">Dynamic refresh rate is on: Windows drops to <paramref name="BaseRefreshHz"/> when the screen is static.</param>
/// <param name="BaseRefreshHz">The rate the desktop idles at under dynamic refresh rate.</param>
/// <param name="Brightness">Built-in panel brightness, 0–100; null when not reported.</param>
public sealed record DisplayState(int? RefreshHz, bool Dynamic, int? BaseRefreshHz, int? Brightness);

public static class SystemInfo
{
    /// <summary>Model and BIOS version, e.g. "XPS 14 DA14260" / "1.11.0". Readable without elevation.</summary>
    public static DeviceInfo Device()
    {
        try
        {
            using var cs = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            using var bios = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            var model = cs.Get().Cast<ManagementObject>().FirstOrDefault()?["Model"] as string;
            var version = bios.Get().Cast<ManagementObject>().FirstOrDefault()?["SMBIOSBIOSVersion"] as string;
            return new DeviceInfo(model ?? "Unknown model", version ?? "?");
        }
        catch (ManagementException)
        {
            return new DeviceInfo("Unknown model", "?");
        }
    }

    public static PowerStatus Power()
    {
        if (!GetSystemPowerStatus(out var s))
            return new PowerStatus(null, null);
        bool? onAc = s.ACLineStatus switch { 1 => true, 0 => false, _ => null };
        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        TimeSpan? estimate = s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new PowerStatus(onAc, percent, estimate, (s.SystemStatusFlag & 1) != 0);
    }

    /// <summary>
    /// The Windows power mode in effect (Settings › Power › Power mode), or null if it can't be read.
    /// Energy saver overrides whatever mode is selected.
    /// </summary>
    public static string? WindowsPowerMode(PowerStatus power)
    {
        if (power.EnergySaver) return "Energy saver";
        if (PowerGetEffectiveOverlayScheme(out var overlay) != 0) return null;
        return overlay.ToString() switch
        {
            "00000000-0000-0000-0000-000000000000" => "Balanced",
            "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Best power efficiency",
            "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => "Better performance",
            "ded574b5-45a0-4f42-8737-46345c09c238" => "Best performance",
            _ => "Custom"
        };
    }

    /// <summary>
    /// The built-in panel's refresh rate (or the first active display if there's no built-in one).
    /// Uses QueryDisplayConfig in "virtual refresh rate aware" mode (Windows 11) so dynamic refresh rate
    /// is reported as the selected rate, not the 60 Hz the desktop idles at. Older builds fall back
    /// to the plain query, which reports the desktop rate.
    /// </summary>
    public static DisplayState Display(bool includeBrightness)
    {
        var brightness = includeBrightness ? Brightness() : null;
        foreach (var virtualAware in new[] { true, false })
        {
            uint flags = QdcOnlyActivePaths | (virtualAware ? QdcVirtualRefreshRateAware : 0);
            if (GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != 0 || pathCount == 0) continue;
            var paths = new byte[pathCount * PathSize];
            var modes = new byte[modeCount * ModeSize];
            if (QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) continue;

            int chosen = 0;
            for (int i = 0; i < pathCount; i++)
                if (IsBuiltIn(BitConverter.ToUInt32(paths, i * PathSize + PathOutputTechnology))) { chosen = i; break; }
            int o = chosen * PathSize;
            var desktop = Hz(BitConverter.ToUInt32(paths, o + PathRefreshNum), BitConverter.ToUInt32(paths, o + PathRefreshDen));
            bool boost = (BitConverter.ToUInt32(paths, o + PathFlags) & PathBoostRefreshRate) != 0;

            // In virtual-aware mode the index is split: target mode in the low 16 bits.
            uint index = BitConverter.ToUInt32(paths, o + PathTargetModeIndex);
            int target = virtualAware ? (int)(index & 0xffff) : (int)index;
            int? selected = desktop;
            if (target < modeCount && BitConverter.ToUInt32(modes, target * ModeSize) == ModeInfoTypeTarget)
                selected = Hz(BitConverter.ToUInt32(modes, target * ModeSize + ModeVSyncNum), BitConverter.ToUInt32(modes, target * ModeSize + ModeVSyncDen)) ?? desktop;
            return new DisplayState(selected, boost && selected > desktop, boost ? desktop : null, brightness);
        }
        return new DisplayState(null, false, null, brightness);
    }

    static int? Hz(uint numerator, uint denominator) =>
        denominator == 0 || numerator == 0 ? null : (int)Math.Round((double)numerator / denominator);

    // DISPLAYCONFIG_OUTPUT_TECHNOLOGY: INTERNAL, DISPLAYPORT_EMBEDDED, UDI_EMBEDDED.
    static bool IsBuiltIn(uint technology) => technology is 0x80000000 or 11 or 13;

    static int? Brightness()
    {
        try
        {
            using var search = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            return search.Get().Cast<ManagementObject>().Select(o => (int?)Convert.ToInt32(o["CurrentBrightness"])).FirstOrDefault();
        }
        catch (Exception e) when (e is ManagementException or COMException) { return null; }
    }

    /// <summary>
    /// Maps performance-counter processor instances ("group,index") to their efficiency class.
    /// The highest class is the performance cores; Windows reports two classes on this chip.
    /// </summary>
    public static Dictionary<string, int> CoreClasses()
    {
        var map = new Dictionary<string, int>();
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
        if (length <= 0) return map;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return map;
            for (int offset = 0; offset < length;)
            {
                int size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0) break;
                // SYSTEM_CPU_SET_INFORMATION: Group @12, LogicalProcessorIndex @14, EfficiencyClass @18.
                int group = Marshal.ReadInt16(buffer, offset + 12);
                int index = Marshal.ReadByte(buffer, offset + 14);
                map[$"{group},{index}"] = Marshal.ReadByte(buffer, offset + 18);
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return map;
    }

    // QueryDisplayConfig. DISPLAYCONFIG_PATH_INFO (72 bytes): targetInfo.modeInfoIdx @32,
    // outputTechnology @36, refreshRate @48/@52, flags @68. DISPLAYCONFIG_MODE_INFO (64 bytes):
    // infoType @0, targetMode.vSyncFreq @32/@36.
    const uint QdcOnlyActivePaths = 0x2, QdcVirtualRefreshRateAware = 0x40, PathBoostRefreshRate = 0x10, ModeInfoTypeTarget = 2;
    const int PathSize = 72, ModeSize = 64, PathTargetModeIndex = 32, PathOutputTechnology = 36,
        PathRefreshNum = 48, PathRefreshDen = 52, PathFlags = 68, ModeVSyncNum = 32, ModeVSyncDen = 36;

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("powrprof.dll")]
    static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint pathCount, byte[] paths, ref uint modeCount, byte[] modes, IntPtr topology);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemCpuSetInformation(IntPtr info, int length, out int returned, IntPtr process, int flags);
}
