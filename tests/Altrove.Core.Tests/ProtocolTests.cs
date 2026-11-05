using System.Text.Json;
using Altrove.Core.Discovery;
using Altrove.Core.Protocol;
using Altrove.Core.Video;

namespace Altrove.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Hello_round_trips_with_type_first()
    {
        var hello = new HelloMessage(1, Roles.Receiver, "abc", "Tab A", "0.1.0", new DisplayInfo(1920, 1200, 60), ["h264"], 1920, 1080);
        var json = hello.ToJson();
        Assert.StartsWith("{\"type\":\"hello\"", json);
        Assert.Contains("\"maxWidth\":1920", json);
        var parsed = Assert.IsType<HelloMessage>(ControlMessage.Parse(json));
        Assert.Equal(hello.Name, parsed.Name);
        Assert.Equal(hello.Display, parsed.Display);
        Assert.Equal(["h264"], parsed.Codecs!);
    }

    [Fact]
    public void Parse_accepts_type_anywhere_and_ignores_unknown_fields()
    {
        var parsed = ControlMessage.Parse("{\"fps\":59.5,\"future\":{\"x\":1},\"type\":\"stats\",\"dropped\":2}");
        var stats = Assert.IsType<StatsMessage>(parsed);
        Assert.Equal(59.5, stats.Fps);
        Assert.Equal(2, stats.Dropped);
    }

    [Fact]
    public void Parse_returns_null_for_unknown_types_and_throws_for_garbage()
    {
        Assert.Null(ControlMessage.Parse("{\"type\":\"touch\",\"x\":1}"));
        Assert.ThrowsAny<JsonException>(() => ControlMessage.Parse("[1,2]"));
        Assert.ThrowsAny<JsonException>(() => ControlMessage.Parse("{\"no\":\"type\"}"));
    }

    [Fact]
    public void Messages_without_fields_serialize_to_just_the_type()
    {
        Assert.Equal("{\"type\":\"keyframe\"}", new KeyframeMessage().ToJson());
        Assert.Equal("{\"type\":\"stop\"}", new StopMessage().ToJson());
        Assert.Equal("{\"type\":\"start\",\"codec\":\"h264\",\"width\":1280,\"height\":720,\"fps\":60,\"mode\":\"mirror\"}",
            new StartMessage("h264", 1280, 720, 60, StreamModes.Mirror).ToJson());
    }

    [Fact]
    public void Video_packet_header_is_big_endian()
    {
        var packet = VideoPacket.Encode([0, 0, 0, 1, 0x65], 0x0102030405060708, VideoPacketFlags.Keyframe | VideoPacketFlags.ParameterSets);
        Assert.Equal(new byte[] { 1, 3, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 1, 0x65 }, packet);
        Assert.True(VideoPacket.TryDecode(packet, out var flags, out var pts, out var unit));
        Assert.Equal(VideoPacketFlags.Keyframe | VideoPacketFlags.ParameterSets, flags);
        Assert.Equal(0x0102030405060708, pts);
        Assert.Equal(5, unit.Length);
        Assert.False(VideoPacket.TryDecode(new byte[5], out _, out _, out _));
    }

    [Fact]
    public void H264_finds_nal_units_with_both_start_code_lengths()
    {
        byte[] data = [0, 0, 0, 1, 0x67, 0xAA, 0, 0, 1, 0x68, 0xBB, 0, 0, 0, 1, 0x65, 0x88, 0x80];
        var nals = H264.NalUnits(data).Select(n => n.ToArray()).ToList();
        Assert.Equal(3, nals.Count);
        Assert.Equal(new byte[] { 0x67, 0xAA }, nals[0]);
        Assert.Equal(new byte[] { 0x68, 0xBB }, nals[1]);
        Assert.Equal(new byte[] { 0x65, 0x88, 0x80 }, nals[2]);
        Assert.Equal((true, true), H264.Describe(data));
        Assert.Equal((false, false), H264.Describe(new byte[] { 0, 0, 1, 0x41, 1 }));
        Assert.Equal(data.Length + 1, H264.ToAnnexB(H264.NalUnits(data)).Length);
    }

    [Fact]
    public void Idr_without_parameter_sets_gets_the_last_ones()
    {
        var cache = new ParameterSetCache();
        byte[] first = [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2, 0, 0, 0, 1, 0x65, 3];
        Assert.Equal(first, cache.Complete(first).ToArray());
        byte[] delta = [0, 0, 0, 1, 0x41, 4];
        Assert.Equal(delta, cache.Complete(delta).ToArray());
        byte[] bareIdr = [0, 0, 0, 1, 0x65, 5];
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2, 0, 0, 0, 1, 0x65, 5 }, cache.Complete(bareIdr).ToArray());
        Assert.Equal(bareIdr, new ParameterSetCache().Complete(bareIdr).ToArray()); // nothing cached yet
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 1920, 1080)]
    [InlineData(2560, 1440, 1920, 1200, 1920, 1080)]
    [InlineData(1920, 1080, 1280, 800, 1280, 720)]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]   // never scaled up
    [InlineData(3440, 1440, 1920, 1080, 1920, 802)]  // ultrawide, even height
    [InlineData(1920, 1080, 1200, 1920, 1920, 1080)] // portrait limits are turned to match
    [InlineData(1366, 768, 1000, 1000, 1000, 562)]
    public void Stream_fits_inside_the_receiver(int sw, int sh, int mw, int mh, int ew, int eh)
    {
        Assert.Equal((ew, eh), StreamSizing.Fit(sw, sh, mw, mh));
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 47800)]
    [InlineData(" 192.168.1.20:5000 ", "192.168.1.20", 5000)]
    [InlineData("ws://10.0.0.2:47801/", "10.0.0.2", 47801)]
    [InlineData("tablet.local", "tablet.local", 47800)]
    [InlineData("tablet.local:1234", "tablet.local", 1234)]
    [InlineData("[fe80::1]:47800", "fe80::1", 47800)]
    public void Receiver_address_parses(string text, string host, int port)
    {
        Assert.True(ReceiverAddress.TryParse(text, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.20:0")]
    [InlineData("192.168.1.20:99999")]
    [InlineData("host:abc")]
    [InlineData("not a host")]
    public void Receiver_address_rejects(string text)
    {
        Assert.False(ReceiverAddress.TryParse(text, out _, out _));
    }

    [Fact]
    public void Receiver_uri_brackets_ipv6()
    {
        Assert.Equal("ws://[fe80::1]:47800/", ReceiverAddress.ToUri("fe80::1", 47800).ToString());
        Assert.Equal("ws://10.0.0.2:47800/", ReceiverAddress.ToUri("10.0.0.2", 47800).ToString());
    }
}
