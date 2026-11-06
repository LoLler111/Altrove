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
        "vdd-install" => VddInstall(args),
        "vdd-uninstall" => VddUninstall(args),
        "vdd" => Vdd(options),
        "extend" => await Extend(options),
        "vdd-detach" => VddDetach(),
        "touch" => await Touch(options, args.Contains("--seconds")),
        "scale" => Scale(options),
        "audio" => await AudioTest(options),
        "mic-selftest" => MicSelfTest(),
        "mic-tone" => await MicTone(options),
        "stress" => await Stress(options),
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

static async Task<int> Touch(Options options, bool hold)
{
    // A press of --seconds (default 0: a tap) at x,y from 0 to 1 on the chosen monitor, as a receiver would send it.
    var monitor = PickMonitor(options);
    var xy = (options.Positional ?? "0.5,0.5").Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    using var touch = new Altrove.Capture.Input.TouchInjector(monitor);
    var origin = Altrove.Capture.Input.TouchInjector.VirtualScreenOrigin;
    Console.WriteLine($"Schermo a {monitor.Left},{monitor.Top} ({monitor.Width}×{monitor.Height}); il desktop inizia a {origin.X},{origin.Y}");
    Console.WriteLine($"Tocco su {monitor.DisplayName} a x {xy[0]:0.###}, y {xy[1]:0.###}" + (hold ? $" per {options.Seconds} s" : ""));
    touch.Inject(new TouchMessage([new TouchPointer(0, xy[0], xy[1], TouchStates.Down)]));
    await Task.Delay(TimeSpan.FromSeconds(hold ? Math.Max(options.Seconds, 0) : 0) + TimeSpan.FromMilliseconds(60));
    touch.Inject(new TouchMessage([new TouchPointer(0, xy[0], xy[1], TouchStates.Up)]));
    Console.WriteLine(touch.Failures == 0 ? "Windows ha accettato il tocco." : $"Windows ha rifiutato {touch.Failures} invii (finestra con permessi di amministratore in primo piano?).");
    return touch.Failures == 0 ? 0 : 1;
}

static async Task<int> Stress(Options options)
{
    // Pieces of the pipeline alone, for --seconds: "capture", "convert" (capture + NV12) or "encode" (made-up frames).
    var what = options.Positional ?? "capture";
    var monitor = PickMonitor(options);
    using var device = GraphicsDevice.CreateFor(monitor);
    var (w, h) = (monitor.Width & ~1, monitor.Height & ~1);
    long frames = 0, encoded = 0;
    INv12Converter? converter = what == "convert" ? Nv12Converters.Create(device, w, h) : null;
    H264Encoder? encoder = null;
    ScreenCapture? capture = null;
    if (what == "encode")
    {
        encoder = H264Encoder.Create(device, new EncoderSettings(w, h, options.Fps, options.Kbps), false, options.Software);
        encoder.FrameEncoded += _ => Interlocked.Increment(ref encoded);
        encoder.Failed += ex => Console.Error.WriteLine($"Errore encoder: {ex.Message}");
        encoder.Start();
    }
    else
    {
        capture = new ScreenCapture(device, monitor);
        capture.FrameArrived += (texture, _) =>
        {
            Interlocked.Increment(ref frames);
            if (converter is not null)
            {
                var d = texture.Description;
                converter.Convert(texture, (int)d.Width, (int)d.Height);
            }
        };
        capture.Start();
    }
    Console.WriteLine($"Prova {what} per {options.Seconds} s su {monitor.DisplayName}{(converter is null ? "" : ", conversione " + converter.Name)}{(encoder is null ? "" : ", " + encoder.Name)}");
    var ring = Enumerable.Range(0, 3).Select(_ => new Nv12Frame { Width = w, Height = h, Bytes = new byte[w * h * 3 / 2] }).ToArray();
    var clock = Stopwatch.StartNew();
    var next = TimeSpan.Zero;
    var n = 0;
    while (clock.Elapsed < TimeSpan.FromSeconds(options.Seconds))
    {
        if (encoder is not null)
        {
            var frame = ring[n % 3];
            Random.Shared.NextBytes(frame.Bytes!.AsSpan(0, w * 64));
            Array.Fill(frame.Bytes!, (byte)(n * 5), w * 64, w * 32);
            encoder.Submit(frame, clock.Elapsed.Ticks);
            n++;
            frames = n;
            next += TimeSpan.FromSeconds(1.0 / options.Fps);
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
        }
        else
            await Task.Delay(1000);
        if ((int)clock.Elapsed.TotalSeconds % 10 == 0 && clock.Elapsed.TotalSeconds >= 1 && encoder is null)
            Console.WriteLine($"  {clock.Elapsed.TotalSeconds:0} s: {frames} fotogrammi");
    }
    capture?.Dispose();
    encoder?.Dispose();
    converter?.Dispose();
    Console.WriteLine($"Fine: {frames} fotogrammi, {encoded} codificati");
    return 0;
}

