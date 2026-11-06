using System.IO.Pipes;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Altrove.Capture.VirtualDisplay;

public sealed record DisplayMode(int Width, int Height, int RefreshRate);

/// <summary>
/// Talks to the running driver: its settings file (monitor count, resolutions) and its named pipe,
/// \\.\pipe\MTTVirtualDisplayPipe, which takes UTF-16 commands such as PING and RELOAD_DRIVER.
/// </summary>
public static class VddControl
{
    private const string PipeName = "MTTVirtualDisplayPipe";

    public static string SettingsFile => Path.Combine(DriverInstaller.SettingsFolder, "vdd_settings.xml");

    /// <summary>The settings Altrove gives the driver: one monitor, the modes listed, software cursor.</summary>
    public static string BuildSettings(int count, IEnumerable<DisplayMode> modes)
    {
        var resolutions = new XElement("resolutions");
        foreach (var mode in modes.Distinct())
        {
            resolutions.Add(new XElement("resolution",
                new XElement("width", mode.Width),
                new XElement("height", mode.Height),
                new XElement("refresh_rate", mode.RefreshRate)));
        }
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("vdd_settings",
                new XElement("monitors", new XElement("count", count)),
                new XElement("gpu", new XElement("friendlyname", "default")),
                resolutions,
                new XElement("options",
                    new XElement("CustomEdid", "false"),
                    new XElement("PreventSpoof", "false"),
                    new XElement("EdidCeaOverride", "false"),
                    // Windows draws the pointer into the image, so capture always shows it.
                    new XElement("HardwareCursor", "false"),
                    new XElement("SDR10bit", "false"),
                    new XElement("HDRPlus", "false"),
                    new XElement("logging", "false"),
                    new XElement("debuglogging", "false"))));
        return document.Declaration + Environment.NewLine + document.Root;
    }

    /// <summary>Modes every virtual monitor offers, on top of the one a receiver asks for.</summary>
    public static IReadOnlyList<DisplayMode> StandardModes { get; } =
    [
        new(1280, 720, 60), new(1280, 800, 60), new(1920, 1080, 60), new(1920, 1200, 60), new(2560, 1440, 60),
    ];

    public static void WriteSettings(int count, IEnumerable<DisplayMode> modes)
    {
        try
        {
            File.WriteAllText(SettingsFile, BuildSettings(count, modes), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException("Non riesco a scrivere le impostazioni dello schermo virtuale: reinstallalo da Altrove.", ex);
        }
    }

    /// <summary>True when the driver is loaded and answering.</summary>
    public static bool Ping() => Send("PING", TimeSpan.FromSeconds(2))?.Contains("PONG", StringComparison.Ordinal) == true;

    /// <summary>Makes the driver read its settings again and re-create its monitors.</summary>
    public static void Reload() => Send("RELOAD_DRIVER", TimeSpan.FromSeconds(5));

    /// <summary>
    /// Sends one command and returns what came back before the driver closed the pipe (it does so after
    /// every command, which Windows reports as a broken pipe rather than end of file). Null if no driver.
    /// </summary>
    public static string? Send(string command, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            try
            {
                pipe.Connect((int)Math.Max(100, (deadline - DateTime.UtcNow).TotalMilliseconds));
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Busy: the driver serves one client at a time and re-creates the pipe after each.
                Thread.Sleep(100);
                continue;
            }
            pipe.ReadMode = PipeTransmissionMode.Message;
            var bytes = Encoding.Unicode.GetBytes(command);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            var reply = new MemoryStream();
            var buffer = new byte[1024];
            try
            {
                var read = pipe.ReadAsync(buffer, 0, buffer.Length);
                while (read.Wait(deadline - DateTime.UtcNow > TimeSpan.Zero ? deadline - DateTime.UtcNow : TimeSpan.Zero) && read.Result > 0)
                {
                    reply.Write(buffer, 0, read.Result);
                    read = pipe.ReadAsync(buffer, 0, buffer.Length);
                }
            }
            catch (AggregateException ex) when (ex.InnerException is IOException)
            {
            }
            catch (IOException)
            {
            }
            return Decode(reply.ToArray());
        }
    }

    // Replies mix narrow (PONG, log lines) and wide (GETSETTINGS) text.
    private static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data.Length % 2 == 0 && data.Where((_, i) => i % 2 == 1).All(b => b == 0))
            return Encoding.Unicode.GetString(data).TrimEnd('\0');
        return Encoding.UTF8.GetString(data).TrimEnd('\0');
    }
}
