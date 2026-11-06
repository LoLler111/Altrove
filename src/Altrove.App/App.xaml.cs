using System.IO;
using System.Windows;
using Altrove.Capture.VirtualDisplay;

namespace Altrove.App;

public partial class App : Application
{
    /// <summary>Argument of the elevated copy of Altrove that installs the virtual display driver.</summary>
    public const string InstallVirtualDisplayArgument = "--install-virtual-display";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains(InstallVirtualDisplayArgument))
        {
            Shutdown(InstallVirtualDisplay());
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
}
