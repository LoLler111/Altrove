using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using Altrove.Capture;
using Altrove.Core.Discovery;
using Altrove.Core.Protocol;
using Altrove.Core.Streaming;

// Test bench for the Windows side without the UI:
//   altrove-cli list                         monitors, GPUs and H.264 encoders
//   altrove-cli record [opts]                capture + encode for a few seconds, save .h264, decode it back
//   altrove-cli discover                     receivers announcing _altrove._tcp
//   altrove-cli send <host[:port]> [opts]    stream to a receiver
//   altrove-cli fake-receiver [--port N]     a receiver on localhost that counts what arrives
// Options: --seconds N  --fps N  --height N  --kbps N  --software  --monitor N  --out file.h264

SetProcessDpiAwarenessContext(-4); // per-monitor v2: real pixel sizes
Console.OutputEncoding = Encoding.UTF8;

var command = args.FirstOrDefault() ?? "list";
var options = Options.Parse(args.Skip(1).ToArray());
try
{
    return command switch
    {
        "list" => List(),
        "record" => await Record(options),
        "discover" => await Discover(options),
        "send" => await Send(options),
        "fake-receiver" => await FakeReceiver(options),
        "announce" => await Announce(options),
        "analyze" => Analyze(options),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Errore: {ex}");
    return 1;
}

static int Usage()
{
    Console.WriteLine("Comandi: list | record | discover | send <host[:port]> | fake-receiver");
    return 2;
}

static int List()
{
    Console.WriteLine("Schermi:");
    foreach (var monitor in Monitors.List())
        Console.WriteLine($"  {monitor.Index}. {monitor.DisplayName} ({monitor.DeviceName}) a {monitor.Left},{monitor.Top}");
    Console.WriteLine("Encoder H.264:");
    foreach (var encoder in H264Encoder.List())
        Console.WriteLine($"  {encoder.Name} {encoder.VendorId}");
    return 0;
}

static MonitorInfo PickMonitor(Options options)
{
    var monitors = Monitors.List();
    return monitors.FirstOrDefault(m => m.Index == options.Monitor) ?? monitors[0];
}

static async Task<int> Record(Options options)
{
    var monitor = PickMonitor(options);
    var (width, height) = Altrove.Core.Video.StreamSizing.Fit(monitor.Width, monitor.Height, options.Height * 16 / 9, options.Height);
    var settings = new EncoderSettings(width, height, options.Fps, options.Kbps);
    Console.WriteLine($"Registro {monitor.DisplayName} → {width}×{height} @ {options.Fps} fps, {options.Kbps} kbps, {options.Seconds} s");

    var frames = new List<EncodedFrame>();
    var firstAt = -1L;
    var clock = Stopwatch.StartNew();
    await using var source = new ScreenVideoSource(monitor, options.Software);
    source.Failed += ex => Console.Error.WriteLine($"Errore encoder: {ex.Message}");
    source.FrameEncoded += frame =>
    {
        lock (frames)
        {
            if (firstAt < 0)
                firstAt = clock.ElapsedMilliseconds;
            frames.Add(frame);
        }
    };
    await source.StartAsync(settings, CancellationToken.None);
    Console.WriteLine($"Encoder: {source.EncoderName}");

    // Something must move on screen or capture delivers nothing: ask for keyframes now and then.
    var end = clock.Elapsed + TimeSpan.FromSeconds(options.Seconds);
    while (clock.Elapsed < end)
    {
        await Task.Delay(500);
        source.RequestKeyframe();
    }
    Console.WriteLine($"Catturati {source.Counters.Captured}, inviati all'encoder {source.Counters.Submitted}");
    await source.DisposeAsync();

    EncodedFrame[] copy;
    lock (frames)
        copy = frames.ToArray();
    Console.WriteLine($"Primo fotogramma dopo {firstAt} ms; fotogrammi: {copy.Length}, IDR: {copy.Count(f => f.IsKeyframe)}, " +
        $"dimensione: {copy.Sum(f => f.Data.Length) / 1024} KiB");
    var total = copy.Sum(f => (long)f.Data.Length);
    var filler = copy.Sum(f => (long)Altrove.Core.Video.H264.FillerBytes(f.Data));
    Console.WriteLine($"Bitrate medio {total * 8.0 / options.Seconds / 1e6:0.0} Mbit/s; riempitivo {filler / 1024} KiB ({(total > 0 ? filler * 100.0 / total : 0):0}%)");
    if (copy.Length == 0)
        return 1;

    await using (var file = File.Create(options.Out))
    {
        foreach (var frame in copy)
            file.Write(frame.Data.Span);
    }
    Console.WriteLine($"Salvato in {Path.GetFullPath(options.Out)}");

    var snapshot = Path.ChangeExtension(Path.GetFullPath(options.Out), ".bmp");
    var verify = H264Verifier.Decode(copy.Select(f => f.Data.ToArray()), snapshot);
    Console.WriteLine($"Primo fotogramma decodificato: {snapshot}");
    Console.WriteLine($"Verifica con {verify.Decoder}: decodificati {verify.FramesDecoded}/{verify.FramesIn}, {verify.Width}×{verify.Height}");
    return verify.FramesDecoded > 0 && verify.Width == width ? 0 : 1;
}

// What a saved .h264 contains: NAL units by type and how much of it is filler.
static int Analyze(Options options)
{
    var data = File.ReadAllBytes(options.Positional ?? options.Out);
    var counts = Altrove.Core.Video.H264.NalUnits(data)
        .GroupBy(n => Altrove.Core.Video.H264.NalType(n.Span))
        .OrderBy(g => g.Key)
        .Select(g => $"{g.Key}={g.Count()}");
    var filler = Altrove.Core.Video.H264.FillerBytes(data);
    Console.WriteLine($"{data.Length / 1024} KiB; NAL per tipo: {string.Join(", ", counts)}");
    Console.WriteLine($"Riempitivo: {filler / 1024} KiB ({filler * 100.0 / data.Length:0}%)");
    return 0;
}

static async Task<int> Discover(Options options)
{
    using var browser = new MdnsBrowser();
    browser.Changed += list =>
    {
        Console.WriteLine($"[{DateTime.Now:T}] {list.Count} ricevitori:");
        foreach (var receiver in list)
            Console.WriteLine($"  {receiver.Name} – {receiver.Address} (id {receiver.Id}, protocollo {receiver.Protocol})");
    };
    browser.Start();
    Console.WriteLine($"Cerco ricevitori per {options.Seconds} s…");
    await Task.Delay(TimeSpan.FromSeconds(options.Seconds));
    Console.WriteLine($"Pacchetti mDNS ricevuti: {browser.PacketsReceived}");
    return browser.Receivers.Count > 0 ? 0 : 1;
}

static async Task<int> Send(Options options)
{
    if (!ReceiverAddress.TryParse(options.Positional, out var host, out var port))
    {
        Console.Error.WriteLine("Indica il ricevitore: send 192.168.1.20[:47800]");
        return 2;
    }
    var monitor = PickMonitor(options);
    var self = new HelloMessage(AltroveProtocol.Version, Roles.Sender, MachineId(), Environment.MachineName, "cli");
    await using var session = await SenderSession.ConnectAsync(ReceiverAddress.ToUri(host, port), self, CancellationToken.None);
    Console.WriteLine($"Collegato a {session.Receiver.Name} (schermo {session.Receiver.Display?.Width}×{session.Receiver.Display?.Height})");
    var (width, height) = session.PlanSize(monitor.Width, monitor.Height, options.Height);
    var settings = new EncoderSettings(width, height, options.Fps, options.Kbps);
    var closed = new TaskCompletionSource<string>();
    session.Closed += why => closed.TrySetResult(why);
    session.StatsUpdated += s => Console.WriteLine(
        $"  {s.SentFps:0.0} fps inviati, {s.Mbps:0.0} Mbit/s, RTT {s.RoundTripMs?.ToString("0") ?? "-"} ms, ricevitore {s.ReceiverFps?.ToString("0.0") ?? "-"} fps, scartati {s.DroppedFrames}");

    await using var source = new ScreenVideoSource(monitor, options.Software);
    await session.StartAsync(source, settings, StreamModes.Mirror, CancellationToken.None);
    Console.WriteLine($"Trasmetto {width}×{height} @ {options.Fps} con {source.EncoderName}");
    var finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(options.Seconds)));
    if (finished != closed.Task)
        await session.StopAsync();
    Console.WriteLine(await closed.Task);
    return 0;
}

