// Adapted from celmic (MIT, Celmic contributors): see third_party/celmic/NOTICE.md.
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace Altrove.Capture.Microphone;

/// <summary>
/// usbip-win2 (BSD-2-Clause, drivers signed by Microsoft): found if installed, installed on request (one
/// administrator prompt), and asked to attach Altrove's microphone. The installer is a pinned release, checked
/// against its SHA-256 before it runs.
/// </summary>
public static class UsbIpSetup
{
    public const string Version = "0.9.8.1";
    private const string InstallerUrl = "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe";
    private const string InstallerSha256 = "38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a";
    private const int ExitReboot = 3010; // Inno Setup's code when Windows must restart

    /// <summary>usbip.exe of an installed usbip-win2, or null.</summary>
    public static string? UsbipExe()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[] { "USBip", "usbip-win2" }.Select(dir => Path.Combine(programFiles, dir, "usbip.exe")).FirstOrDefault(File.Exists);
    }

    public static bool IsInstalled => UsbipExe() is not null;

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

    /// <summary>Downloads the pinned installer into the temp folder and checks it. Throws if anything is off.</summary>
    public static async Task<string> DownloadInstallerAsync(CancellationToken token)
    {
        var file = Path.Combine(Path.GetTempPath(), "Altrove", Path.GetFileName(new Uri(InstallerUrl).LocalPath));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Altrove");
        var data = await http.GetByteArrayAsync(InstallerUrl, token);
        if (!Convert.ToHexString(SHA256.HashData(data)).Equals(InstallerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("L'installatore di usbip-win2 scaricato non corrisponde a quello atteso: non lo avvio.");
        await File.WriteAllBytesAsync(file, data, token);
        return file;
    }

    /// <summary>Runs the installer as administrator (Windows asks once). True if Windows wants a restart.</summary>
    public static async Task<bool> InstallAsync(string installer)
    {
        using var process = Process.Start(new ProcessStartInfo(installer, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE={ExitReboot}")
        {
            Verb = "runas",
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("L'installatore di usbip-win2 non è partito.");
        await process.WaitForExitAsync();
        return process.ExitCode switch
        {
            0 => false,
            ExitReboot => true,
            var code => throw new InvalidOperationException($"L'installazione di usbip-win2 non è riuscita (codice {code})."),
        };
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
