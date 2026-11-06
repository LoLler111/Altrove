using System.Net;
using System.Net.Sockets;
using Altrove.Core.Discovery;

namespace Altrove.Core.Tests;

public class ReceiverProbeTests
{
    [Fact]
    public async Task ListeningPortIsReachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(await ReceiverProbe.IsReachableAsync("127.0.0.1", port, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ClosedPortIsNot()
    {
        // A port that was free a moment ago: nothing listens there now.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.False(await ReceiverProbe.IsReachableAsync("127.0.0.1", port, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SilentAddressGivesUpAtTheTimeout()
    {
        // TEST-NET-1 (RFC 5737): routed nowhere, so the connection neither succeeds nor is refused.
        var started = DateTime.UtcNow;
        Assert.False(await ReceiverProbe.IsReachableAsync("192.0.2.1", 47800, TimeSpan.FromMilliseconds(300)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }
}
