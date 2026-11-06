// Adapted from celmic (MIT, Celmic contributors) and Virtual Cables by Tom Albans (MIT):
// see third_party/celmic/NOTICE.md.
using System.Buffers.Binary;
using System.Text;

namespace Altrove.Core.UsbMic;

/// <summary>
/// A full-speed USB Audio Class 2.0 microphone, 48 kHz 24-bit stereo (the tablet's mono voice on both channels):
/// its descriptors, the control requests usbaudio2.sys makes, and the capture endpoint, fed from a
/// <see cref="MicJitterBuffer"/>. UAC2 rather than UAC1, so Windows binds usbaudio2.sys, which keeps streaming
/// under load.
/// </summary>
public sealed class Uac2Microphone
{
    public const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SubslotBytes = 3;
    public const int FrameBytes = Channels * SubslotBytes;
    /// <summary>One packet per 1 ms full-speed frame.</summary>
    public const int PacketFrames = SampleRate / 1000;
    public const int PacketBytes = PacketFrames * FrameBytes;
    /// <summary>The isochronous IN endpoint (0x81).</summary>
    public const uint CaptureEndpoint = 1;
    public const byte Interfaces = 2;
    /// <summary>Unregistered "experimental" vendor ID, as local virtual devices use.</summary>
    public const ushort VendorId = 0xFFFF;
    public const ushort ProductId = 0xA17E;
    public const string Product = "Microfono Altrove";

    private const byte DescriptorDevice = 0x01, DescriptorConfiguration = 0x02, DescriptorString = 0x03,
        DescriptorInterface = 0x04, DescriptorEndpoint = 0x05, DescriptorInterfaceAssociation = 0x0B;
    private const byte CsInterface = 0x24, CsEndpoint = 0x25;
    private const byte IpVersion0200 = 0x20;
    // Microphone input terminal -> feature unit -> USB streaming output terminal, on one fixed clock.
    private const byte ClockSource = 9, InputTerminal = 1, FeatureUnit = 2, OutputTerminal = 3;

    public const byte RequestGetStatus = 0x00, RequestClearFeature = 0x01, RequestSetFeature = 0x03, RequestSetAddress = 0x05,
        RequestGetDescriptor = 0x06, RequestGetConfiguration = 0x08, RequestSetConfiguration = 0x09,
        RequestGetInterface = 0x0A, RequestSetInterface = 0x0B, RequestSynchFrame = 0x0C;
    private const byte AudioCur = 0x01, AudioRange = 0x02;
    private const byte CsSamFreqControl = 0x01, CsClockValidControl = 0x02, FuMuteControl = 0x01, FuVolumeControl = 0x02;
    // Feature unit volume, in 1/256 dB.
    private const short VolumeMin = -60 * 256, VolumeMax = 0, VolumeRes = 256, VolumeSilent = short.MinValue;

    private readonly Dictionary<(byte Type, byte Index), byte[]> _descriptors = [];
    private readonly object _lock = new();
    private readonly MicJitterBuffer _jitter;
    private readonly MicPlayout _playout = new(SampleRate);
    private float[] _mono = [];
    private byte _configuration;
    private bool _streaming, _mute;
    private short _volume;

    public Uac2Microphone(MicJitterBuffer jitter)
    {
        _jitter = jitter;
        _descriptors[(DescriptorDevice, 0)] = DeviceDescriptor();
        _descriptors[(DescriptorConfiguration, 0)] = ConfigurationDescriptor();
        _descriptors[(DescriptorString, 0)] = [4, DescriptorString, 0x09, 0x04]; // en-US
        _descriptors[(DescriptorString, 1)] = StringDescriptor("Altrove");
        _descriptors[(DescriptorString, 2)] = StringDescriptor(Product);
        _descriptors[(DescriptorString, 3)] = StringDescriptor("ALTROVE-1");
    }

    /// <summary>The tablet's voice goes into the microphone; otherwise it records silence.</summary>
    public volatile bool Feeding;

    /// <summary>How much voice is kept waiting, against Wi-Fi jitter.</summary>
    public int TargetLatencyMs { get; set; } = 60;

    /// <summary>An app is recording from the microphone.</summary>
    public bool Streaming
    {
        get
        {
            lock (_lock)
                return _configuration == 1 && _streaming;
        }
    }

    public static byte[] DeviceDescriptor()
    {
        var b = new List<byte> { 18, DescriptorDevice };
        AddLe16(b, 0x0200); // USB 2.0, full speed only
        b.AddRange([0xEF, 0x02, 0x01, 64]); // miscellaneous / interface association
        AddLe16(b, VendorId);
        AddLe16(b, ProductId);
        AddLe16(b, 0x0100);
        b.AddRange([1, 2, 3, 1]); // manufacturer, product, serial strings; 1 configuration
        return [.. b];
    }

