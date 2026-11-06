// Adapted from celmic (MIT, Celmic contributors) and Virtual Cables by Tom Albans (MIT):
// see third_party/celmic/NOTICE.md.
using System.Buffers.Binary;

namespace Altrove.Core.UsbMic;

/// <summary>The USB/IP wire format (protocol version 1.1.1) the microphone speaks with usbip-win2. All big-endian.</summary>
public static class UsbIp
{
    public const ushort ProtocolVersion = 0x0111;
    public const ushort OpReqImport = 0x8003;
    public const ushort OpRepImport = 0x0003;
    public const ushort OpReqDevlist = 0x8005;
    public const ushort OpRepDevlist = 0x0005;
    public const uint CmdSubmit = 1;
    public const uint CmdUnlink = 2;
    public const uint RetSubmit = 3;
    public const uint RetUnlink = 4;
    public const uint DirectionOut = 0;
    public const uint DirectionIn = 1;
    public const uint SpeedFull = 2;
    /// <summary><c>number_of_packets</c> of a URB that is not isochronous.</summary>
    public const uint NoIsoPackets = 0xFFFF_FFFF;
    public const int StatusOk = 0;
    public const int StatusPipe = -32;       // EPIPE: stall
    public const int StatusConnReset = -104; // ECONNRESET: unlinked

    private const uint MaxTransfer = 1024 * 1024;
    private const uint MaxIsoPackets = 1024;

    /// <summary>Reads the 8-byte operation header and returns its code.</summary>
    public static ushort ReadOpHeader(Stream s)
    {
        Span<byte> b = stackalloc byte[8];
        s.ReadExactly(b);
        var version = BinaryPrimitives.ReadUInt16BigEndian(b);
        if (version != ProtocolVersion)
            throw new InvalidDataException($"Unsupported USB/IP version 0x{version:x4}.");
        return BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
    }

    public static void AppendOpHeader(List<byte> dst, ushort code, uint status)
    {
        AppendBe16(dst, ProtocolVersion);
        AppendBe16(dst, code);
        AppendBe32(dst, status);
    }

    public static BasicHeader ReadBasicHeader(Stream s)
    {
        Span<byte> b = stackalloc byte[20];
        s.ReadExactly(b);
        return new BasicHeader(Be32(b, 0), Be32(b, 4), Be32(b, 12), Be32(b, 16));
    }

    /// <summary>The rest of a CMD_SUBMIT after its basic header: OUT payload and isochronous descriptors.</summary>
    public static Submit ReadSubmit(Stream s, BasicHeader basic)
    {
        Span<byte> b = stackalloc byte[28];
        s.ReadExactly(b);
        var submit = new Submit
        {
            Basic = basic,
            TransferBufferLength = Be32(b, 4),
            NumberOfPackets = Be32(b, 12),
            Setup = b[20..28].ToArray(),
        };
        if (submit.TransferBufferLength > MaxTransfer)
            throw new InvalidDataException($"Transfer length {submit.TransferBufferLength} too large.");
        if (basic.Direction == DirectionOut && submit.TransferBufferLength > 0)
        {
            submit.Out = new byte[submit.TransferBufferLength];
            s.ReadExactly(submit.Out);
        }
        if (submit.IsIsochronous)
        {
            if (submit.NumberOfPackets > MaxIsoPackets)
                throw new InvalidDataException($"{submit.NumberOfPackets} isochronous packets.");
            Span<byte> d = stackalloc byte[16];
            for (var i = 0; i < submit.NumberOfPackets; i++)
            {
                s.ReadExactly(d);
                submit.Packets.Add(new IsoPacket { Offset = Be32(d, 0), Length = Be32(d, 4), ActualLength = Be32(d, 8), Status = (int)Be32(d, 12) });
            }
        }
        return submit;
    }

    /// <summary>The body of a CMD_UNLINK: the sequence number to unlink.</summary>
    public static uint ReadUnlink(Stream s)
    {
        Span<byte> b = stackalloc byte[28];
        s.ReadExactly(b);
        return Be32(b, 0);
    }