static int Scale(Options options)
{
    // altrove-cli scale [--monitor N] reads the Windows scale of a monitor; altrove-cli scale 150 sets it.
    var monitor = PickMonitor(options);
    if (options.Positional is { } wanted)
    {
        var ok = Altrove.Capture.VirtualDisplay.DisplayScale.Set(monitor.DeviceName, int.Parse(wanted));
        Console.WriteLine(ok ? $"Scala di {monitor.DisplayName}: {wanted}%" : "Windows non ha accettato la scala.");
        if (!ok)
            return 1;
    }
    if (Altrove.Capture.VirtualDisplay.DisplayScale.Get(monitor.DeviceName) is not { } scale)
    {
        Console.WriteLine($"Scala di {monitor.DisplayName} non leggibile.");
        return 1;
    }
    Console.WriteLine($"{monitor.DisplayName} ({monitor.DeviceName}): {scale.Current}%, possibili {string.Join(", ", scale.Available)}");
    return 0;
}

static async Task<int> AudioTest(Options options)
{
    // altrove-cli audio [pcm] --seconds 5: captures what the PC plays as the receiver would get it, decodes it back
    // and saves altrove-audio.wav to listen to.
    var codec = options.Positional ?? Altrove.Core.Protocol.AudioCodecs.Opus;
    using var audio = new Altrove.Capture.Audio.LoopbackAudio(codec);
    var packets = new List<byte[]>();
    audio.PacketReady += (packet, _) => { lock (packets) packets.Add(packet); };
    audio.Failed += ex => Console.Error.WriteLine($"Errore audio: {ex.Message}");
    audio.Start();
    Console.WriteLine($"Registro l'audio di «{audio.DeviceName}» per {options.Seconds} s ({codec})…");
    await Task.Delay(TimeSpan.FromSeconds(options.Seconds));
    audio.Dispose();
    int atStop;
    lock (packets)
        atStop = packets.Count;
    await Task.Delay(1500); // nothing may come after the stop

    byte[][] copy;
    lock (packets)
        copy = packets.ToArray();
    if (copy.Length > atStop)
        Console.WriteLine($"ATTENZIONE: {copy.Length - atStop} pacchetti arrivati dopo lo stop");
    var bytes = copy.Sum(p => (long)p.Length);
    Console.WriteLine($"Pacchetti: {copy.Length} ({copy.Length * 10 / 1000.0:0.0} s di suono), {bytes / 1024} KiB, {bytes * 8 / 1000.0 / Math.Max(1, options.Seconds):0} kbit/s");
    if (copy.Length == 0)
    {
        Console.WriteLine("Nessun suono: fai suonare qualcosa sul PC durante la prova.");
        return 1;
    }
    var pcm = new List<short>();
    var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 2);
    var buffer = new short[960 * 2];
    foreach (var packet in copy)
    {
        if (codec == Altrove.Core.Protocol.AudioCodecs.Opus)
        {
            var samples = decoder.Decode(packet, buffer, 960);
            pcm.AddRange(buffer.AsSpan(0, samples * 2).ToArray());
        }
        else
        {
            for (var i = 0; i + 1 < packet.Length; i += 2)
                pcm.Add(BitConverter.ToInt16(packet, i));
        }
    }
    var rms = Math.Sqrt(pcm.Average(s => (double)s * s)) / 32768;
    Console.WriteLine($"Ridecodificati {pcm.Count / 2} campioni per canale, livello medio {20 * Math.Log10(Math.Max(rms, 1e-9)):0.0} dBFS");
    using (var writer = new NAudio.Wave.WaveFileWriter("altrove-audio.wav", new NAudio.Wave.WaveFormat(48000, 16, 2)))
    {
        var raw = new byte[pcm.Count * 2];
        Buffer.BlockCopy(pcm.ToArray(), 0, raw, 0, raw.Length);
        writer.Write(raw, 0, raw.Length);
    }
    Console.WriteLine($"Salvato {Path.GetFullPath("altrove-audio.wav")}");
    return 0;
}