    public static byte[] ConfigurationDescriptor()
    {
        const byte c = CsInterface;
        byte[] channels = [3, 0, 0, 0]; // front left, front right
        var b = new List<byte>();
        b.AddRange([9, DescriptorConfiguration, 0, 0, Interfaces, 1, 0, 0x80, 50]);
        b.AddRange([8, DescriptorInterfaceAssociation, 0, Interfaces, 0x01, 0x00, IpVersion0200, 0]);

        // AudioControl interface 0.
        b.AddRange([9, DescriptorInterface, 0, 0, 0, 0x01, 0x01, IpVersion0200, 0]);
        var header = b.Count;
        b.AddRange([9, c, 0x01, 0x00, 0x02, 0x03, 0, 0, 0x00]); // AC header: ADC 2.0, microphone
        b.AddRange([8, c, 0x0A, ClockSource, 0x05, 0x05, 0, 0]); // internal fixed clock, synchronized to SOF
        b.AddRange([17, c, 0x02, InputTerminal]); // microphone input terminal
        AddLe16(b, 0x0201);
        b.AddRange([0, ClockSource, Channels]);
        b.AddRange(channels);
        b.AddRange([0, 0, 0, 0]);
        b.AddRange([18, c, 0x06, FeatureUnit, InputTerminal, 0x0F, 0, 0, 0]); // feature unit: master mute and volume
        b.AddRange(new byte[8]);
        b.Add(0);
        b.AddRange([12, c, 0x03, OutputTerminal]); // USB streaming output terminal
        AddLe16(b, 0x0101);
        b.AddRange([0, FeatureUnit, ClockSource, 0, 0, 0]);
        var acLength = (ushort)(b.Count - header);
        b[header + 6] = (byte)acLength;
        b[header + 7] = (byte)(acLength >> 8);

        // AudioStreaming interface 1: alternate 0 idle, alternate 1 streams.
        b.AddRange([9, DescriptorInterface, 1, 0, 0, 0x01, 0x02, IpVersion0200, 0]);
        b.AddRange([9, DescriptorInterface, 1, 1, 1, 0x01, 0x02, IpVersion0200, 0]);
        b.AddRange([16, c, 0x01, OutputTerminal, 0x00, 0x01, 1, 0, 0, 0, Channels]); // AS general: Type I PCM
        b.AddRange(channels);
        b.Add(0);
        b.AddRange([6, c, 0x02, 0x01, SubslotBytes, 24]); // Type I format: 3-byte subslots, 24 bits
        // Isochronous IN endpoint, synchronous to the bus frame: one 48-frame packet per frame, no feedback.
        b.AddRange([7, DescriptorEndpoint, (byte)(0x80 | CaptureEndpoint), 0x0D]);
        AddLe16(b, PacketBytes);
        b.Add(1);
        b.AddRange([8, CsEndpoint, 0x01, 0x00, 0x00, 0x00, 0, 0]);

        var total = (ushort)b.Count;
        b[2] = (byte)total;
        b[3] = (byte)(total >> 8);
        return [.. b];
    }

    private static byte[] StringDescriptor(string s)
    {
        var text = Encoding.Unicode.GetBytes(s.Length > 126 ? s[..126] : s);
        var b = new byte[2 + text.Length];
        b[0] = (byte)b.Length;
        b[1] = DescriptorString;
        text.CopyTo(b, 2);
        return b;
    }