    /// <summary>A USBIP_RET_SUBMIT; replies carry zero devid, direction and endpoint.</summary>
    public static void AppendRetSubmit(List<byte> dst, Submit submit, int status, uint startFrame, ReadOnlySpan<byte> data, IReadOnlyList<IsoPacket> packets)
    {
        if (status != StatusOk)
            data = [];
        var actual = (uint)data.Length;
        if (submit.Basic.Direction == DirectionOut && status == StatusOk)
            actual = (uint)submit.Out.Length;
        var numberOfPackets = submit.IsIsochronous ? (uint)packets.Count : NoIsoPackets;
        var errors = (uint)packets.Count(p => p.Status != StatusOk);

        AppendBe32(dst, RetSubmit);
        AppendBe32(dst, submit.Basic.Sequence);
        dst.AddRange(new byte[12]);
        AppendBe32(dst, (uint)status);
        AppendBe32(dst, actual);
        AppendBe32(dst, startFrame);
        AppendBe32(dst, numberOfPackets);
        AppendBe32(dst, errors);
        dst.AddRange(new byte[8]);
        if (submit.Basic.Direction == DirectionIn)
            dst.AddRange(data.ToArray());
        foreach (var p in packets)
        {
            AppendBe32(dst, p.Offset);
            AppendBe32(dst, p.Length);
            AppendBe32(dst, p.ActualLength);
            AppendBe32(dst, (uint)p.Status);
        }
    }

    public static void AppendRetUnlink(List<byte> dst, uint sequence, int status)
    {
        AppendBe32(dst, RetUnlink);
        AppendBe32(dst, sequence);
        dst.AddRange(new byte[12]);
        AppendBe32(dst, (uint)status);
        dst.AddRange(new byte[24]);
    }

    /// <summary>The 312-byte device record of OP_REP_DEVLIST and OP_REP_IMPORT.</summary>
    public static void AppendDeviceRecord(List<byte> b, string busId)
    {
        b.AddRange(Fixed($"/sys/devices/platform/altrove/{busId}", 256));
        b.AddRange(Fixed(busId, 32));
        var dev = Uac2Microphone.DeviceDescriptor();
        AppendBe32(b, 1); // bus number
        AppendBe32(b, 1); // device number
        AppendBe32(b, SpeedFull);
        foreach (var at in new[] { 8, 10, 12 })
            AppendBe16(b, BinaryPrimitives.ReadUInt16LittleEndian(dev.AsSpan(at))); // vendor, product, bcdDevice
        b.AddRange(dev[4..7]); // class, subclass, protocol
        b.AddRange([1, dev[17], Uac2Microphone.Interfaces]); // configuration value, configurations, interfaces
    }

    private static byte[] Fixed(string value, int size)
    {
        var bytes = new byte[size];
        var text = System.Text.Encoding.ASCII.GetBytes(value);
        Array.Copy(text, bytes, Math.Min(text.Length, size));
        return bytes;
    }

    private static uint Be32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b[at..]);

    public static void AppendBe16(List<byte> dst, ushort v)
    {
        dst.Add((byte)(v >> 8));
        dst.Add((byte)v);
    }

    public static void AppendBe32(List<byte> dst, uint v)
    {
        dst.Add((byte)(v >> 24));
        dst.Add((byte)(v >> 16));
        dst.Add((byte)(v >> 8));
        dst.Add((byte)v);
    }
}

public readonly record struct BasicHeader(uint Command, uint Sequence, uint Direction, uint Endpoint);

public struct IsoPacket
{
    public uint Offset;
    public uint Length;
    public uint ActualLength;
    public int Status;
}

/// <summary>A CMD_SUBMIT with its OUT payload and isochronous packet descriptors.</summary>
public sealed class Submit
{
    public BasicHeader Basic;
    public uint TransferBufferLength;
    public uint NumberOfPackets;
    public byte[] Setup = new byte[8];
    public byte[] Out = [];
    public List<IsoPacket> Packets = [];

    public bool IsIsochronous => NumberOfPackets != UsbIp.NoIsoPackets && NumberOfPackets != 0;
}