static async Task<int> FakeReceiver(Options options)
{
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{options.Port}/");
    listener.Start();
    Console.WriteLine($"Ricevitore finto su ws://localhost:{options.Port}/ (Ctrl+C per uscire)");
    while (true)
    {
        var context = await listener.GetContextAsync();
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            continue;
        }
        var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var hello = new HelloMessage(AltroveProtocol.Version, Roles.Receiver, "fake", "Ricevitore finto", "cli", new DisplayInfo(1920, 1080, 60), ["h264"], 1920, 1080);
        await ws.SendAsync(Encoding.UTF8.GetBytes(hello.ToJson()), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4 * 1024 * 1024];
        int frames = 0, keyframes = 0;
        long bytes = 0;
        var second = Stopwatch.StartNew();
        await using var dump = File.Create(options.Out);
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var offset = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(offset), CancellationToken.None);
                    offset += result.Count;
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = ControlMessage.Parse(Encoding.UTF8.GetString(buffer, 0, offset));
                    if (message is PingMessage ping)
                        await ws.SendAsync(Encoding.UTF8.GetBytes(new PongMessage(ping.T).ToJson()), WebSocketMessageType.Text, true, CancellationToken.None);
                    else
                        Console.WriteLine($"  ← {message?.GetType().Name}: {Encoding.UTF8.GetString(buffer, 0, Math.Min(offset, 200))}");
                    continue;
                }
                if (VideoPacket.TryDecode(buffer.AsMemory(0, offset), out var flags, out _, out var unit))
                {
                    frames++;
                    bytes += offset;
                    if (flags.HasFlag(VideoPacketFlags.Keyframe))
                        keyframes++;
                    dump.Write(unit.Span);
                }
                if (second.ElapsedMilliseconds >= 1000)
                {
                    Console.WriteLine($"  {frames} fotogrammi/s ({keyframes} IDR), {bytes * 8 / 1e6:0.0} Mbit/s");
                    await ws.SendAsync(Encoding.UTF8.GetBytes(new StatsMessage(frames).ToJson()), WebSocketMessageType.Text, true, CancellationToken.None);
                    frames = keyframes = 0;
                    bytes = 0;
                    second.Restart();
                }
            }
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"  connessione chiusa: {ex.Message}");
        }
        Console.WriteLine($"Trasmettitore scollegato; flusso salvato in {Path.GetFullPath(options.Out)}");
    }
}

