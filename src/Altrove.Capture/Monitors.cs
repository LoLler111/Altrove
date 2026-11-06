using Altrove.Capture.Native;

namespace Altrove.Capture;

/// <summary>A screen Windows knows about. Sizes are physical pixels.</summary>
public sealed record MonitorInfo(IntPtr Handle, string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary, int Index, bool IsVirtual = false)
{
    public string DisplayName => IsVirtual
        ? $"Schermo virtuale di Altrove – {Width}×{Height}"
        : $"Schermo {Index}{(IsPrimary ? " (principale)" : "")} – {Width}×{Height}";

    public override string ToString() => DisplayName;
}

public static class Monitors
{
    private const uint PrimaryFlag = 1;

    /// <summary>All monitors, primary first. The process must be per-monitor DPI aware for real pixel sizes.</summary>
    public static IReadOnlyList<MonitorInfo> List()
    {
        var found = new List<(IntPtr Handle, Interop.MonitorInfoEx Info)>();
        Interop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new Interop.MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<Interop.MonitorInfoEx>() };
            if (Interop.GetMonitorInfo(handle, ref info))
                found.Add((handle, info));
            return true;
        }, IntPtr.Zero);

        var virtualName = VirtualDisplay.VirtualMonitor.FindDevice()?.Name;
        return found
            .OrderByDescending(m => (m.Info.Flags & PrimaryFlag) != 0)
            .ThenBy(m => m.Info.Monitor.Left)
            .ThenBy(m => m.Info.Monitor.Top)
            .Select((m, i) => new MonitorInfo(
                m.Handle,
                m.Info.Device,
                m.Info.Monitor.Left,
                m.Info.Monitor.Top,
                m.Info.Monitor.Right - m.Info.Monitor.Left,
                m.Info.Monitor.Bottom - m.Info.Monitor.Top,
                (m.Info.Flags & PrimaryFlag) != 0,
                i + 1,
                string.Equals(m.Info.Device, virtualName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
