using System.IO;
using System.Windows;
using Altrove.Capture.VirtualDisplay;

namespace Altrove.App;

public partial class App : Application
{
    /// <summary>Argument of the elevated copy of Altrove that installs the virtual display driver.</summary>
    public const string InstallVirtualDisplayArgument = "--install-virtual-display";

    /// <summary>Argument of the elevated copy of Altrove that installs usbip-win2's drivers for the tablet's microphone.</summary>
    public const string InstallMicArgument = "--install-mic";

    /// <summary>Exit code of that copy when Windows must restart before the microphone works (as Windows Installer's).</summary>
    public const int ExitRestartNeeded = 3010;

    /// <summary>Where errors nobody caught end up, for bug reports: %LOCALAPPDATA%\Altrove\altrove.log.</summary>
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altrove", "altrove.log");

    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log($"Errore non gestito: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => Log($"Errore in un'attività: {args.Exception}");
        DispatcherUnhandledException += (_, args) => Log($"Errore nell'interfaccia: {args.Exception}");
        if (e.Args.Contains(InstallVirtualDisplayArgument))
        {
            Shutdown(InstallVirtualDisplay());
            return;
        }
        if (e.Args.Contains(InstallMicArgument))
        {
            Shutdown(InstallMic());
            return;
        }
        new MainWindow().Show();
    }

    /// <summary>Runs as administrator: installs the driver and takes its monitor off the desktop until it is needed.</summary>
    private static int InstallVirtualDisplay()
    {
        try
        {
            var inf = Path.Combine(AppContext.BaseDirectory, "VirtualDisplayDriver", "MttVDD.inf");
            DriverInstaller.Install(inf, VddControl.BuildSettings(1, VddControl.StandardModes));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installazione dello schermo virtuale non riuscita:\n{ex.Message}", "Altrove", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>Runs as administrator: installs the drivers of the bundled usbip-win2.</summary>
    private static int InstallMic()
    {
        try
        {
            return Altrove.Capture.Microphone.UsbIpSetup.Install() ? ExitRestartNeeded : 0;
        }
        catch (Exception ex)
        {
            Log($"Installazione di usbip-win2 non riuscita: {ex}");
            MessageBox.Show($"Installazione del microfono non riuscita:\n{ex.Message}", "Altrove", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
