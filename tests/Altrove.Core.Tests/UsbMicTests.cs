using System.Buffers.Binary;
using Altrove.Core.UsbMic;

namespace Altrove.Core.Tests;

/// <summary>The checks celmic makes on the same wire format and descriptors (third_party/celmic/NOTICE.md).</summary>
public class UsbMicTests
{
    private static uint Be32(IReadOnlyList<byte> b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    private static byte[] SubmitBytes(uint direction, uint endpoint, uint length, uint packets)
    {
        var b = new List<byte>();
        foreach (var v in new uint[] { UsbIp.CmdSubmit, 7, 0x0001_0001, direction, endpoint, 0, length, 0, packets, 0 })
            UsbIp.AppendBe32(b, v);
        b.AddRange([0x80, 6, 0, 1, 0, 0, 18, 0]);
        return [.. b];
    }

    [Fact]
    public void Parses_control_and_isochronous_submits()
    {
        var control = new MemoryStream(SubmitBytes(UsbIp.DirectionIn, 0, 18, UsbIp.NoIsoPackets));
        var basic = UsbIp.ReadBasicHeader(control);
        Assert.Equal((UsbIp.CmdSubmit, 7u, 0u), (basic.Command, basic.Sequence, basic.Endpoint));
        var submit = UsbIp.ReadSubmit(control, basic);
        Assert.False(submit.IsIsochronous);
        Assert.Equal(6, submit.Setup[1]); // GET_DESCRIPTOR
        Assert.Equal(control.Length, control.Position);

        var wire = new List<byte>(SubmitBytes(UsbIp.DirectionIn, 1, 576, 2));
        foreach (var (offset, length) in new[] { (0u, 288u), (288u, 288u) })
            foreach (var v in new[] { offset, length, 0u, 0u })
                UsbIp.AppendBe32(wire, v);
        var iso = new MemoryStream([.. wire]);
        var isoSubmit = UsbIp.ReadSubmit(iso, UsbIp.ReadBasicHeader(iso));
        Assert.True(isoSubmit.IsIsochronous);
        Assert.Equal(288u, isoSubmit.Packets[1].Offset);
        Assert.Empty(isoSubmit.Out);
    }

    [Fact]
    public void Replies_have_the_usbip_layout()
    {
        var submit = new Submit { Basic = new BasicHeader(0, 9, UsbIp.DirectionIn, 1), NumberOfPackets = 2 };
        var packets = new List<IsoPacket> { new() { Length = 288, ActualLength = 288 }, new() { Length = 288, ActualLength = 288 } };
        var wire = new List<byte>();
        UsbIp.AppendRetSubmit(wire, submit, UsbIp.StatusOk, 123, new byte[576], packets);
        Assert.Equal(UsbIp.RetSubmit, Be32(wire, 0));
        Assert.Equal(9u, Be32(wire, 4));
        Assert.Equal(576u, Be32(wire, 24));
        Assert.Equal(123u, Be32(wire, 28));
        Assert.Equal(2u, Be32(wire, 32));
        Assert.Equal(48 + 576 + 2 * 16, wire.Count);

        var error = new List<byte>();
        UsbIp.AppendRetSubmit(error, new Submit { Basic = new BasicHeader(0, 1, UsbIp.DirectionIn, 0), NumberOfPackets = UsbIp.NoIsoPackets },
            UsbIp.StatusPipe, 0, [1, 2, 3], []);
        Assert.Equal(48, error.Count);
        Assert.Equal(UsbIp.StatusPipe, (int)Be32(error, 20));

        var unlink = new List<byte>();
        UsbIp.AppendRetUnlink(unlink, 5, UsbIp.StatusConnReset);
        Assert.Equal(48, unlink.Count);
        Assert.Equal((UsbIp.RetUnlink, 5u), (Be32(unlink, 0), Be32(unlink, 4)));

        var record = new List<byte>();
        UsbIp.AppendDeviceRecord(record, "1-1");
        Assert.Equal(312, record.Count);
        Assert.Equal(Uac2Microphone.Interfaces, record[311]);
    }

    [Fact]
    public void Descriptors_are_a_uac2_microphone()
    {
        var device = Uac2Microphone.DeviceDescriptor();
        Assert.Equal(18, device.Length);
        Assert.Equal(new byte[] { 0xEF, 0x02, 0x01 }, device[4..7]);

        var config = Uac2Microphone.ConfigurationDescriptor();
        Assert.Equal(config.Length, BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(2)));
        var parts = new List<byte[]>();
        for (var pos = 0; pos < config.Length; pos += config[pos])
        {
            Assert.True(config[pos] >= 2 && pos + config[pos] <= config.Length, $"descriptor at {pos}");
            parts.Add(config[pos..(pos + config[pos])]);
        }
        Assert.Equal(3, parts.Count(p => p[1] == 0x04)); // AC, and two alternate settings of AS
        var endpoint = Assert.Single(parts, p => p[1] == 0x05);
        Assert.Equal((0x81, 0x0D), (endpoint[2], endpoint[3]));
        Assert.Equal(Uac2Microphone.PacketBytes, BinaryPrimitives.ReadUInt16LittleEndian(endpoint.AsSpan(4)));
        var acHeader = parts.FindIndex(p => p[1] == 0x24 && p[2] == 0x01);
        var acTotal = parts.Skip(acHeader).TakeWhile(p => p[1] == 0x24).Sum(p => p.Length);
        Assert.Equal(acTotal, BinaryPrimitives.ReadUInt16LittleEndian(parts[acHeader].AsSpan(6)));
    }

