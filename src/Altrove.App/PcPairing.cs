using System.Windows;
using Altrove.Core.Streaming;

namespace Altrove.App;

/// <summary>The receivers this PC is paired with (tokens in settings.json), and the PIN window for new ones.</summary>
public sealed class PcPairing(AppSettings settings) : IPairingHandler
{
    public string? TokenFor(string receiverId) => settings.PairingTokens.GetValueOrDefault(receiverId);

    public void Remember(string receiverId, string token)
    {
        settings.PairingTokens[receiverId] = token;
        settings.Save();
    }

    public void Forget(string receiverId)
    {
        if (settings.PairingTokens.Remove(receiverId))
            settings.Save();
    }

    public async Task<string?> AskPinAsync(string receiverName, bool wrong, CancellationToken token)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
            return null;
        return await dispatcher.InvokeAsync(() => PinWindow.Ask(Application.Current.MainWindow, receiverName, wrong));
    }
}
