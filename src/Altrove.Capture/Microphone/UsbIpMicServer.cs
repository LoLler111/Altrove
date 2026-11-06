// Adapted from celmic (MIT, Celmic contributors) and Virtual Cables by Tom Albans (MIT):
// see third_party/celmic/NOTICE.md.
using System.Net;
using System.Net.Sockets;
using Altrove.Core.UsbMic;

namespace Altrove.Capture.Microphone;

/// <summary>
/// "Microfono Altrove" without a driver of our own: a USB Audio Class 2.0 microphone exported over USB/IP on
/// localhost. usbip-win2 (open source, Microsoft-signed) attaches it and Windows binds its own usbaudio2.sys, so every
/// app sees an ordinary USB microphone. Only 127.0.0.1 can reach it.
/// </summary>
public sealed class UsbIpMicServer : IDisposable
{
    public const int TcpPort = 47809;
    public const string BusId = "1-1";

    private readonly TcpListener _listener = new(IPAddress.Loopback, TcpPort);
    private readonly Uac2Microphone _device;
    private readonly object _sessionLock = new();
    private TcpClient? _current;
    private volatile bool _stopping;

    public UsbIpMicServer(MicJitterBuffer jitter)
    {
        _device = new Uac2Microphone(jitter);
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "Altrove USB/IP" }.Start();
    }

    /// <summary>Windows has the microphone attached.</summary>
    public bool Attached
    {
        get
        {
            lock (_sessionLock)
                return _current is not null;
        }
    }

    /// <summary>An app is recording from the microphone.</summary>
    public bool Streaming => Attached && _device.Streaming;

    /// <summary>The tablet's voice goes into the microphone (otherwise it records silence).</summary>
    public bool Feeding
    {
        get => _device.Feeding;
        set => _device.Feeding = value;
    }

    private void AcceptLoop()
    {
        while (!_stopping)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            new Thread(() => HandleConnection(client)) { IsBackground = true, Name = "Altrove USB/IP connection" }.Start();
        }
    }

    private void HandleConnection(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 10_000;
            var stream = client.GetStream();
            var reader = new BufferedStream(stream, 64 * 1024);
            try
            {
                switch (UsbIp.ReadOpHeader(reader))
                {
                    case UsbIp.OpReqDevlist:
                    {
                        var reply = new List<byte>();
                        UsbIp.AppendOpHeader(reply, UsbIp.OpRepDevlist, 0);
                        UsbIp.AppendBe32(reply, 1);
                        UsbIp.AppendDeviceRecord(reply, BusId);
                        reply.AddRange([0x01, 0x01, 0x20, 0]); // AudioControl, UAC2
                        reply.AddRange([0x01, 0x02, 0x20, 0]); // AudioStreaming, UAC2
                        stream.Write([.. reply]);
                        break;
                    }
                    case UsbIp.OpReqImport:
                    {
                        var busId = new byte[32];
                        reader.ReadExactly(busId);
                        var reply = new List<byte>();
                        if (System.Text.Encoding.ASCII.GetString(busId).TrimEnd('\0') != BusId)
                        {
                            UsbIp.AppendOpHeader(reply, UsbIp.OpRepImport, 1);
                            stream.Write([.. reply]);
                            return;
                        }
                        // usbip-win2 may reattach before noticing the old connection is gone: the new one wins.
                        lock (_sessionLock)
                        {
                            _current?.Close();
                            _current = client;
                        }
                        UsbIp.AppendOpHeader(reply, UsbIp.OpRepImport, 0);
                        UsbIp.AppendDeviceRecord(reply, BusId);
                        stream.Write([.. reply]);
                        client.ReceiveTimeout = 0; // an idle microphone gets no URBs
                        using (var session = new Session(reader, stream, _device))
                            session.Run();
                        lock (_sessionLock)
                        {
                            if (ReferenceEquals(_current, client))
                                _current = null;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or ObjectDisposedException or EndOfStreamException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _listener.Stop();
        lock (_sessionLock)
            _current?.Close();
    }

    /// <summary>One isochronous capture URB, waiting for its last frame to pass.</summary>
    private sealed record Job(Submit Submit, ulong Start, ulong End, CancellationFlag Cancel);

    private sealed class CancellationFlag
    {
        public volatile bool Cancelled;
    }

    /// <summary>One attachment: control requests in order, capture URBs completed on the bus-frame schedule.</summary>
    private sealed class Session(Stream reader, Stream writer, Uac2Microphone device) : IDisposable
    {
        private readonly object _writeLock = new();
        private readonly object _scheduleLock = new();
        private readonly Dictionary<uint, CancellationFlag> _pending = [];
        private ulong? _nextFrame;
        private readonly Queue<Job> _queue = new();
        private readonly object _queueLock = new();
        private volatile bool _stopping;
        private readonly List<byte> _frame = new(4096);

        public void Run()
        {
            using var timer = BusClock.FineTimer();
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            var worker = new Thread(CompleteUrbs) { IsBackground = true, Name = "Altrove USB/IP iso", Priority = ThreadPriority.Highest };
            worker.Start();
            try
            {
                while (true)
                    HandleCommand();
            }
            finally
            {
                _stopping = true;
                lock (_queueLock)
                    Monitor.PulseAll(_queueLock);
                worker.Join(1000);
            }
        }

        private void HandleCommand()
        {
            var basic = UsbIp.ReadBasicHeader(reader);
            switch (basic.Command)
            {
                case UsbIp.CmdSubmit:
                {
                    var submit = UsbIp.ReadSubmit(reader, basic);
                    if (basic.Endpoint == 0)
                    {
                        Control(submit);
                        return;
                    }
                    if (!(submit.IsIsochronous && basic.Endpoint == Uac2Microphone.CaptureEndpoint && basic.Direction == UsbIp.DirectionIn))
                    {
                        Reply(submit, null, UsbIp.StatusPipe, 0, [], []);
                        return;
                    }
                    var cancel = new CancellationFlag();
                    ulong start, end;
                    lock (_scheduleLock)
                    {
                        _pending[basic.Sequence] = cancel;
                        (start, end) = Reserve((ulong)submit.Packets.Count, BusClock.Frame());
                    }
                    lock (_queueLock)
                    {
                        _queue.Enqueue(new Job(submit, start, end, cancel));
                        Monitor.PulseAll(_queueLock);
                    }
                    return;
                }
                case UsbIp.CmdUnlink:
                {
                    var target = UsbIp.ReadUnlink(reader);
                    lock (_writeLock)
                    {
                        bool found;
                        lock (_scheduleLock)
                            found = Claim(target, unlinked: true);
                        lock (_queueLock)
                            Monitor.PulseAll(_queueLock);
                        _frame.Clear();
                        // The protocol answers a successful unlink with -ECONNRESET.
                        UsbIp.AppendRetUnlink(_frame, basic.Sequence, found ? UsbIp.StatusConnReset : UsbIp.StatusOk);
                        writer.Write([.. _frame]);
                    }
                    return;
                }
                default:
                    throw new InvalidDataException($"USB/IP command {basic.Command}.");
            }
        }

        // Endpoint zero is serialized: enumeration depends on request order.
        private void Control(Submit submit)
        {
            var setup = SetupPacket.Parse(submit.Setup);
            var (data, status) = device.HandleControl(setup, submit.Out);
            if (status == UsbIp.StatusOk && (setup.IsEndpointHaltClear || setup.RestartsStreams))
            {
                lock (_scheduleLock)
                    ResetIfIdle();
            }
            if (data.Length > submit.TransferBufferLength)
                data = data[..(int)submit.TransferBufferLength];
            Reply(submit, null, status == 0 ? UsbIp.StatusOk : UsbIp.StatusPipe, 0, data, []);
        }

        /// <summary>
        /// Places an ASAP isochronous URB as a host controller does: right after the previous one, or on the next
        /// frame if that slot has begun (Windows queues several URBs, so the schedule sets the cadence).
        /// </summary>
        private (ulong Start, ulong End) Reserve(ulong packets, ulong current)
        {
            var start = _nextFrame is { } next && next > current ? next : current + 1;
            var end = start + packets;
            _nextFrame = end;
            return (start, end);
        }

        // The host stopped, aborted or reset the stream: the next URB starts a fresh schedule.
        private void ResetIfIdle()
        {
            if (_pending.Count == 0)
                _nextFrame = null;
        }

        /// <summary>Takes a pending URB, so it gets exactly one reply.</summary>
        private bool Claim(uint sequence, bool unlinked)
        {
            if (!_pending.Remove(sequence, out var cancel))
                return false;
            if (unlinked)
            {
                cancel.Cancelled = true;
                ResetIfIdle();
            }
            return true;
        }

        private void Reply(Submit submit, uint? tracked, int status, uint startFrame, ReadOnlySpan<byte> data, IReadOnlyList<IsoPacket> packets)
        {
            lock (_writeLock)
            {
                if (tracked is { } sequence)
                {
                    lock (_scheduleLock)
                    {
                        if (!Claim(sequence, unlinked: false))
                            return; // an UNLINK already answered it
                    }
                }
                _frame.Clear();
                UsbIp.AppendRetSubmit(_frame, submit, status, startFrame, data, packets);
                writer.Write([.. _frame]);
            }
        }

        /// <summary>Completes capture URBs in order, each once its last frame has passed, with the voice of that moment.</summary>
        private void CompleteUrbs()
        {
            var audio = Array.Empty<byte>();
            while (true)
            {
                Job job;
                lock (_queueLock)
                {
                    while (true)
                    {
                        if (_stopping)
                            return;
                        if (_queue.TryDequeue(out var next))
                        {
                            job = next;
                            break;
                        }
                        Monitor.Wait(_queueLock);
                    }
                }

                bool Cancelled() => job.Cancel.Cancelled || _stopping;
                while (!Cancelled())
                {
                    var left = BusClock.UntilFrame(job.End);
                    if (left > TimeSpan.FromMilliseconds(2))
                    {
                        lock (_queueLock)
                            Monitor.Wait(_queueLock, left - TimeSpan.FromMilliseconds(1));
                    }
                    else if (BusClock.Frame() < job.End)
                        Thread.Sleep(0);
                    else
                        break;
                }
                if (Cancelled())
                    continue;

                var total = (int)Math.Min(job.Submit.Packets.Sum(p => (long)p.Length), job.Submit.TransferBufferLength);
                if (audio.Length != total)
                    audio = new byte[total];
                device.ReadCapture(audio, job.Submit.Packets.Select(p => (int)p.Length));
                var remaining = audio.Length;
                for (var i = 0; i < job.Submit.Packets.Count; i++)
                {
                    var packet = job.Submit.Packets[i];
                    packet.ActualLength = (uint)Math.Min((int)packet.Length, remaining);
                    packet.Status = UsbIp.StatusOk;
                    remaining -= (int)packet.ActualLength;
                    job.Submit.Packets[i] = packet;
                }
                try
                {
                    // usbaudio2.sys tracks the stream position from the frame we report.
                    Reply(job.Submit, job.Submit.Basic.Sequence, UsbIp.StatusOk, (uint)job.Start, audio, job.Submit.Packets);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return;
                }
            }
        }

        public void Dispose() => _stopping = true;
    }
}