    private static (byte[] Data, int Status) Control(Uac2Microphone mic, byte type, byte request, ushort value, ushort index, ushort length, byte[]? output = null) =>
        mic.HandleControl(new SetupPacket(type, request, value, index, length), output ?? []);

    [Fact]
    public void Control_requests_and_capture_with_gain()
    {
        var jitter = new MicJitterBuffer();
        var mic = new Uac2Microphone(jitter);
        var name = Control(mic, 0x80, Uac2Microphone.RequestGetDescriptor, 0x0302, 0x0409, 255);
        Assert.Equal(Uac2Microphone.Product, System.Text.Encoding.Unicode.GetString(name.Data, 2, name.Data.Length - 2));
        Assert.Equal(UsbIp.StatusPipe, Control(mic, 0x80, Uac2Microphone.RequestGetDescriptor, 0x0600, 0, 10).Status); // no qualifier
        Assert.Equal(BitConverter.GetBytes(48_000), Control(mic, 0xA1, 0x01, 0x0100, 0x0900, 4).Data);

        Assert.False(mic.Streaming);
        Control(mic, 0x00, Uac2Microphone.RequestSetConfiguration, 1, 0, 0);
        Control(mic, 0x01, Uac2Microphone.RequestSetInterface, 1, 1, 0);
        Assert.True(mic.Streaming);

        jitter.Push(Enumerable.Repeat(0.5f, 9600).ToArray(), 48_000); // 200 ms
        float Sample()
        {
            var output = new byte[Uac2Microphone.PacketBytes * 2];
            mic.ReadCapture(output, [Uac2Microphone.PacketBytes, Uac2Microphone.PacketBytes]);
            var at = Uac2Microphone.PacketBytes + Uac2Microphone.FrameBytes * 10;
            var left = output[at] | output[at + 1] << 8 | (sbyte)output[at + 2] << 16;
            var right = output[at + 3] | output[at + 4] << 8 | (sbyte)output[at + 5] << 16;
            Assert.Equal(left, right);
            return left / 8_388_607f;
        }
        Assert.Equal(0f, Sample()); // silent until Altrove feeds it
        mic.Feeding = true;
        Assert.Equal(0.5f, Sample(), 3);
        Control(mic, 0x21, 0x01, 0x0200, 0x0200, 2, BitConverter.GetBytes((short)(-6 * 256)));
        Assert.Equal(0.25f, Sample(), 2); // -6 dB
        Control(mic, 0x21, 0x01, 0x0100, 0x0200, 1, [1]);
        Assert.Equal(0f, Sample()); // muted
    }

    [Fact]
    public void Jitter_buffer_waits_for_its_target_and_rebuffers_after_an_underrun()
    {
        var jitter = new MicJitterBuffer();
        var playout = new MicPlayout(48_000);
        var output = new float[480];
        jitter.Push(Enumerable.Repeat(0.5f, 960).ToArray(), 48_000); // 20 ms < 40 ms
        playout.Fill(jitter, 40, output);
        Assert.All(output, s => Assert.Equal(0f, s));
        jitter.Push(Enumerable.Repeat(0.5f, 1920).ToArray(), 48_000);
        playout.Fill(jitter, 40, output);
        Assert.All(output.Skip(2), s => Assert.Equal(0.5f, s, 5));

        var big = new float[4000];
        playout.Fill(jitter, 40, big);
        Assert.Equal(1, jitter.Underruns);

        for (var i = 0; i < 1000; i++)
            jitter.Push(new float[480], 48_000); // 10 s with nobody listening
        Assert.True(jitter.FillMs <= 2000);
    }
}
