using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Altrove.Core.Protocol;
using Altrove.Core.Video;

namespace Altrove.Core.Streaming;

public sealed record SenderStats(
    double SentFps,
    double Mbps,
    double? RoundTripMs,
    double? ReceiverFps,
    int DroppedFrames,
    int QueuedFrames);

/// <summary>
/// The sender's side of one connection to a receiver: handshake, then video packets with control
/// messages in between. When the network cannot keep up it drops frames up to the next IDR, so the
/// delay never builds up (docs/PROTOCOLLO.md).
/// </summary>
public sealed class SenderSession : IAsyncDisposable
{
    /// <summary>Frames waiting to be sent before the queue is thrown away: about 130 ms at 60 fps.</summary>
    internal const int MaxQueuedFrames = 8;

    /// <summary>Audio packets waiting before the oldest are dropped: about half a second of 10 ms Opus packets.</summary>
    internal const int MaxQueuedAudio = 50;

    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Queue<byte[]> _video = new();
    private readonly Queue<string> _control = new();
    private readonly Queue<byte[]> _audio = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IEncodedVideoSource? _source;
    private bool _waitingForKeyframe;
    private string? _finalMessage;
    private Task? _sendLoop, _receiveLoop, _pingLoop;
    private int _closed;

    // Statistics, reset every second.
    private int _framesSent, _droppedTotal;
    private long _bytesSent;
    private long _statsStart;
    private double? _roundTripMs, _receiverFps;

    private SenderSession(ClientWebSocket socket, HelloMessage receiver)
    {
        _socket = socket;
        Receiver = receiver;
    }

    public HelloMessage Receiver { get; }

    /// <summary>Raised about once a second while streaming.</summary>
    public event Action<SenderStats>? StatsUpdated;

    /// <summary>Raised once when the connection ends; the text says why, in Italian, for the user.</summary>
    public event Action<string>? Closed;

