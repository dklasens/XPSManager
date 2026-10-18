using System.Runtime.InteropServices;

namespace XpsManager.Core;

/// <param name="MediaMhz">The separate media engine (video decode/encode), not system memory.</param>
public sealed record GpuClocks(double? ClockMhz, double? RequestMhz, double? MaxMhz, double? MediaMhz, uint Throttle);

/// <summary>
/// Read-only access to Intel's graphics control library (ControlLib.dll, installed with the driver).
/// Frequency reads work without elevation. Structs are laid out by hand from igcl_api.h (v1.1).
/// </summary>
public sealed unsafe class IntelGraphics : IDisposable
{
    // ctl_freq_throttle_reason_flags_t
    public const uint ThrottleAveragePower = 1, ThrottleBurstPower = 2, ThrottleCurrent = 4, ThrottleThermal = 8,
        ThrottlePsu = 16, ThrottleSoftwareRange = 32, ThrottleHardwareRange = 64;

    IntPtr _api, _gpu, _media;
    double? _gpuMax;

    IntelGraphics() { }

    /// <summary>Why the last <see cref="TryOpen"/> returned null, for diagnostics.</summary>
    public static string? OpenError { get; private set; }

    /// <summary>Null when the library is missing or no Intel GPU is found.</summary>
    public static IntelGraphics? TryOpen()
    {
        var g = new IntelGraphics();
        try
        {
            byte* init = stackalloc byte[64];
            new Span<byte>(init, 64).Clear();
            *(uint*)init = 36;                      // ctl_init_args_t.Size
            *(uint*)(init + 8) = (1u << 16) | 1u;   // AppVersion 1.1
            *(uint*)(init + 12) = 1;                // CTL_INIT_FLAG_USE_LEVEL_ZERO
            IntPtr api;
            var rc = ctlInit(init, &api);
            if (rc != 0 || api == IntPtr.Zero) { OpenError = $"ctlInit 0x{rc:x}"; return null; }
            g._api = api;

            // Ask for the count first; the library only fills handles after it has been queried.
            uint count = 0;
            ctlEnumerateDevices(api, &count, null);
            var devices = stackalloc IntPtr[(int)Math.Min(count, 4)];
            count = Math.Min(count, 4);
            rc = ctlEnumerateDevices(api, &count, devices);
            if (rc != 0 || count == 0) { OpenError = $"ctlEnumerateDevices 0x{rc:x} ({count})"; g.Dispose(); return null; }
            var device = devices[0];

            uint domains = 0;
            rc = ctlEnumFrequencyDomains(device, &domains, null);
            if (rc != 0) { OpenError = $"ctlEnumFrequencyDomains 0x{rc:x}"; g.Dispose(); return null; }
            var handles = stackalloc IntPtr[(int)Math.Min(domains, 8)];
            domains = Math.Min(domains, 8);
            ctlEnumFrequencyDomains(device, &domains, handles);
            byte* props = stackalloc byte[32];
            for (int i = 0; i < domains; i++)
            {
                new Span<byte>(props, 32).Clear();
                *(uint*)props = 32;
                if (ctlFrequencyGetProperties(handles[i], props) != 0) continue;
                if (*(int*)(props + 8) == 0) { g._gpu = handles[i]; g._gpuMax = *(double*)(props + 24); }
                else if (*(int*)(props + 8) == 2) g._media = handles[i]; // ctl_freq_domain_t: GPU 0, MEMORY 1, MEDIA 2
            }
            if (g._gpu != IntPtr.Zero) return g;
            OpenError = $"No GPU frequency domain ({domains} domains)";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            OpenError = e.Message;
        }
        g.Dispose();
        return null;
    }

    public GpuClocks Read()
    {
        var (gpuActual, gpuRequest, throttle) = State(_gpu);
        var (mediaActual, _, _) = State(_media);
        return new GpuClocks(gpuActual, gpuRequest, _gpuMax, mediaActual, throttle);
    }

    static (double? Actual, double? Request, uint Throttle) State(IntPtr domain)
    {
        if (domain == IntPtr.Zero) return (null, null, 0);
        byte* state = stackalloc byte[56];
        new Span<byte>(state, 56).Clear();
        *(uint*)state = 56; // ctl_freq_state_t
        if (ctlFrequencyGetState(domain, state) != 0) return (null, null, 0);
        static double? Valid(double v) => v > 0 ? v : null; // the library reports -1 for unknown
        return (Valid(*(double*)(state + 40)), Valid(*(double*)(state + 16)), *(uint*)(state + 48));
    }

    /// <summary>Plain-language throttle reason, or null when the GPU isn't being held back.</summary>
    public static string? DescribeThrottle(uint flags)
    {
        // A user-set range isn't a limit worth flagging; the hardware range is the chip's own bounds.
        flags &= ~(ThrottleSoftwareRange | ThrottleHardwareRange);
        if (flags == 0) return null;
        var reasons = new List<string>();
        if ((flags & (ThrottleAveragePower | ThrottleBurstPower)) != 0) reasons.Add("Power limit");
        if ((flags & ThrottleThermal) != 0) reasons.Add("Thermal");
        if ((flags & ThrottleCurrent) != 0) reasons.Add("Current limit");
        if ((flags & ThrottlePsu) != 0) reasons.Add("Power supply");
        return string.Join(", ", reasons);
    }

    public void Dispose()
    {
        if (_api != IntPtr.Zero) ctlClose(_api);
        _api = _gpu = _media = IntPtr.Zero;
    }

    const string Lib = "ControlLib.dll";
    [DllImport(Lib)] static extern uint ctlInit(byte* args, IntPtr* api);
    [DllImport(Lib)] static extern uint ctlClose(IntPtr api);
    [DllImport(Lib)] static extern uint ctlEnumerateDevices(IntPtr api, uint* count, IntPtr* devices);
    [DllImport(Lib)] static extern uint ctlEnumFrequencyDomains(IntPtr device, uint* count, IntPtr* domains);
    [DllImport(Lib)] static extern uint ctlFrequencyGetProperties(IntPtr domain, byte* properties);
    [DllImport(Lib)] static extern uint ctlFrequencyGetState(IntPtr domain, byte* state);
}