    /// <summary>Standard requests and the UAC2 class requests; returns the IN data and a USB/IP status.</summary>
    public (byte[] Data, int Status) HandleControl(SetupPacket setup, ReadOnlySpan<byte> output)
    {
        lock (_lock)
        {
            (byte[], int) Reply(byte[] value) => (value.Length > setup.Length ? value[..setup.Length] : value, UsbIp.StatusOk);
            (byte[], int) Ok() => ([], UsbIp.StatusOk);
            (byte[], int) Stall() => ([], UsbIp.StatusPipe);

            if ((setup.RequestType & 0x60) == 0x00)
            {
                switch (setup.Request)
                {
                    case RequestGetDescriptor:
                        return _descriptors.TryGetValue(((byte)(setup.Value >> 8), (byte)setup.Value), out var d) ? Reply(d) : Stall();
                    case RequestSetAddress or RequestClearFeature or RequestSetFeature:
                        return Ok();
                    case RequestSetConfiguration when setup.Value <= 1:
                        _configuration = (byte)setup.Value;
                        _streaming = false;
                        return Ok();
                    case RequestGetConfiguration:
                        return Reply([_configuration]);
                    case RequestSetInterface:
                        if (_configuration == 1 && setup.Index == 0 && setup.Value == 0)
                            return Ok();
                        if (_configuration == 1 && setup.Index == 1 && setup.Value <= 1)
                        {
                            _streaming = setup.Value == 1;
                            return Ok();
                        }
                        return Stall();
                    case RequestGetInterface:
                        return setup.Index switch { 0 => Reply([0]), 1 => Reply([(byte)(_streaming ? 1 : 0)]), _ => Stall() };
                    case RequestGetStatus or RequestSynchFrame:
                        return Reply([0, 0]);
                    default:
                        return Stall();
                }
            }

            // Class requests to an AudioControl entity: wIndex = entity and interface, wValue = control and channel.
            var get = (setup.RequestType & 0x80) != 0;
            var entity = (byte)(setup.Index >> 8);
            var control = (byte)(setup.Value >> 8);
            if ((setup.RequestType & 0x1F) != 0x01 || (byte)setup.Index != 0 || (byte)setup.Value != 0)
                return Stall();

            switch (entity, control, setup.Request, get)
            {
                case (ClockSource, CsSamFreqControl, AudioCur, true):
                    return Reply(BitConverter.GetBytes(SampleRate));
                case (ClockSource, CsSamFreqControl, AudioRange, true):
                    return Reply([1, 0, .. BitConverter.GetBytes(SampleRate), .. BitConverter.GetBytes(SampleRate), 0, 0, 0, 0]);
                case (ClockSource, CsSamFreqControl, AudioCur, false):
                    // Read-only, but setting the frequency it already has is harmless.
                    return output.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(output) == SampleRate ? Ok() : Stall();
                case (ClockSource, CsClockValidControl, AudioCur, true):
                    return Reply([1]);
                case (FeatureUnit, FuMuteControl, AudioCur, true):
                    return Reply([(byte)(_mute ? 1 : 0)]);
                case (FeatureUnit, FuMuteControl, AudioCur, false):
                    if (output.Length > 0)
                        _mute = output[0] != 0;
                    return Ok();
                case (FeatureUnit, FuVolumeControl, AudioCur, true):
                    return Reply(BitConverter.GetBytes(_volume));
                case (FeatureUnit, FuVolumeControl, AudioCur, false):
                    if (output.Length >= 2)
                    {
                        var volume = BinaryPrimitives.ReadInt16LittleEndian(output);
                        _volume = volume == VolumeSilent ? volume : Math.Clamp(volume, VolumeMin, VolumeMax);
                    }
                    return Ok();
                case (FeatureUnit, FuVolumeControl, AudioRange, true):
                    return Reply([1, 0, .. BitConverter.GetBytes(VolumeMin), .. BitConverter.GetBytes(VolumeMax), .. BitConverter.GetBytes(VolumeRes)]);
                default:
                    return Stall();
            }
        }
    }

    private float Gain()
    {
        lock (_lock)
            return _mute || _volume == VolumeSilent ? 0f : MathF.Pow(10f, _volume / 256f / 20f);
    }

    /// <summary>Fills a capture URB: packets back to back in <paramref name="output"/> with the given lengths.</summary>
    public void ReadCapture(Span<byte> output, IEnumerable<int> lengths)
    {
        if (!Streaming || !Feeding)
        {
            output.Clear();
            return;
        }
        var gain = Gain();
        lock (_playout)
        {
            var position = 0;
            foreach (var requested in lengths)
            {
                var length = Math.Min(requested, output.Length - position);
                var packet = output.Slice(position, length);
                position += length;
                var frames = length / FrameBytes;
                if (_mono.Length < frames)
                    _mono = new float[frames];
                var mono = _mono.AsSpan(0, frames);
                _playout.Fill(_jitter, TargetLatencyMs, mono);
                for (var f = 0; f < frames; f++)
                {
                    var value = (int)(Math.Clamp(mono[f] * gain, -1f, 1f) * 8_388_607f);
                    var at = f * FrameBytes;
                    for (var ch = 0; ch < Channels; ch++)
                    {
                        packet[at + ch * SubslotBytes] = (byte)value;
                        packet[at + ch * SubslotBytes + 1] = (byte)(value >> 8);
                        packet[at + ch * SubslotBytes + 2] = (byte)(value >> 16);
                    }
                }
                packet[(frames * FrameBytes)..].Clear();
            }
            output[position..].Clear();
        }
    }

    private static void AddLe16(List<byte> b, int v)
    {
        b.Add((byte)v);
        b.Add((byte)(v >> 8));
    }
}

/// <summary>The 8-byte setup packet of a control transfer (little-endian fields).</summary>
public readonly record struct SetupPacket(byte RequestType, byte Request, ushort Value, ushort Index, ushort Length)
{
    public static SetupPacket Parse(ReadOnlySpan<byte> b) => new(b[0], b[1],
        BinaryPrimitives.ReadUInt16LittleEndian(b[2..]), BinaryPrimitives.ReadUInt16LittleEndian(b[4..]), BinaryPrimitives.ReadUInt16LittleEndian(b[6..]));

    /// <summary>CLEAR_FEATURE(ENDPOINT_HALT): the host reset a pipe.</summary>
    public bool IsEndpointHaltClear => RequestType == 0x02 && Request == Uac2Microphone.RequestClearFeature && Value == 0;

    /// <summary>Requests that stop or restart the audio stream.</summary>
    public bool RestartsStreams => (RequestType == 0x01 && Request == Uac2Microphone.RequestSetInterface)
        || (RequestType == 0x00 && Request == Uac2Microphone.RequestSetConfiguration);
}
