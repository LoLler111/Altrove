using System.Net.Sockets;

namespace Altrove.Core.Discovery;

/// <summary>
/// Whether a receiver answers on its port, for addresses typed by hand (nothing announces them): a TCP
/// connection, closed at once. The receiver sees a sender that never said hello, so it does not take the place
/// of the one streaming (docs/PROTOCOLLO.md).
/// </summary>
public static class ReceiverProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1.5);

    public static async Task<bool> IsReachableAsync(string host, int port, TimeSpan timeout, CancellationToken token = default)
    {
        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, limit.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
