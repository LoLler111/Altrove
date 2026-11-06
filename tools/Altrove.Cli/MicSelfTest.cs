using System.Diagnostics;
using System.Net.Sockets;
using Altrove.Capture.Microphone;
using Altrove.Core.UsbMic;

/// <summary>usbip-win2's side of the USB/IP conversation, to check Altrove's microphone without installing anything.</summary>
internal sealed class UsbIpMicServerHarness : IDisposable
{
    private readonly MicJitterBuffer _jitter = new();
    private readonly UsbIpMicServer _server;
    private uint _sequence;

    public UsbIpMicServerHarness() => _server = new UsbIpMicServer(_jitter) { Feeding = true };

    public int Run()
    {
        // A 440 Hz tone at half scale, 10 ms at a time, as the tablet would send it.
        using var feeding = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            var n = 0;
            var block = new float[480];
            var clock = Stopwatch.StartNew();
            for (var i = 0; !feeding.IsCancellationRequested; i++)
            {
                for (var k = 0; k < block.Length; k++, n++)
                    block[k] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 48_000f);
                _jitter.Push(block, 48_000);
                var wait = TimeSpan.FromMilliseconds(10 * (i + 1)) - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);
            }
        });
        Thread.Sleep(100);

        using (var list = Connect())
        {
            var request = new List<byte>();
            UsbIp.AppendOpHeader(request, UsbIp.OpReqDevlist, 0);
            list.Write([.. request]);
            var header = new byte[12];
            list.ReadExactly(header);
            Console.WriteLine($"Elenco dei dispositivi: {(header[11] == 1 ? "un microfono" : "vuoto")}");
        }

        using var stream = Connect();
        var import = new List<byte>();
        UsbIp.AppendOpHeader(import, UsbIp.OpReqImport, 0);
        var bus = new byte[32];
        "1-1"u8.CopyTo(bus);
        import.AddRange(bus);
        stream.Write([.. import]);
        var reply = new byte[8 + 312];
        stream.ReadExactly(reply);
        if (reply[7] != 0)
        {
            Console.WriteLine("Il microfono ha rifiutato l'importazione.");
            return 1;
        }

        var device = Control(stream, 0x80, 0x06, 0x0100, 0, 18);
        Console.WriteLine($"Descrittore: {device.Length} byte, vendor {device[9]:X2}{device[8]:X2}, product {device[11]:X2}{device[10]:X2}");
        var name = Control(stream, 0x80, 0x06, 0x0302, 0x0409, 255);
        Console.WriteLine($"Nome: {System.Text.Encoding.Unicode.GetString(name, 2, name.Length - 2)}");
        Control(stream, 0x00, 0x09, 1, 0, 0);   // SET_CONFIGURATION 1
        Control(stream, 0x01, 0x0B, 1, 1, 0);   // SET_INTERFACE 1, alternate 1: streaming
        Console.WriteLine($"In registrazione: {_server.Streaming}");

        // Windows keeps a few URBs queued: send 3 ahead, then one more per completion.
        const int urbs = 50, packets = 10;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
            SubmitCapture(stream, packets);
        double sumSquares = 0;
        long samples = 0;
        var frames = new List<uint>();
        for (var i = 0; i < urbs; i++)
        {
            var (status, startFrame, data) = ReadIsoReply(stream, packets);
            if (status != 0)
            {
                Console.WriteLine($"Errore USB {status}");
                return 1;
            }
            frames.Add(startFrame);
            for (var at = i < 10 ? data.Length : 0; at + 2 < data.Length; at += Uac2Microphone.FrameBytes)
            {
                var value = (data[at] | data[at + 1] << 8 | (sbyte)data[at + 2] << 16) / 8_388_607.0;
                sumSquares += value * value;
                samples++;
            }
            if (i + 3 < urbs)
                SubmitCapture(stream, packets);
        }
        var elapsed = clock.Elapsed.TotalMilliseconds;
        feeding.Cancel();
        feeder.Wait();
        // The first blocks wait for the jitter buffer to fill up: measure the steady part.
        var rms = Math.Sqrt(sumSquares / Math.Max(1, samples));
        var steady = frames.Zip(frames.Skip(1), (a, b) => b - a).All(d => d == packets);
        Console.WriteLine($"{urbs} blocchi da {packets} ms in {elapsed:0} ms (attesi ~{urbs * packets}); fotogrammi USB consecutivi: {(steady ? "sì" : "no")}");
        Console.WriteLine($"Livello: {20 * Math.Log10(Math.Max(rms, 1e-9)):0.0} dBFS (tono a -9 dBFS di media)");
        var ok = steady && rms > 0.2 && elapsed > urbs * packets * 0.8 && elapsed < urbs * packets * 1.5;
        Console.WriteLine(ok ? "Microfono USB/IP: funziona." : "Microfono USB/IP: qualcosa non va.");
        return ok ? 0 : 1;
    }

    private static NetworkStream Connect()
    {
        var client = new TcpClient();
        client.Connect("127.0.0.1", UsbIpMicServer.TcpPort);
        client.NoDelay = true;
        return client.GetStream();
    }

    private byte[] Control(NetworkStream s, byte type, byte request, ushort value, ushort index, ushort length)
    {
        var b = new List<byte>();
        var direction = (type & 0x80) != 0 ? UsbIp.DirectionIn : UsbIp.DirectionOut;
        foreach (var v in new uint[] { UsbIp.CmdSubmit, ++_sequence, 0x0001_0001, direction, 0, 0, length, 0, UsbIp.NoIsoPackets, 0 })
            UsbIp.AppendBe32(b, v);
        b.AddRange([type, request, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), (byte)length, (byte)(length >> 8)]);
        s.Write([.. b]);
        var header = new byte[48];
        s.ReadExactly(header);
        var actual = (int)(header[24] << 24 | header[25] << 16 | header[26] << 8 | header[27]);
        var data = new byte[direction == UsbIp.DirectionIn ? actual : 0];
        s.ReadExactly(data);
        return data;
    }

    private void SubmitCapture(NetworkStream s, int packets)
    {
        var b = new List<byte>();
        foreach (var v in new uint[] { UsbIp.CmdSubmit, ++_sequence, 0x0001_0001, UsbIp.DirectionIn, 1, 0, (uint)(packets * Uac2Microphone.PacketBytes), 0, (uint)packets, 0 })
            UsbIp.AppendBe32(b, v);
        b.AddRange(new byte[8]);
        for (var i = 0; i < packets; i++)
            foreach (var v in new uint[] { (uint)(i * Uac2Microphone.PacketBytes), Uac2Microphone.PacketBytes, 0, 0 })
                UsbIp.AppendBe32(b, v);
        s.Write([.. b]);
    }

    private static (int Status, uint StartFrame, byte[] Data) ReadIsoReply(NetworkStream s, int packets)
    {
        var header = new byte[48];
        s.ReadExactly(header);
        static uint Be32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
        var data = new byte[Be32(header, 24)];
        s.ReadExactly(data);
        s.ReadExactly(new byte[packets * 16]);
        return ((int)Be32(header, 20), Be32(header, 28), data);
    }

    public void Dispose() => _server.Dispose();
}
