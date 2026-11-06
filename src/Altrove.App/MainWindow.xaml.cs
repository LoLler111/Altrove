using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Altrove.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;
        _model.UpdateRestartRequested += (_, _) => Close();
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing)
            return;
        // Say goodbye to the receiver before the process goes away.
        e.Cancel = true;
        _closing = true;
        await _model.DisposeAsync();
        // DisposeAsync may finish at once, while this Closing is still running: close again only after it.
        _ = Dispatcher.BeginInvoke(Close);
    }

    private void UseDarkTitleBar()
    {
        const int immersiveDarkMode = 20;
        var enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, immersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
