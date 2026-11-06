using Altrove.Core.Video;

namespace Altrove.Capture.VirtualDisplay;

/// <summary>
/// The extra monitor for "Estendi": the driver's virtual display, put on the desktop at the size of
/// the receiver while streaming and taken off afterwards, so no phantom monitor catches the mouse.
/// </summary>
public static class VirtualMonitor
{
    // The driver's monitors report this EDID manufacturer/product ("Generic Monitor (VDD by MTT)").
    private const string MonitorIdMarker = "MTT1337";

    public static bool IsDriverInstalled => DriverInstaller.IsInstalled();

    /// <summary>A Remote Desktop session shows only its own remote monitors, never the driver's.</summary>
    public static bool IsRemoteSession => GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>The display device the virtual monitor hangs off, attached or not; null if the driver is not running.</summary>
    public static DisplaySettings.Device? FindDevice() =>
        DisplaySettings.List().FirstOrDefault(d => d.MonitorId.Contains(MonitorIdMarker, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a display device name that belongs to the virtual monitor.</summary>
    public static bool IsVirtual(string deviceName) =>
        FindDevice() is { } device && device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The virtual monitor's mode for a receiver: its screen in landscape, shrunk to the user's limit and
    /// the decoder's, with even sides.
    /// </summary>
    public static DisplayMode PlanMode(int displayWidth, int displayHeight, int maxWidth, int maxHeight, int? userMaxHeight, int fps)
    {
        var (width, height) = (Math.Max(displayWidth, displayHeight), Math.Min(displayWidth, displayHeight));
        if (width <= 0 || height <= 0)
            (width, height) = (1920, 1080);
        var limitHeight = userMaxHeight is > 0 ? Math.Min(maxHeight, userMaxHeight.Value) : maxHeight;
        var (w, h) = StreamSizing.Fit(width, height, maxWidth, limitHeight);
        return new DisplayMode(w, h, fps);
    }

    /// <summary>
    /// Puts the virtual monitor on the desktop at <paramref name="mode"/>, to the right of the other
    /// monitors, and returns it as a capturable monitor. Installs nothing: the driver must be there.
    /// </summary>
    public static async Task<MonitorInfo> AttachAsync(DisplayMode mode, CancellationToken token)
    {
        // The driver only offers the modes in its settings: make sure this one is among them.
        var modes = VddControl.StandardModes.Append(mode).ToList();
        var wanted = VddControl.BuildSettings(1, modes);
        var current = File.Exists(VddControl.SettingsFile) ? await File.ReadAllTextAsync(VddControl.SettingsFile, token) : "";
        if (current != wanted)
        {
            VddControl.WriteSettings(1, modes);
            VddControl.Reload();
        }

        var device = await WaitForDeviceAsync(token)
            ?? throw new InvalidOperationException(IsRemoteSession
                ? "In una sessione Desktop remoto Windows non mostra lo schermo virtuale: usa Altrove dal PC stesso."
                : "Lo schermo virtuale non è comparso: prova a reinstallarlo da Altrove.");

        // Right of everything else, top edge level with the primary monitor.
        var others = Monitors.List().Where(m => !m.DeviceName.Equals(device.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var x = others.Count > 0 ? others.Max(m => m.Left + m.Width) : 0;
        var y = others.FirstOrDefault(m => m.IsPrimary)?.Top ?? 0;
        DisplaySettings.Attach(device.Name, mode.Width, mode.Height, mode.RefreshRate, x, y);

        for (var i = 0; i < 50; i++)
        {
            var monitor = Monitors.List().FirstOrDefault(m => m.DeviceName.Equals(device.Name, StringComparison.OrdinalIgnoreCase));
            if (monitor is not null && monitor.Width == mode.Width && monitor.Height == mode.Height)
                return monitor;
            await Task.Delay(100, token);
        }
        throw new InvalidOperationException($"Windows non ha attivato lo schermo virtuale a {mode.Width}×{mode.Height}.");
    }

    /// <summary>Takes the virtual monitor off the desktop if it is on it. Safe to call any time.</summary>
    public static void Detach()
    {
        if (FindDevice() is { AttachedToDesktop: true } device)
            DisplaySettings.Detach(device.Name);
    }

    private static async Task<DisplaySettings.Device?> WaitForDeviceAsync(CancellationToken token)
    {
        // After a reload the driver re-creates its monitor; Windows needs a moment to see it.
        for (var i = 0; i < 100; i++)
        {
            if (FindDevice() is { } device)
                return device;
            await Task.Delay(100, token);
        }
        return null;
    }
}