// Has Windows' own mDNS responder announce a receiver, to test discovery without an Android device.
static async Task<int> Announce(Options options)
{
    var host = Environment.MachineName + ".local";
    var keys = new[] { "id", "name", "protocol" };
    var values = new[] { "announce-test", "Ricevitore di prova", "1" };
    var instance = Dns.DnsServiceConstructInstance($"Prova Altrove.{AltroveProtocol.ServiceType}.local", host, IntPtr.Zero, IntPtr.Zero,
        (ushort)options.Port, 0, 0, (uint)keys.Length, keys, values);
    if (instance == IntPtr.Zero)
        throw new InvalidOperationException("DnsServiceConstructInstance failed");
    var done = new TaskCompletionSource<uint>();
    Dns.RegisterComplete callback = (status, _, _) => done.TrySetResult(status);
    var request = new Dns.RegisterRequest { Version = 1, ServiceInstance = instance, Callback = Marshal.GetFunctionPointerForDelegate(callback) };
    var result = Dns.DnsServiceRegister(ref request, IntPtr.Zero);
    Console.WriteLine($"DnsServiceRegister: {result} (9506 = in corso)");
    Console.WriteLine($"Registrazione: {await done.Task.WaitAsync(TimeSpan.FromSeconds(10))}; annuncio per {options.Seconds} s");
    await Task.Delay(TimeSpan.FromSeconds(options.Seconds));
    Dns.DnsServiceDeRegister(ref request, IntPtr.Zero);
    GC.KeepAlive(callback);
    return 0;
}

static string MachineId() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..16].ToLowerInvariant();

[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(nint value);

internal sealed record Options(int Seconds, int Fps, int Height, int Kbps, bool Software, int Monitor, string Out, int Port, string? Positional)
{
    public static Options Parse(string[] args)
    {
        int seconds = 5, fps = 60, height = 1080, kbps = 10_000, monitor = 1, port = AltroveProtocol.DefaultPort;
        var software = false;
        var output = "altrove-test.h264";
        string? positional = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} vuole un valore");
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(Next()); break;
                case "--fps": fps = int.Parse(Next()); break;
                case "--height": height = int.Parse(Next()); break;
                case "--kbps": kbps = int.Parse(Next()); break;
                case "--software": software = true; break;
                case "--monitor": monitor = int.Parse(Next()); break;
                case "--out": output = Next(); break;
                case "--port": port = int.Parse(Next()); break;
                default: positional = args[i]; break;
            }
        }
        return new Options(seconds, fps, height, kbps, software, monitor, output, port, positional);
    }
}

internal static class Dns
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    public struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr Callback;
        public IntPtr Context;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertiesCount, string[] keys, string[] values);

    [DllImport("dnsapi.dll")]
    public static extern uint DnsServiceRegister(ref RegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    public static extern uint DnsServiceDeRegister(ref RegisterRequest request, IntPtr cancel);
}
