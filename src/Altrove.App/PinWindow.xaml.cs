using System.Windows;
using System.Windows.Controls;

namespace Altrove.App;

/// <summary>Asks for the PIN a receiver shows the first time this PC connects to it.</summary>
public partial class PinWindow : Window
{
    public PinWindow(string receiverName, bool wrong)
    {
        InitializeComponent();
        Heading.Text = $"Collega «{receiverName}»";
        Wrong.Visibility = wrong ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PinBox.Focus();
    }

    /// <summary>The digits typed, once the user pressed Collega.</summary>
    public string? Pin { get; private set; }

    private void OnPinChanged(object sender, TextChangedEventArgs e)
    {
        var digits = new string(PinBox.Text.Where(char.IsAsciiDigit).ToArray());
        if (digits != PinBox.Text)
        {
            PinBox.Text = digits;
            PinBox.CaretIndex = digits.Length;
        }
        ConnectButton.IsEnabled = digits.Length == 6;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        Pin = PinBox.Text;
        DialogResult = true;
    }

    /// <summary>Shows the window over <paramref name="owner"/>; null if the user cancels.</summary>
    public static string? Ask(Window? owner, string receiverName, bool wrong)
    {
        var window = new PinWindow(receiverName, wrong) { Owner = owner };
        return window.ShowDialog() == true ? window.Pin : null;
    }
}
