using System.Runtime.InteropServices;

namespace Altrove.Capture.VirtualDisplay;

/// <summary>
/// The classic display-settings API: which display devices exist, which monitor hangs off each, and
/// putting one on the desktop at a given mode and position, or taking it off (width and height 0).
/// </summary>
public static class DisplaySettings
{
    public sealed record Device(string Name, string AdapterString, string MonitorId, bool AttachedToDesktop);

    public static IReadOnlyList<Device> List()
    {
        var result = new List<Device>();
        for (uint i = 0; ; i++)
        {
            var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(null, i, ref adapter, 0))
                break;
            var monitor = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            var monitorId = EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0) ? monitor.DeviceId : "";
            result.Add(new Device(adapter.DeviceName, adapter.DeviceString, monitorId, (adapter.StateFlags & AttachedToDesktopFlag) != 0));
        }
        return result;
    }

    public static (int Width, int Height, int Refresh, int X, int Y)? Current(string deviceName)
    {
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettingsW(deviceName, EnumCurrentSettings, ref mode) || mode.PelsWidth == 0)
            return null;
        return ((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency, mode.PositionX, mode.PositionY);
    }

    /// <summary>Puts the device on the desktop with this mode, its top-left corner at (x, y).</summary>
    public static void Attach(string deviceName, int width, int height, int refresh, int x, int y)
    {
        var mode = new DevMode
        {
            Size = (short)Marshal.SizeOf<DevMode>(),
            Fields = DmPosition | DmPelsWidth | DmPelsHeight | DmDisplayFrequency,
            PositionX = x,
            PositionY = y,
            PelsWidth = (uint)width,
            PelsHeight = (uint)height,
            DisplayFrequency = (uint)refresh,
        };
        Apply(deviceName, ref mode);
    }

    /// <summary>Takes the device off the desktop: Windows then behaves as if that monitor were unplugged.</summary>
    public static void Detach(string deviceName)
    {
        var mode = new DevMode
        {
            Size = (short)Marshal.SizeOf<DevMode>(),
            Fields = DmPosition | DmPelsWidth | DmPelsHeight,
        };
        Apply(deviceName, ref mode);
    }

    private static void Apply(string deviceName, ref DevMode mode)
    {
        var result = ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero);
        if (result != DispChangeSuccessful)
            throw new InvalidOperationException($"Windows ha rifiutato la modalità dello schermo virtuale (codice {result}).");
        // An empty call applies every pending change at once.
        result = ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (result != DispChangeSuccessful)
            throw new InvalidOperationException($"Windows non ha applicato le impostazioni dello schermo (codice {result}).");
    }

    private const int AttachedToDesktopFlag = 0x1;
    private const int EnumCurrentSettings = -1;
    private const uint DmPosition = 0x20, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x1, CdsNoReset = 0x10000000;
    private const int DispChangeSuccessful = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    // DEVMODEW, display variant of the union.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string device, ref DevMode mode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, IntPtr mode, IntPtr hwnd, uint flags, IntPtr param);
}
