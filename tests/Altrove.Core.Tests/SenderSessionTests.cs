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
    public async Task Touch_configure_and_a_new_source_without_reconnecting()
    {
        await using var receiver = await FakeReceiver.StartAsync();
        await using var session = await SenderSession.ConnectAsync(receiver.Uri, Sender, CancellationToken.None);
        var touches = new System.Collections.Concurrent.ConcurrentQueue<TouchMessage>();
        var requests = new System.Collections.Concurrent.ConcurrentQueue<ConfigureMessage>();
        session.TouchReceived += touches.Enqueue;
        session.ConfigureRequested += requests.Enqueue;

        var first = new FakeSource();
        await session.StartAsync(first, new EncoderSettings(1920, 1080, 60, 8000), StreamModes.Mirror, CancellationToken.None);
        Assert.IsType<HelloMessage>(await receiver.NextControlAsync());
        Assert.IsType<StartMessage>(await receiver.NextControlAsync(skipPings: true));
        session.SendSettings(new SettingsMessage(StreamModes.Mirror, null, 0, 60, 8000, new SettingsChoices([StreamModes.Mirror], [], [0], [60], [8000])));
        Assert.IsType<SettingsMessage>(await receiver.NextControlAsync(skipPings: true));

        await receiver.SendAsync(new TouchMessage([new TouchPointer(3, 0.5, 0.5, TouchStates.Down)]));
        await receiver.SendAsync(new ConfigureMessage(Mode: StreamModes.Extend));
        await WaitUntil(() => !touches.IsEmpty && !requests.IsEmpty);
        Assert.Equal(3, touches.Single().Pointers.Single().Id);
        Assert.Equal(StreamModes.Extend, requests.Single().Mode);

        var second = new FakeSource();
        await session.SwitchSourceAsync(second, new EncoderSettings(1920, 1200, 60, 8000), StreamModes.Extend, CancellationToken.None);
        var start = Assert.IsType<StartMessage>(await receiver.NextControlAsync(skipPings: true));
        Assert.Equal((1920, 1200, StreamModes.Extend), (start.Width, start.Height, start.Mode));

        // The old source is no longer heard; the new one's deltas wait for its first IDR.
        first.Emit(Idr(10));
        second.Emit(Delta(11));
        second.Emit(Idr(12));
        Assert.True(VideoPacket.TryDecode(await receiver.NextVideoAsync(), out _, out var pts, out _));
        Assert.Equal(12, pts);
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

    [Fact]
    public async Task A_bye_for_going_away_lets_the_sender_reconnect_and_a_user_bye_does_not()
    {
        foreach (var (reason, retry) in new[] { (ByeReasons.Away, true), (ByeReasons.User, false), (ByeReasons.Replaced, false) })
        {
            await using var receiver = await FakeReceiver.StartAsync();
            await using var session = await SenderSession.ConnectAsync(receiver.Uri, Sender, CancellationToken.None);
            var closed = new TaskCompletionSource<string>();
            session.Closed += why => closed.TrySetResult(why);
            await session.StartAsync(new FakeSource(), new EncoderSettings(1280, 720, 30, 4000), StreamModes.Mirror, CancellationToken.None);
            await receiver.SendAsync(new ByeMessage(reason));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(retry, session.CanReconnect);
        }
    }

    [Fact]
    public async Task A_receiver_that_stops_answering_pings_counts_as_lost()
    {
        var limit = SenderSession.SilenceLimit;
        SenderSession.SilenceLimit = TimeSpan.FromSeconds(2);
        try
        {
            await using var receiver = await FakeReceiver.StartAsync();
            receiver.AnswersPings = false;
            await using var session = await SenderSession.ConnectAsync(receiver.Uri, Sender, CancellationToken.None);
            var closed = new TaskCompletionSource<string>();
            session.Closed += why => closed.TrySetResult(why);
            await session.StartAsync(new FakeSource(), new EncoderSettings(1280, 720, 30, 4000), StreamModes.Mirror, CancellationToken.None);
            Assert.Equal("Il ricevitore non risponde più.", await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(session.CanReconnect);
        }
        finally
        {
            SenderSession.SilenceLimit = limit;
        }
    }

    [Fact]
    public async Task Pairing_with_a_known_token_needs_no_pin()
    {
        await using var receiver = await FakeReceiver.StartAsync(pin: "123456", knownToken: "saved");
        var pairing = new FakePairing { Tokens = { ["tab"] = "saved" } };
        await using var session = await SenderSession.ConnectAsync(receiver.Uri, Sender, pairing, CancellationToken.None);
        Assert.IsType<HelloMessage>(await receiver.NextControlAsync());
        Assert.Equal("saved", Assert.IsType<PairMessage>(await receiver.NextControlAsync()).Token);
        Assert.Empty(pairing.Asked);
        Assert.Equal("saved", pairing.Tokens["tab"]);
    }

    [Fact]
    public async Task Pairing_asks_the_pin_again_when_wrong_and_keeps_the_new_token()
    {
        await using var receiver = await FakeReceiver.StartAsync(pin: "123456");
        // An old token the receiver forgot: dropped, then a wrong PIN, then the right one.
        var pairing = new FakePairing { Tokens = { ["tab"] = "forgotten" }, Pins = new(["111111", " 123456 "]) };
        await using var session = await SenderSession.ConnectAsync(receiver.Uri, Sender, pairing, CancellationToken.None);
        Assert.Equal([("Tab A", false), ("Tab A", true)], pairing.Asked);
        Assert.Equal("new-token", pairing.Tokens["tab"]);
        Assert.IsType<HelloMessage>(await receiver.NextControlAsync());
        Assert.Equal("forgotten", Assert.IsType<PairMessage>(await receiver.NextControlAsync()).Token);
        Assert.Equal("111111", Assert.IsType<PairMessage>(await receiver.NextControlAsync()).Pin);
        Assert.Equal("123456", Assert.IsType<PairMessage>(await receiver.NextControlAsync()).Pin);
    }

    [Fact]
    public async Task Giving_up_on_the_pin_cancels_and_says_goodbye()
    {
        await using var receiver = await FakeReceiver.StartAsync(pin: "123456");
        var pairing = new FakePairing();
        await Assert.ThrowsAsync<PairingCancelledException>(() => SenderSession.ConnectAsync(receiver.Uri, Sender, pairing, CancellationToken.None));
        Assert.IsType<HelloMessage>(await receiver.NextControlAsync());
        Assert.IsType<PairMessage>(await receiver.NextControlAsync());
        Assert.Equal(ByeReasons.User, Assert.IsType<ByeMessage>(await receiver.NextControlAsync()).Reason);
    }

    [Fact]
    public async Task Without_a_pairing_handler_a_receiver_that_wants_one_is_refused()
    {
        await using var receiver = await FakeReceiver.StartAsync(pin: "123456");
        var error = await Assert.ThrowsAsync<IOException>(() => SenderSession.ConnectAsync(receiver.Uri, Sender, CancellationToken.None));
        Assert.Contains("PIN", error.Message);
    }

    [Fact]
    public async Task Keys_reach_the_sender_and_the_encoder_starts_at_the_chosen_rate()
    {
        await using var receiver = await FakeReceiver.StartAsync();
        await using var session = await SenderSession.ConnectAsync(receiver.Uri, Sender, CancellationToken.None);
        session.AdaptBitrate = true;
        var keys = new System.Collections.Concurrent.ConcurrentQueue<KeyMessage>();
        session.KeyReceived += keys.Enqueue;
        var source = new FakeSource();
        await session.StartAsync(source, new EncoderSettings(1280, 720, 30, 9000), StreamModes.Mirror, CancellationToken.None);
        Assert.Equal(9000, source.StartKbps);
        await receiver.SendAsync(new KeyMessage(Key: "v", Modifiers: [KeyModifiers.Ctrl]));
        await WaitUntil(() => !keys.IsEmpty);
        Assert.Equal("v", keys.Single().Key);
    }

    private sealed class FakePairing : IPairingHandler
    {
        public Dictionary<string, string> Tokens { get; } = [];
        public Queue<string> Pins { get; init; } = new();
        public List<(string Name, bool Wrong)> Asked { get; } = [];
        public string? TokenFor(string receiverId) => Tokens.GetValueOrDefault(receiverId);
        public void Remember(string receiverId, string token) => Tokens[receiverId] = token;
        public void Forget(string receiverId) => Tokens.Remove(receiverId);

        public Task<string?> AskPinAsync(string receiverName, bool wrong, CancellationToken token)
        {
            Asked.Add((receiverName, wrong));
            return Task.FromResult(Pins.Count > 0 ? Pins.Dequeue() : null);
        }
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
        public Task StartAsync(EncoderSettings settings, CancellationToken token)
        {
            StartKbps = settings.BitrateKbps;
            return Task.CompletedTask;
        }
        public void RequestKeyframe() => Interlocked.Increment(ref KeyframeRequests);
        public int StartKbps, BitrateKbps;
        public void SetBitrate(int kbps) => BitrateKbps = kbps;
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

        /// <summary>With <paramref name="pin"/> it wants pairing: <paramref name="knownToken"/> opens at once, the PIN gives "new-token".</summary>
        public static Task<FakeReceiver> StartAsync(int protocol = AltroveProtocol.Version, string? pin = null, string? knownToken = null)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            var receiver = new FakeReceiver(listener, port);
            _ = Task.Run(() => receiver.AcceptAsync(protocol, pin, knownToken));
            return Task.FromResult(receiver);
        }

        public bool AnswersPings { get; set; } = true;

        private async Task AcceptAsync(int protocol, string? pin, string? knownToken)
        {
            var context = await _listener.GetContextAsync();
            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            _socket.SetResult(ws);
            var hello = new HelloMessage(protocol, Roles.Receiver, "tab", "Tab A", "0.1.0", new DisplayInfo(1920, 1200, 60), ["h264"], 1920, 1080,
                pin is null ? null : [Features.Pairing]);
            await ws.SendAsync(Encoding.UTF8.GetBytes(hello.ToJson()), WebSocketMessageType.Text, true, CancellationToken.None);
            async Task Reply(ControlMessage message) =>
                await ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJson()), WebSocketMessageType.Text, true, CancellationToken.None);
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
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        switch (ControlMessage.Parse(Encoding.UTF8.GetString(message.ToArray())))
                        {
                            case PairMessage pair when pin is not null:
                                if (pair.Token is not null && pair.Token == knownToken)
                                    await Reply(new PairedMessage(knownToken));
                                else if (pair.Pin == pin)
                                    await Reply(new PairedMessage("new-token"));
                                else
                                    await Reply(new PinRequiredMessage(Wrong: pair.Pin is not null));
                                break;
                            case PingMessage ping when AnswersPings:
                                await Reply(new PongMessage(ping.T));
                                break;
                        }
                    }
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
