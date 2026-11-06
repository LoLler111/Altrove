using Velopack;

namespace Altrove.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must come first: when Setup.exe or the updater starts us with its hook arguments, this
        // handles them and exits. A downloaded update not applied yet is installed here too.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