static int MicSelfTest()
{
    // Plays usbip-win2's part against Altrove's USB/IP microphone: enumerate, start streaming, read 50 capture URBs
    // of 10 ms, and check that a 440 Hz tone comes out on time. Needs nothing installed.
    using UsbIpMicServerHarness harness = new();
    return harness.Run();
}

static async Task<int> MicTone(Options options)
{
    // "Microfono Altrove" for real, with a 440 Hz tone instead of the tablet: attaches it through usbip-win2 and
    // lists Windows' microphones. Record from it with any app during --seconds.
    if (!Altrove.Capture.Microphone.UsbIpSetup.IsInstalled)
    {
        Console.WriteLine("usbip-win2 non è installato.");
        return 1;
    }
    using var bridge = new Altrove.Capture.Microphone.MicBridge();
    bridge.Begin(new MicStartMessage(Altrove.Core.Protocol.AudioCodecs.Pcm, 48_000, 1, 10));
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
    var clock = Stopwatch.StartNew();
    var block = new byte[960];
    var n = 0;
    var shown = false;
    for (var i = 0; !stop.IsCancellationRequested; i++)
    {
        for (var k = 0; k < 480; k++, n++)
        {
            var s = (short)(0.3 * 32767 * Math.Sin(2 * Math.PI * 440 * n / 48_000.0));
            block[k * 2] = (byte)s;
            block[k * 2 + 1] = (byte)(s >> 8);
        }
        bridge.Push(block);
        if (!shown && bridge.Attached && clock.Elapsed > TimeSpan.FromSeconds(3))
        {
            shown = true;
            Console.WriteLine("Collegato a Windows. Microfoni presenti:");
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in devices.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
                Console.WriteLine($"  {d.FriendlyName}");
        }
        var wait = TimeSpan.FromMilliseconds(10 * (i + 1)) - clock.Elapsed;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);
        if (i % 500 == 0)
            Console.WriteLine($"  {clock.Elapsed.TotalSeconds:0} s: collegato {bridge.Attached}, un'app registra {bridge.Recording}{(bridge.AttachError is { } e ? " · " + e : "")}");
    }
    return bridge.Attached ? 0 : 1;
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

// Puts the virtual monitor on the desktop at a receiver's size (default: the Tab A's 1920×1200),
// records it like "record", then takes it off again.
static async Task<int> Extend(Options options)
{
    if (!Altrove.Capture.VirtualDisplay.VirtualMonitor.IsDriverInstalled)
    {
        Console.Error.WriteLine("Driver dello schermo virtuale non installato: altrove-cli vdd-install");
        return 1;
    }
    var size = (options.Display ?? "1920x1200").Split('x', '×');
    var mode = Altrove.Capture.VirtualDisplay.VirtualMonitor.PlanMode(int.Parse(size[0]), int.Parse(size[1]), 3840, 2160, null, options.Fps);
    Console.WriteLine($"Attacco lo schermo virtuale a {mode.Width}×{mode.Height} @ {mode.RefreshRate} Hz…");
    var monitor = await Altrove.Capture.VirtualDisplay.VirtualMonitor.AttachAsync(mode, CancellationToken.None);
    try
    {
        Console.WriteLine($"Comparso: {monitor.DisplayName} ({monitor.DeviceName}) a {monitor.Left},{monitor.Top}");
        // Something must change on it, or capture delivers a single frame: Record asks for keyframes.
        return await Record(options with { Height = mode.Height }, monitor);
    }
    finally
    {
        Altrove.Capture.VirtualDisplay.VirtualMonitor.Detach();
        Console.WriteLine("Schermo virtuale staccato.");
    }
}

