using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Altrove.Core.Protocol;
using Altrove.Core.Streaming;

namespace Altrove.Core.Tests;

public class SenderSessionTests
{
    private static readonly HelloMessage Sender = new(AltroveProtocol.Version, Roles.Sender, "pc", "PC-TEST", "0.1.0");

    [Fact]
    public async Task Handshake_start_and_frames_reach_the_receiver()
    {
        await using var receiver = await FakeReceiver.StartAsync();
        await using var session = await SenderSession.ConnectAsync(receiver.Uri, Sender, CancellationToken.None);
        Assert.Equal("Tab A", session.Receiver.Name);
        Assert.Equal((1920, 1080), session.PlanSize(2560, 1440));
        Assert.Equal((1280, 720), session.PlanSize(2560, 1440, 720));

        var source = new FakeSource();
        await session.StartAsync(source, new EncoderSettings(1920, 1080, 60, 8000), StreamModes.Mirror, CancellationToken.None);

        source.Emit(Delta(1)); // before any IDR: dropped
        source.Emit(Idr(2));
        source.Emit(Delta(3));

        var hello = Assert.IsType<HelloMessage>(await receiver.NextControlAsync());
        Assert.Equal("PC-TEST", hello.Name);
        var start = Assert.IsType<StartMessage>(await receiver.NextControlAsync(skipPings: true));
        Assert.Equal(1920, start.Width);

        var first = await receiver.NextVideoAsync();
        Assert.True(VideoPacket.TryDecode(first, out var flags, out var pts, out _));
        Assert.Equal(VideoPacketFlags.Keyframe | VideoPacketFlags.ParameterSets, flags);
        Assert.Equal(2, pts);
        Assert.True(VideoPacket.TryDecode(await receiver.NextVideoAsync(), out flags, out pts, out _));
        Assert.Equal(VideoPacketFlags.None, flags);
        Assert.Equal(3, pts);

        // A keyframe request reaches the encoder and holds deltas back until the next IDR.
        await receiver.SendAsync(new KeyframeMessage());
        await WaitUntil(() => source.KeyframeRequests > 0);
        source.Emit(Delta(4));
        source.Emit(Idr(5));
        Assert.True(VideoPacket.TryDecode(await receiver.NextVideoAsync(), out _, out pts, out _));
        Assert.Equal(5, pts);

        var closed = new TaskCompletionSource<string>();
        session.Closed += why => closed.TrySetResult(why);
        await session.StopAsync();
        var bye = Assert.IsType<ByeMessage>(await receiver.NextControlAsync(skipPings: true));
        Assert.Equal(ByeReasons.User, bye.Reason);
        Assert.Equal("Trasmissione fermata.", await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Receiver_with_another_protocol_is_refused()
    {
        await using var receiver = await FakeReceiver.StartAsync(protocol: 99);
        var error = await Assert.ThrowsAsync<IOException>(() => SenderSession.ConnectAsync(receiver.Uri, Sender, CancellationToken.None));
        Assert.Contains("protocollo 99", error.Message);
    }

    [Fact]
    public async Task Bye_from_the_receiver_closes_the_session()
    {
        await using var receiver = await FakeReceiver.StartAsync();
        await using var session = await SenderSession.ConnectAsync(receiver.Uri, Sender, CancellationToken.None);
        var closed = new TaskCompletionSource<string>();
        session.Closed += why => closed.TrySetResult(why);
        await session.StartAsync(new FakeSource(), new EncoderSettings(1280, 720, 30, 4000), StreamModes.Mirror, CancellationToken.None);
        await receiver.SendAsync(new ByeMessage(ByeReasons.Replaced));
        Assert.Contains("altro dispositivo", await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static EncodedFrame Idr(long pts) => new(new byte[] { 0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2, 0, 0, 0, 1, 0x65, 3 }, pts, true);

    private static EncodedFrame Delta(long pts) => new(new byte[] { 0, 0, 0, 1, 0x41, 9 }, pts, false);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakeSource : IEncodedVideoSource
    {
        public int KeyframeRequests;
        public (int Width, int Height) SourceSize => (2560, 1440);
        public string EncoderName => "fake";
        public event Action<EncodedFrame>? FrameEncoded;
        public event Action<Exception>? Failed;
        public Task StartAsync(EncoderSettings settings, CancellationToken token) => Task.CompletedTask;
        public void RequestKeyframe() => Interlocked.Increment(ref KeyframeRequests);
        public void Emit(EncodedFrame frame) => FrameEncoded?.Invoke(frame);
        public ValueTask DisposeAsync()
        {
            _ = Failed;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A receiver on localhost that records what it gets.</summary>
    private sealed class FakeReceiver : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly TaskCompletionSource<WebSocket> _socket = new();
        private readonly System.Threading.Channels.Channel<(WebSocketMessageType Type, byte[] Data)> _messages =
            System.Threading.Channels.Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();

        private FakeReceiver(HttpListener listener, int port)
        {
            _listener = listener;
            Uri = new Uri($"ws://localhost:{port}/");
        }

        public Uri Uri { get; }

        public static Task<FakeReceiver> StartAsync(int protocol = AltroveProtocol.Version)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            var receiver = new FakeReceiver(listener, port);
            _ = Task.Run(() => receiver.AcceptAsync(protocol));
            return Task.FromResult(receiver);
        }

        private async Task AcceptAsync(int protocol)
        {
            var context = await _listener.GetContextAsync();
            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            _socket.SetResult(ws);
            var hello = new HelloMessage(protocol, Roles.Receiver, "tab", "Tab A", "0.1.0", new DisplayInfo(1920, 1200, 60), ["h264"], 1920, 1080);
            await ws.SendAsync(Encoding.UTF8.GetBytes(hello.ToJson()), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    await _messages.Writer.WriteAsync((result.MessageType, message.ToArray()));
                }
            }
            catch (Exception ex) when (ex is WebSocketException or HttpListenerException or ObjectDisposedException)
            {
            }
        }

        public async Task SendAsync(ControlMessage message)
        {
            var ws = await _socket.Task;
            await ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJson()), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task<ControlMessage?> NextControlAsync(bool skipPings = false)
        {
            while (true)
            {
                var (type, data) = await NextAsync(m => m.Type == WebSocketMessageType.Text);
                var message = ControlMessage.Parse(Encoding.UTF8.GetString(data));
                if (skipPings && message is PingMessage or PongMessage)
                    continue;
                _ = type;
                return message;
            }
        }

        public async Task<byte[]> NextVideoAsync() => (await NextAsync(m => m.Type == WebSocketMessageType.Binary)).Data;

        private readonly List<(WebSocketMessageType Type, byte[] Data)> _skipped = [];

        private async Task<(WebSocketMessageType Type, byte[] Data)> NextAsync(Func<(WebSocketMessageType Type, byte[] Data), bool> match)
        {
            var earlier = _skipped.FindIndex(m => match(m));
            if (earlier >= 0)
            {
                var found = _skipped[earlier];
                _skipped.RemoveAt(earlier);
                return found;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var message = await _messages.Reader.ReadAsync(timeout.Token);
                if (match(message))
                    return message;
                _skipped.Add(message);
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public ValueTask DisposeAsync()
        {
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}
