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
        Assert.Null(ControlMessage.Parse("{\"type\":\"future\",\"x\":1}"));
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
    public void Filler_bytes_count_only_filler_nal_units()
    {
        byte[] data = [0, 0, 0, 1, 0x41, 1, 2, 0, 0, 0, 1, 0x0C, 0xFF, 0xFF, 0xFF, 0x80];
        Assert.Equal(9, H264.FillerBytes(data));
        Assert.Equal(0, H264.FillerBytes(new byte[] { 0, 0, 0, 1, 0x65, 1 }));
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

    // The same JSON as android/app/src/test/.../ProtocolTest.kt.

    [Fact]
    public void Hello_features()
    {
        var hello = Assert.IsType<HelloMessage>(ControlMessage.Parse(
            "{\"type\":\"hello\",\"protocol\":1,\"role\":\"sender\",\"id\":\"pc\",\"name\":\"PC-FISSO\",\"features\":[\"touch\",\"configure\"]}"));
        Assert.True(hello.Supports(Features.Touch));
        Assert.True(hello.Supports(Features.Configure));
        Assert.False(new HelloMessage(1, Roles.Sender, "pc", "PC").Supports(Features.Touch));
    }

    [Fact]
    public void Touch_parses_what_the_receiver_writes()
    {
        var touch = Assert.IsType<TouchMessage>(ControlMessage.Parse(
            "{\"type\":\"touch\",\"pointers\":[{\"id\":0,\"x\":0.25,\"y\":0.5,\"state\":\"down\"},{\"id\":1,\"x\":1.0,\"y\":0.0,\"state\":\"move\"}]}"));
        Assert.Equal(new TouchPointer(0, 0.25, 0.5, TouchStates.Down), touch.Pointers[0]);
        Assert.Equal(new TouchPointer(1, 1, 0, TouchStates.Move), touch.Pointers[1]);
    }

    [Fact]
    public void Configure_carries_only_what_changes()
    {
        var configure = Assert.IsType<ConfigureMessage>(ControlMessage.Parse("{\"type\":\"configure\",\"mode\":\"extend\",\"maxHeight\":720}"));
        Assert.Equal(new ConfigureMessage(Mode: StreamModes.Extend, MaxHeight: 720), configure);
        Assert.Equal("{\"type\":\"configure\",\"fps\":30}", new ConfigureMessage(Fps: 30).ToJson());
    }

    [Fact]
    public void Settings_round_trip()
    {
        var settings = new SettingsMessage(StreamModes.Mirror, "\\\\.\\DISPLAY1", 0, 60, 12000,
            new SettingsChoices([StreamModes.Mirror, StreamModes.Extend], [new ScreenChoice("\\\\.\\DISPLAY1", "Schermo 1")], [0, 1080, 720], [60, 30], [6000, 12000]));
        var json = settings.ToJson();
        Assert.Equal(
            "{\"type\":\"settings\",\"mode\":\"mirror\",\"screen\":\"\\\\\\\\.\\\\DISPLAY1\",\"maxHeight\":0,\"fps\":60,\"bitrateKbps\":12000," +
            "\"choices\":{\"modes\":[\"mirror\",\"extend\"],\"screens\":[{\"id\":\"\\\\\\\\.\\\\DISPLAY1\",\"name\":\"Schermo 1\"}]," +
            "\"maxHeights\":[0,1080,720],\"fps\":[60,30],\"bitratesKbps\":[6000,12000]}}", json);
        var parsed = Assert.IsType<SettingsMessage>(ControlMessage.Parse(json));
        Assert.Equal(settings.Screen, parsed.Screen);
        Assert.Equal(settings.Choices.Screens, parsed.Choices.Screens);
        Assert.Equal(settings.Choices.BitratesKbps, parsed.Choices.BitratesKbps);
    }

    [Fact]
    public void Mouse_and_resolutions_match_the_android_side()
    {
        var move = Assert.IsType<MouseMessage>(ControlMessage.Parse("{\"type\":\"mouse\",\"action\":\"move\",\"dx\":0.0125,\"dy\":-0.004}"));
        Assert.Equal(new MouseMessage(MouseActions.Move, 0.0125, -0.004), move);
        Assert.Equal(new MouseMessage(MouseActions.Click, Button: MouseButtons.Right),
            ControlMessage.Parse("{\"type\":\"mouse\",\"action\":\"click\",\"button\":\"right\"}"));
        Assert.Equal(150, Assert.IsType<ConfigureMessage>(ControlMessage.Parse("{\"type\":\"configure\",\"scale\":150}")).Scale);
        var json = new SettingsMessage(StreamModes.Extend, null, 1200, 60, 12000,
            new SettingsChoices([StreamModes.Mirror], [], [0], [60], [12000], [new ResolutionChoice(1200, 1920, 1200, true)], [100, 125]), Scale: 125).ToJson();
        Assert.Contains("\"resolutions\":[{\"maxHeight\":1200,\"width\":1920,\"height\":1200,\"recommended\":true}]", json);
        Assert.Contains("\"scales\":[100,125]", json);
        Assert.EndsWith("\"scale\":125}", json);
    }

    [Fact]
    public void Audio_messages_and_packets_match_the_android_side()
    {
        Assert.Equal("{\"type\":\"audioStart\",\"codec\":\"opus\",\"sampleRate\":48000,\"channels\":2,\"frameMs\":10}",
            new AudioStartMessage(AudioCodecs.Opus, 48000, 2, 10).ToJson());
        Assert.Equal("{\"type\":\"audioStop\"}", new AudioStopMessage().ToJson());
        var hello = Assert.IsType<HelloMessage>(ControlMessage.Parse(
            "{\"type\":\"hello\",\"protocol\":1,\"role\":\"receiver\",\"id\":\"tab\",\"name\":\"Tab A\",\"audioCodecs\":[\"opus\",\"pcm\"]}"));
        Assert.Equal(["opus", "pcm"], hello.AudioCodecs!);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 1, 0, 9, 8 }, VideoPacket.EncodeAudio([9, 8], 256));
        Assert.False(VideoPacket.TryDecode(VideoPacket.EncodeAudio([9, 8], 256), out _, out _, out _)); // not video
    }
}
