// Adapted from celmic (MIT, Celmic contributors): see third_party/celmic/NOTICE.md.
using System.ComponentModel;
using System.Diagnostics;
using Altrove.Capture.Native;

namespace Altrove.Capture.Microphone;

/// <summary>
/// usbip-win2 0.9.8.1 (drivers signed by Microsoft): the part Altrove needs ships next to the apps
/// (third_party/usbip-win2: the two driver packages and usbip.exe), so nothing is downloaded. Installed on request
/// (one administrator prompt) the way its own installer does it, and asked to attach Altrove's microphone.
/// </summary>
public static class UsbIpSetup
{
    public const string Version = "0.9.8.1";

    /// <summary>The emulated USB host controller: a root device with this id, driver usbip2_ude.</summary>
    public const string HardwareId = @"ROOT\USBIP_WIN2\UDE";

    private static readonly Guid UsbClass = new("36fc9e60-c465-11cf-8056-444553540000");

    /// <summary>Where the bundled files are, next to Altrove.exe (and altrove-cli.exe).</summary>
    public static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "usbip-win2");

    /// <summary>
    /// usbip.exe to talk to the driver with, or null. A full usbip-win2 installed by its own setup wins: its tool
    /// matches the driver it installed, which may be another version than ours.
    /// </summary>
    public static string? UsbipExe()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[] { Path.Combine(programFiles, "USBip"), Path.Combine(programFiles, "usbip-win2"), BundledFolder }
            .Select(dir => Path.Combine(dir, "usbip.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>True when the driver is in Windows (from Altrove or from usbip-win2's setup) and usbip.exe is at hand.</summary>
    public static bool IsInstalled => RootDevice.Exists(UsbClass, HardwareId) && UsbipExe() is not null;

    /// <summary>
    /// Installs the drivers from the bundled folder; needs administrator rights. First the filter for the USB 3
    /// root hubs (Windows restarts every USB 3 hub for a moment), then the emulated host controller, whose root hub
    /// then gets the filter. True if Windows wants a restart.
    /// </summary>
    public static bool Install()
    {
        var restart = RootDevice.InstallPackage(Path.Combine(BundledFolder, "usbip2_filter.inf"));
        restart |= RootDevice.Install(UsbClass, "USB", HardwareId, Path.Combine(BundledFolder, "usbip2_ude.inf"));
        return restart;
    }

    /// <summary>Removes the emulated host controller (the microphone goes with it); needs administrator rights.</summary>
    public static void Uninstall() => RootDevice.Remove(UsbClass, HardwareId);

    private static string Target => $"usbip://127.0.0.1:{UsbIpMicServer.TcpPort}/{UsbIpMicServer.BusId}";

    /// <summary>
    /// Attaches the microphone to Windows unless it already is. Users may attach without elevation. Without
    /// --once usbip-win2 keeps reattaching by itself (also after Altrove restarts); with 0.9.8.1, --once dropped
    /// the connection whenever an app stopped recording.
    /// </summary>
    public static void Attach()
    {
        var usbip = UsbipExe() ?? throw new InvalidOperationException("usbip-win2 non è installato.");
        if (Run(usbip, "port").Output.Contains(Target, StringComparison.OrdinalIgnoreCase))
            return;
        var target = $"--tcp-port {UsbIpMicServer.TcpPort} attach -r 127.0.0.1 -b {UsbIpMicServer.BusId}";
        Run(usbip, target + " --stop"); // drop attempts still pending from an earlier run
        var (code, output) = Run(usbip, target);
        if (code != 0)
            throw new InvalidOperationException($"Windows non ha collegato il microfono: {output}");
    }

    /// <summary>Takes the microphone off Windows (it disappears from the apps' lists).</summary>
    public static void Detach()
    {
        if (UsbipExe() is not { } usbip)
            return;
        var target = $"--tcp-port {UsbIpMicServer.TcpPort} attach -r 127.0.0.1 -b {UsbIpMicServer.BusId}";
        Run(usbip, target + " --stop");
        var ports = Run(usbip, "port").Output;
        // "Port 01: ... usbip://127.0.0.1:47809/1-1": detach that port.
        foreach (var line in ports.Split('\n'))
        {
            if (!line.Contains(Target, StringComparison.OrdinalIgnoreCase))
                continue;
            var digits = new string(line.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0)
                Run(usbip, $"detach -p {int.Parse(digits)}");
        }
    }

    private static (int Code, string Output) Run(string exe, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return (process.ExitCode, output.Trim());
        }
        catch (Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