static async Task<int> Record(Options options, MonitorInfo? chosen = null)
{
    var monitor = chosen ?? PickMonitor(options);
    var (width, height) = Altrove.Core.Video.StreamSizing.Fit(monitor.Width, monitor.Height, options.Height * 16 / 9, options.Height);
    if (chosen is not null)
        (width, height) = (monitor.Width & ~1, monitor.Height & ~1);
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

// Virtual display driver: install (asks for administrator rights), remove, or send it a raw command.
static int VddInstall(string[] args)
{
    if (!Altrove.Capture.VirtualDisplay.DriverInstaller.IsAdministrator)
        return RunElevated(args);
    var inf = Path.Combine(AppContext.BaseDirectory, "VirtualDisplayDriver", "MttVDD.inf");
    var settings = Altrove.Capture.VirtualDisplay.VddControl.BuildSettings(1, Altrove.Capture.VirtualDisplay.VddControl.StandardModes);
    Altrove.Capture.VirtualDisplay.DriverInstaller.Install(inf, settings);
    Console.WriteLine($"Driver installato; impostazioni in {Altrove.Capture.VirtualDisplay.VddControl.SettingsFile}");
    return 0;
}

static int VddUninstall(string[] args)
{
    if (!Altrove.Capture.VirtualDisplay.DriverInstaller.IsAdministrator)
        return RunElevated(args);
    Altrove.Capture.VirtualDisplay.DriverInstaller.Uninstall();
    Console.WriteLine("Driver rimosso.");
    return 0;
}

// Runs this same command again as administrator, its output going to a log next to the exe.
static int RunElevated(string[] args)
{
    var log = Path.Combine(Path.GetTempPath(), "altrove-elevated.log");
    var start = new ProcessStartInfo("cmd.exe", $"/c \"\"{Environment.ProcessPath}\" {string.Join(' ', args)} > \"{log}\" 2>&1\"")
    {
        Verb = "runas",
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    };
    using var process = Process.Start(start)!;
    process.WaitForExit();
    Console.Write(File.Exists(log) ? File.ReadAllText(log) : "");
    return process.ExitCode;
}

static int VddDetach()
{
    Altrove.Capture.VirtualDisplay.VirtualMonitor.Detach();
    Console.WriteLine("Schermo virtuale staccato (se era attaccato).");
    return 0;
}

static int Vdd(Options options)
{
    foreach (var device in Altrove.Capture.VirtualDisplay.DisplaySettings.List())
        Console.WriteLine($"  {device.Name} | {device.AdapterString} | {device.MonitorId} | sul desktop: {device.AttachedToDesktop}");
    var installed = Altrove.Capture.VirtualDisplay.DriverInstaller.IsInstalled();
    Console.WriteLine($"Driver installato: {installed}");
    var command = options.Positional ?? "PING";
    var reply = Altrove.Capture.VirtualDisplay.VddControl.Send(command, TimeSpan.FromSeconds(5));
    Console.WriteLine(reply is null ? "Il driver non risponde sulla pipe." : $"Risposta a {command}: {reply}");
    return reply is null ? 1 : 0;
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

    // Decode it too, cutting access units at each access unit delimiter (NAL type 9).
    var starts = new List<int>();
    for (var i = 0; i + 3 < data.Length; i++)
    {
        if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1F) == 9)
            starts.Add(i > 0 && data[i - 1] == 0 ? i - 1 : i);
    }
    if (starts.Count == 0)
        starts.Add(0);
    var units = starts.Select((start, n) => data[start..(n + 1 < starts.Count ? starts[n + 1] : data.Length)]).ToList();
    var snapshot = Path.ChangeExtension(options.Positional ?? options.Out, ".bmp");
    var result = H264Verifier.Decode(units, snapshot);
    Console.WriteLine($"Verifica con {result.Decoder}: decodificati {result.FramesDecoded}/{units.Count}, {result.Width}×{result.Height}");
    Console.WriteLine($"Primo fotogramma decodificato: {snapshot}");
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

internal sealed record Options(int Seconds, int Fps, int Height, int Kbps, bool Software, int Monitor, string Out, int Port, string? Positional, string? Display = null)
{
    public static Options Parse(string[] args)
    {
        int seconds = 5, fps = 60, height = 1080, kbps = 10_000, monitor = 1, port = AltroveProtocol.DefaultPort;
        var software = false;
        var output = "altrove-test.h264";
        string? positional = null, display = null;
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
                case "--display": display = Next(); break;
                default: positional = args[i]; break;
            }
        }
        return new Options(seconds, fps, height, kbps, software, monitor, output, port, positional, display);
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
