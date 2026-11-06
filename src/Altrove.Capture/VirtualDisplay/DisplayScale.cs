using System.Runtime.InteropServices;

namespace Altrove.Capture.VirtualDisplay;

/// <summary>
/// Windows' per-monitor scale (Settings → Display → Scale), read and set like the Settings app does: through
/// DisplayConfigGet/SetDeviceInfo with the undocumented DPI scale requests (-3 and -4). The values are steps
/// relative to the scale Windows recommends for that monitor. No administrator rights needed.
/// </summary>
public static class DisplayScale
{
    /// <summary>The steps Windows uses, in percent; a monitor offers a range of them.</summary>
    private static readonly int[] Steps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>Current scale and the ones Windows allows for <paramref name="deviceName"/> (e.g. \\.\DISPLAY11); null if not found.</summary>
    public static (int Current, IReadOnlyList<int> Available)? Get(string deviceName)
    {
        if (Find(deviceName) is not { } source)
            return null;
        var request = new DpiScaleGet
        {
            Header = new DeviceInfoHeader { Type = GetDpiScale, Size = (uint)Marshal.SizeOf<DpiScaleGet>(), AdapterId = source.AdapterId, Id = source.Id },
        };
        if (DisplayConfigGetDeviceInfo(ref request) != 0)
            return null;
        var recommended = -request.MinScaleRel;
        var current = Math.Clamp(recommended + request.CurScaleRel, 0, Steps.Length - 1);
        var last = Math.Clamp(recommended + request.MaxScaleRel, 0, Steps.Length - 1);
        return (Steps[current], Steps[..(last + 1)]);
    }

    /// <summary>Sets the scale in percent (one of <see cref="Get"/>'s values). False if Windows refused or the monitor is gone.</summary>
    public static bool Set(string deviceName, int percent)
    {
        if (Find(deviceName) is not { } source)
            return false;
        var target = Array.IndexOf(Steps, percent);
        if (target < 0)
            return false;
        var get = new DpiScaleGet
        {
            Header = new DeviceInfoHeader { Type = GetDpiScale, Size = (uint)Marshal.SizeOf<DpiScaleGet>(), AdapterId = source.AdapterId, Id = source.Id },
        };
        if (DisplayConfigGetDeviceInfo(ref get) != 0)
            return false;
        var recommended = -get.MinScaleRel;
        var set = new DpiScaleSet
        {
            Header = new DeviceInfoHeader { Type = SetDpiScale, Size = (uint)Marshal.SizeOf<DpiScaleSet>(), AdapterId = source.AdapterId, Id = source.Id },
            ScaleRel = target - recommended,
        };
        return DisplayConfigSetDeviceInfo(ref set) == 0;
    }

    /// <summary>The active display path whose source is <paramref name="deviceName"/>.</summary>
    private static (Luid AdapterId, uint Id)? Find(string deviceName)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out var pathCount, out var modeCount) != 0)
                return null;
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            var result = QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == ErrorInsufficientBuffer)
                continue; // the layout changed between the two calls
            if (result != 0)
                return null;
            foreach (var path in paths.AsSpan(0, (int)pathCount))
            {
                var name = new SourceDeviceName
                {
                    Header = new DeviceInfoHeader
                    {
                        Type = GetSourceName,
                        Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                        AdapterId = path.SourceAdapterId,
                        Id = path.SourceId,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref name) == 0 && string.Equals(name.ViewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return (path.SourceAdapterId, path.SourceId);
            }
            return null;
        }
        return null;
    }

    // ---- user32 display configuration ----

    private const uint OnlyActivePaths = 2; // QDC_ONLY_ACTIVE_PATHS
    private const int ErrorInsufficientBuffer = 122;
    private const int GetSourceName = 1;   // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
    private const int GetDpiScale = -3;    // undocumented, used by the Settings app
    private const int SetDpiScale = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    // DISPLAYCONFIG_PATH_INFO, 72 bytes: source (20), target (48), flags.
    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public Luid SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoIdx;
        public uint SourceStatusFlags;
        public Luid TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoIdx;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;
        public uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO, 64 bytes; only its size matters here.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo
    {
        public int InfoType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleGet
    {
        public DeviceInfoHeader Header;
        public int MinScaleRel;
        public int CurScaleRel;
        public int MaxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleSet
    {
        public DeviceInfoHeader Header;
        public int ScaleRel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, IntPtr currentTopology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DpiScaleGet request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DpiScaleSet request);
}