    public static async Task<SenderSession> ConnectAsync(Uri receiver, HelloMessage self, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(receiver, timeout.Token);
            await SendTextAsync(socket, self.ToJson(), timeout.Token);
            HelloMessage? hello = null;
            while (hello is null)
            {
                var text = await ReceiveTextAsync(socket, timeout.Token)
                    ?? throw new IOException("Il ricevitore ha chiuso la connessione.");
                switch (ControlMessage.Parse(text))
                {
                    case HelloMessage h:
                        hello = h;
                        break;
                    case ByeMessage bye:
                        throw new IOException($"Il ricevitore ha rifiutato la connessione ({bye.Reason}).");
                }
            }
            if (hello.Protocol != AltroveProtocol.Version)
            {
                await SendTextAsync(socket, new ByeMessage(ByeReasons.Protocol).ToJson(), timeout.Token);
                throw new IOException($"Il ricevitore usa il protocollo {hello.Protocol}, questa app il {AltroveProtocol.Version}: aggiorna l'app più vecchia.");
            }
            return new SenderSession(socket, hello);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The stream size for this receiver: the source fitted within the receiver's decoder limits.</summary>
    public (int Width, int Height) PlanSize(int sourceWidth, int sourceHeight, int? userMaxHeight = null)
    {
        var maxWidth = Receiver.MaxWidth ?? Receiver.Display?.Width ?? 1920;
        var maxHeight = Receiver.MaxHeight ?? Receiver.Display?.Height ?? 1080;
        if (userMaxHeight is > 0)
        {
            // "720p" means at most 1280×720 for a 16:9 screen; a wider screen gets fewer lines instead.
            maxHeight = Math.Min(maxHeight, userMaxHeight.Value);
            maxWidth = Math.Min(maxWidth, (int)Math.Round(userMaxHeight.Value * 16.0 / 9));
        }
        return StreamSizing.Fit(sourceWidth, sourceHeight, maxWidth, maxHeight);
    }

    /// <summary>Raised on a network thread for every touch message (only if the hello announced <see cref="Features.Touch"/>).</summary>
    public event Action<TouchMessage>? TouchReceived;

    /// <summary>Raised on a network thread for every mouse message (only if the hello announced <see cref="Features.Mouse"/>).</summary>
    public event Action<MouseMessage>? MouseReceived;

    /// <summary>The receiver started sending its microphone in this format (feature <see cref="Features.Mic"/>).</summary>
    public event Action<MicStartMessage>? MicStarted;

    public event Action? MicStopped;

    /// <summary>One packet of the receiver's microphone (binary kind 3, header stripped), on the network thread.</summary>
    public event Action<ReadOnlyMemory<byte>>? MicPacketReceived;

    private void OnBinary(byte[] packet)
    {
        if (packet.Length > VideoPacket.HeaderSize && packet[0] == VideoPacket.KindMic)
            MicPacketReceived?.Invoke(packet.AsMemory(VideoPacket.HeaderSize));
    }

    /// <summary>Raised on a network thread when the receiver asks to change settings.</summary>
    public event Action<ConfigureMessage>? ConfigureRequested;

    public async Task StartAsync(IEncodedVideoSource source, EncoderSettings settings, string mode, CancellationToken token)
    {
        if (_sendLoop is not null)
            throw new InvalidOperationException("Already streaming.");
        _statsStart = _clock.ElapsedTicks;
        _sendLoop = Task.Run(SendLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _pingLoop = Task.Run(PingLoopAsync);
        await SwitchSourceAsync(source, settings, mode, token);
    }

    /// <summary>
    /// Streams from <paramref name="source"/> from now on, without closing the connection: the receiver gets a
    /// new start and restarts its decoder. The previous source is let go but not disposed: that is the caller's.
    /// </summary>
    public async Task SwitchSourceAsync(IEncodedVideoSource source, EncoderSettings settings, string mode, CancellationToken token)
    {
        if (_sendLoop is null)
            throw new InvalidOperationException("Not started.");
        DetachSource();
        _source = source;
        source.FrameEncoded += OnFrame;
        source.Failed += OnSourceFailed;
        EnqueueControl(new StartMessage("h264", settings.Width, settings.Height, settings.Fps, mode));
        await source.StartAsync(settings, token);
    }

    /// <summary>True once <see cref="StartAsync"/> ran: later sources go through <see cref="SwitchSourceAsync"/>.</summary>
    public bool IsStarted => _sendLoop is not null;

    /// <summary>
    /// Stops listening to the current source, before the caller disposes it: nothing it does from now on
    /// (frames, errors while shutting down) reaches the receiver. The connection stays open.
    /// </summary>
    public void DetachSource()
    {
        if (_source is { } previous)
        {
            previous.FrameEncoded -= OnFrame;
            previous.Failed -= OnSourceFailed;
        }
        _source = null;
        lock (_lock)
        {
            // Frames of the old stream mean nothing to the new decoder; nothing is sendable before the first IDR.
            _video.Clear();
            _waitingForKeyframe = true;
        }
    }

    /// <summary>True when the receiver can play sound in <paramref name="codec"/> (its hello listed it).</summary>
    public bool ReceiverPlays(string codec) => Receiver.AudioCodecs?.Contains(codec) == true;

    /// <summary>Announces the audio format; packets sent with <see cref="SendAudio"/> follow it.</summary>
    public void StartAudio(AudioStartMessage format) => EnqueueControl(format);

    public void StopAudio()
    {
        lock (_lock)
            _audio.Clear();
        EnqueueControl(new AudioStopMessage());
    }

    /// <summary>Queues one audio packet; if the network is behind, the oldest sound goes first.</summary>
    public void SendAudio(ReadOnlySpan<byte> payload, long presentationMicros)
    {
        if (_closed != 0)
            return;
        var packet = VideoPacket.EncodeAudio(payload, presentationMicros);
        lock (_lock)
        {
            while (_audio.Count >= MaxQueuedAudio)
                _audio.Dequeue();
            _audio.Enqueue(packet);
        }
        _wake.Release();
    }

    /// <summary>Tells the receiver what is streaming and what it may change (feature <see cref="Features.Configure"/>).</summary>
    public void SendSettings(SettingsMessage settings) => EnqueueControl(settings);

    private void OnSourceFailed(Exception error) => _ = CloseAsync($"Errore nella cattura dello schermo: {error.Message}", ByeReasons.Error);

    private void OnFrame(EncodedFrame frame)
    {
        if (_closed != 0)
            return;
        var flags = VideoPacketFlags.None;
        if (frame.IsKeyframe)
        {
            flags |= VideoPacketFlags.Keyframe;
            if (H264.Describe(frame.Data).HasParameterSets)
                flags |= VideoPacketFlags.ParameterSets;
        }
        var packet = VideoPacket.Encode(frame.Data.Span, frame.TimestampMicros, flags);
        var askKeyframe = false;
        lock (_lock)
        {
            if (frame.IsKeyframe)
                _waitingForKeyframe = false;
            if (_waitingForKeyframe)
            {
                _droppedTotal++;
                return;
            }
            if (_video.Count >= MaxQueuedFrames && !frame.IsKeyframe)
            {
                // The network is behind: throw the backlog away and restart from a fresh IDR.
                _droppedTotal += _video.Count + 1;
                _video.Clear();
                _waitingForKeyframe = true;
                askKeyframe = true;
            }
            else
            {
                if (frame.IsKeyframe && _video.Count >= MaxQueuedFrames)
                {
                    _droppedTotal += _video.Count;
                    _video.Clear();
                }
                _video.Enqueue(packet);
            }
        }
        if (askKeyframe)
            _source?.RequestKeyframe();
        _wake.Release();
    }

    private void EnqueueControl(ControlMessage message)
    {
        lock (_lock)
            _control.Enqueue(message.ToJson());
        _wake.Release();
    }

    private async Task SendLoopAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _wake.WaitAsync(token);
                while (true)
                {
                    string? text = null;
                    byte[]? packet = null;
                    var isVideo = false;
                    lock (_lock)
                    {
                        // Control first, then sound (small, and late sound is worse than a late frame), then video.
                        if (_control.Count > 0)
                            text = _control.Dequeue();
                        else if (_audio.Count > 0)
                            packet = _audio.Dequeue();
                        else if (_video.Count > 0)
                        {
                            packet = _video.Dequeue();
                            isVideo = true;
                        }
                    }
                    if (text is not null && ReferenceEquals(text, _finalMessage))
                    {
                        await SendTextAsync(_socket, text, token);
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
                        return;
                    }
                    if (text is not null)
                        await SendTextAsync(_socket, text, token);
                    else if (packet is not null)
                    {
                        await _socket.SendAsync(packet, WebSocketMessageType.Binary, true, token);
                        if (isVideo)
                            Interlocked.Increment(ref _framesSent);
                        Interlocked.Add(ref _bytesSent, packet.Length);
                    }
                    else
                        break;
                }
                ReportStatsIfDue();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            _ = CloseAsync("La connessione con il ricevitore si è interrotta.", null);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var text = await ReceiveTextAsync(_socket, token, OnBinary);
                if (text is null)
                {
                    _ = CloseAsync("Il ricevitore ha chiuso la connessione.", null);
                    return;
                }
                ControlMessage? message;
                try
                {
                    message = ControlMessage.Parse(text);
                }
                catch (JsonException)
                {
                    continue;
                }
                switch (message)
                {
                    case KeyframeMessage:
                        lock (_lock)
                        {
                            _video.Clear();
                            _waitingForKeyframe = true;
                        }
                        _source?.RequestKeyframe();
                        break;
                    case PingMessage ping:
                        EnqueueControl(new PongMessage(ping.T));
                        break;
                    case PongMessage pong:
                        _roundTripMs = _clock.ElapsedMilliseconds - pong.T;
                        break;
                    case StatsMessage stats:
                        _receiverFps = stats.Fps;
                        break;
                    case TouchMessage touch:
                        TouchReceived?.Invoke(touch);
                        break;
                    case MouseMessage mouse:
                        MouseReceived?.Invoke(mouse);
                        break;
                    case ConfigureMessage configure:
                        ConfigureRequested?.Invoke(configure);
                        break;
                    case MicStartMessage micStart:
                        MicStarted?.Invoke(micStart);
                        break;
                    case MicStopMessage:
                        MicStopped?.Invoke();
                        break;
                    case ByeMessage bye:
                        _ = CloseAsync(bye.Reason == ByeReasons.Replaced
                            ? "Un altro dispositivo ha preso il ricevitore."
                            : "Il ricevitore ha chiuso la trasmissione.", null);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            _ = CloseAsync("La connessione con il ricevitore si è interrotta.", null);
        }
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                EnqueueControl(new PingMessage(_clock.ElapsedMilliseconds));
                await Task.Delay(1000, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReportStatsIfDue()
    {
        var now = _clock.ElapsedTicks;
        var elapsed = (now - Interlocked.Read(ref _statsStart)) / (double)Stopwatch.Frequency;
        if (elapsed < 1)
            return;
        _statsStart = now;
        var frames = Interlocked.Exchange(ref _framesSent, 0);
        var bytes = Interlocked.Exchange(ref _bytesSent, 0);
        int queued, dropped;
        lock (_lock)
        {
            queued = _video.Count;
            dropped = _droppedTotal;
        }
        StatsUpdated?.Invoke(new SenderStats(frames / elapsed, bytes * 8 / elapsed / 1_000_000, _roundTripMs, _receiverFps, dropped, queued));
    }

    /// <summary>Ends the session, telling the receiver why if it is still listening.</summary>
    public Task StopAsync() => CloseAsync("Trasmissione fermata.", ByeReasons.User);

    private async Task CloseAsync(string why, string? byeReason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        if (_source is not null)
        {
            _source.FrameEncoded -= OnFrame;
            _source.Failed -= OnSourceFailed;
        }
        if (byeReason is not null && _socket.State == WebSocketState.Open)
        {
            // The send loop owns the socket's sending side: hand it the goodbye as its last message.
            var bye = new ByeMessage(byeReason).ToJson();
            if (_sendLoop is { IsCompleted: false })
            {
                lock (_lock)
                {
                    _video.Clear();
                    _control.Clear();
                    _audio.Clear();
                    _finalMessage = bye;
                    _control.Enqueue(bye);
                }
                _wake.Release();
                try
                {
                    await _sendLoop.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (TimeoutException)
                {
                }
            }
            else
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await SendTextAsync(_socket, bye, timeout.Token);
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
            }
        }
        _stop.Cancel();
        Closed?.Invoke(why);
    }

    private static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token);

    /// <summary>The next text message; binary messages are skipped (receivers send none in v1). Null when the socket closes.</summary>
    /// <summary>The next text message; binary ones go to <paramref name="onBinary"/> (the receiver's microphone), or are skipped. Null when the socket closes.</summary>
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken token, Action<byte[]>? onBinary = null)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 1024 * 1024)
                throw new IOException("Control message too large.");
            if (!result.EndOfMessage)
                continue;
            if (result.MessageType == WebSocketMessageType.Text)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            onBinary?.Invoke(message.ToArray());
            message.SetLength(0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("Trasmissione fermata.", ByeReasons.User);
        foreach (var loop in new[] { _sendLoop, _receiveLoop, _pingLoop })
        {
            if (loop is null)
                continue;
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
        _socket.Dispose();
        _stop.Dispose();
        _wake.Dispose();
    }
}
