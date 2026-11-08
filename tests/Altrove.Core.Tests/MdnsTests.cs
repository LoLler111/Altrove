using System.Buffers.Binary;
using System.Net;
using System.Text;
using Altrove.Core.Discovery;
using Altrove.Core.Protocol;

namespace Altrove.Core.Tests;

public class MdnsTests
{
    private const string Service = "_altrove._tcp.local";

    [Fact]
    public void Query_has_one_ptr_question()
    {
        var query = DnsMessage.BuildQuery(0x1234, Service, DnsType.Ptr);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(query));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal("_altrove._tcp.local", DnsMessage.ReadName(query, 12, out var next));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(next)));
        Assert.Equal(query.Length, next + 4);
    }

    [Fact]
    public void Response_with_compressed_names_gives_the_receiver()
    {
        var packet = new ResponseBuilder()
            .Ptr(Service, "Tab A Altrove." + Service)
            .Srv("Tab A Altrove." + Service, 47801, "android-1234.local")
            .Txt("Tab A Altrove." + Service, "id=abc123", "name=Tab A", "protocol=1")
            .A("android-1234.local", [192, 168, 1, 50])
            .Build();

        var found = MdnsBrowser.Interpret(packet, IPAddress.Parse("192.168.1.50"), Service, DateTimeOffset.UnixEpoch);

        var receiver = Assert.Single(found);
        Assert.Equal("abc123", receiver.Id);
        Assert.Equal("Tab A", receiver.Name);
        Assert.Equal("192.168.1.50", receiver.Host);
        Assert.Equal(47801, receiver.Port);
        Assert.Equal("192.168.1.50:47801", receiver.Address);
    }

    [Fact]
    public void Missing_txt_and_srv_fall_back_to_instance_name_and_default_port()
    {
        var packet = new ResponseBuilder().Ptr(Service, "Fire TV." + Service).Build();
        var receiver = Assert.Single(MdnsBrowser.Interpret(packet, IPAddress.Parse("10.0.0.9"), Service, DateTimeOffset.UnixEpoch));
        Assert.Equal("Fire TV", receiver.Name);
        Assert.Equal(47800, receiver.Port);
        Assert.Equal("10.0.0.9:47800", receiver.Id);
    }

    private static DiscoveredReceiver Sighting(bool withTxt, string from = "192.168.1.50", int seconds = 0, string instance = "Tab A")
    {
        var builder = new ResponseBuilder()
            .Ptr(Service, instance + "." + Service)
            .Srv(instance + "." + Service, 47800, "android.local");
        if (withTxt)
            builder.Txt(instance + "." + Service, "id=tab-a", "name=Tab A", "protocol=1");
        return Assert.Single(MdnsBrowser.Interpret(builder.Build(), IPAddress.Parse(from), Service, DateTimeOffset.UnixEpoch.AddSeconds(seconds)));
    }

    [Fact]
    public void Answer_without_txt_refreshes_the_known_receiver_instead_of_adding_it_twice()
    {
        var table = new ReceiverTable(TimeSpan.FromSeconds(7));
        Assert.True(table.Update(Sighting(withTxt: true)));
        Assert.False(table.Update(Sighting(withTxt: false, seconds: 3)));

        var receiver = Assert.Single(table.Receivers);
        Assert.Equal("tab-a", receiver.Id);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(3), receiver.LastSeen);
    }

    [Fact]
    public void Receiver_first_seen_without_txt_is_replaced_once_its_id_arrives()
    {
        var table = new ReceiverTable(TimeSpan.FromSeconds(7));
        table.Update(Sighting(withTxt: false));
        Assert.Equal("192.168.1.50:47800", Assert.Single(table.Receivers).Id);

        Assert.True(table.Update(Sighting(withTxt: true, seconds: 1)));
        Assert.Equal("tab-a", Assert.Single(table.Receivers).Id);
    }

    [Fact]
    public void Answer_without_txt_from_the_other_network_is_matched_by_instance_name()
    {
        var table = new ReceiverTable(TimeSpan.FromSeconds(7));
        table.Update(Sighting(withTxt: true));
        table.Update(Sighting(withTxt: false, from: "172.20.3.9", seconds: 1));
        Assert.Equal("192.168.1.50", Assert.Single(table.Receivers).Host);

        // Silent on the first network for longer than the hold: the other address takes over, same entry.
        table.Update(Sighting(withTxt: false, from: "172.20.3.9", seconds: 9));
        var receiver = Assert.Single(table.Receivers);
        Assert.Equal(("tab-a", "172.20.3.9"), (receiver.Id, receiver.Host));
    }

    [Fact]
    public void Two_devices_with_the_same_name_stay_two()
    {
        var table = new ReceiverTable(TimeSpan.FromSeconds(7));
        table.Update(Sighting(withTxt: true));
        table.Update(Sighting(withTxt: false, from: "192.168.1.51", instance: "Tab A (2)"));
        Assert.Equal(2, table.Receivers.Count);
    }

    [Fact]
    public void Silent_receivers_expire()
    {
        var table = new ReceiverTable(TimeSpan.FromSeconds(7));
        table.Update(Sighting(withTxt: true));
        Assert.False(table.Expire(DateTimeOffset.UnixEpoch));
        Assert.True(table.Expire(DateTimeOffset.UnixEpoch.AddSeconds(1)));
        Assert.Empty(table.Receivers);
    }

    [Fact]
    public void Other_services_queries_and_garbage_are_ignored()
    {
        var other = new ResponseBuilder().Ptr("_googlecast._tcp.local", "TV._googlecast._tcp.local").Build();
        Assert.Empty(MdnsBrowser.Interpret(other, IPAddress.Loopback, Service, DateTimeOffset.UnixEpoch));
        Assert.Empty(MdnsBrowser.Interpret(DnsMessage.BuildQuery(1, Service, DnsType.Ptr), IPAddress.Loopback, Service, DateTimeOffset.UnixEpoch));
        Assert.Empty(MdnsBrowser.Interpret([0x00, 0x01, 0x84, 0x00, 0, 0, 0, 5, 0, 0, 0, 0, 0xC0], IPAddress.Loopback, Service, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Compression_loops_do_not_hang()
    {
        byte[] packet = [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12];
        Assert.Empty(DnsMessage.ParseRecords(packet));
    }

    [Fact]
    public void Sweep_covers_the_rest_of_a_home_network()
    {
        var targets = MdnsBrowser.SweepTargets(IPAddress.Parse("192.168.1.249"), 24).Select(a => a.ToString()).ToList();
        Assert.Equal(253, targets.Count);
        Assert.Contains("192.168.1.69", targets);
        Assert.DoesNotContain("192.168.1.249", targets); // ourselves
        Assert.DoesNotContain("192.168.1.0", targets);
        Assert.DoesNotContain("192.168.1.255", targets);
    }

    [Fact]
    public void Sweep_of_a_big_network_stays_in_our_slash_24()
    {
        // Hyper-V's Default Switch is a /20: 4094 addresses would be too many packets.
        var targets = MdnsBrowser.SweepTargets(IPAddress.Parse("172.20.3.7"), 20).ToList();
        Assert.Equal(253, targets.Count);
        Assert.All(targets, a => Assert.StartsWith("172.20.3.", a.ToString()));
    }

    [Theory]
    [InlineData("8.8.4.4", 24)]       // public: not our business
    [InlineData("169.254.10.1", 16)]  // link-local, no DHCP: nothing worth asking
    [InlineData("192.168.1.10", 31)]  // point-to-point
    [InlineData("192.168.1.10", 0)]   // prefix unknown
    public void Sweep_skips_networks_that_are_not_ours(string address, int prefix)
    {
        Assert.Empty(MdnsBrowser.SweepTargets(IPAddress.Parse(address), prefix));
    }

    [Fact]
    public void Small_network_sweep_leaves_out_network_and_broadcast()
    {
        Assert.Equal(["10.0.0.6"], MdnsBrowser.SweepTargets(IPAddress.Parse("10.0.0.5"), 30).Select(a => a.ToString()));
    }

    [Fact]
    public void Same_network_follows_the_prefix()
    {
        Assert.True(MdnsBrowser.SameNetwork(IPAddress.Parse("192.168.1.249"), 24, IPAddress.Parse("192.168.1.69")));
        Assert.False(MdnsBrowser.SameNetwork(IPAddress.Parse("192.168.1.249"), 24, IPAddress.Parse("192.168.2.69")));
        Assert.True(MdnsBrowser.SameNetwork(IPAddress.Parse("10.1.2.3"), 8, IPAddress.Parse("10.200.0.1")));
    }

    [Fact]
    public async Task Receiver_that_answers_direct_questions_is_found_without_multicast()
    {
        // A receiver on 127.0.0.2 that, like Android, answers a question sent straight to it, back to its port.
        using var receiver = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var answer = new ResponseBuilder()
            .Ptr(Service, "Tab A." + Service)
            .Srv("Tab A." + Service, 47800, "android.local")
            .Txt("Tab A." + Service, "id=tab-a", "name=Tab A", "protocol=1")
            .Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var questions = 0;
        var answering = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var question = await receiver.ReceiveAsync(stop.Token);
                Interlocked.Increment(ref questions);
                await receiver.SendAsync(answer, question.RemoteEndPoint, stop.Token);
            }
        });

        using var browser = new MdnsBrowser(AltroveProtocol.ServiceType, null, port, loopbackOnly: true);
        var found = new TaskCompletionSource<DiscoveredReceiver>();
        browser.Changed += list =>
        {
            if (list.FirstOrDefault() is { } first)
                found.TrySetResult(first);
        };
        browser.Start();

        var receiverFound = await found.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("tab-a", receiverFound.Id);
        Assert.Equal("Tab A", receiverFound.Name);
        Assert.Equal("127.0.0.2:47800", receiverFound.Address);
        Assert.True(questions >= 1);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answering);
    }

    /// <summary>Builds a response the way mDNSResponder does, compressing every repeated name.</summary>
    private sealed class ResponseBuilder
    {
        private readonly List<byte> _bytes = [0, 0, 0x84, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];
        private readonly Dictionary<string, int> _names = new(StringComparer.OrdinalIgnoreCase);
        private int _count;

        public ResponseBuilder Ptr(string name, string target) => Record(name, DnsType.Ptr, data => Name(data, target));

        public ResponseBuilder Srv(string name, ushort port, string target) => Record(name, DnsType.Srv, data =>
        {
            data.AddRange([0, 0, 0, 0, (byte)(port >> 8), (byte)port]);
            Name(data, target);
        });

        public ResponseBuilder Txt(string name, params string[] entries) => Record(name, DnsType.Txt, data =>
        {
            foreach (var entry in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(entry);
                data.Add((byte)bytes.Length);
                data.AddRange(bytes);
            }
        });

        public ResponseBuilder A(string name, byte[] address) => Record(name, DnsType.A, data => data.AddRange(address));

        public byte[] Build()
        {
            var result = _bytes.ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)_count);
            return result;
        }

        private ResponseBuilder Record(string name, DnsType type, Action<List<byte>> writeData)
        {
            Name(_bytes, name);
            _bytes.AddRange([0, (byte)type, 0x80, 1, 0, 0, 0x11, 0x94]);
            var lengthAt = _bytes.Count;
            _bytes.AddRange([0, 0]);
            // Data names may point back into the message, so they are written straight into it.
            writeData(_bytes);
            var length = _bytes.Count - lengthAt - 2;
            _bytes[lengthAt] = (byte)(length >> 8);
            _bytes[lengthAt + 1] = (byte)length;
            _count++;
            return this;
        }

        private void Name(List<byte> data, string name)
        {
            var labels = name.Split('.');
            for (var i = 0; i < labels.Length; i++)
            {
                var suffix = string.Join('.', labels[i..]);
                if (_names.TryGetValue(suffix, out var at))
                {
                    data.Add((byte)(0xC0 | (at >> 8)));
                    data.Add((byte)at);
                    return;
                }
                _names[suffix] = data.Count;
                var bytes = Encoding.UTF8.GetBytes(labels[i]);
                data.Add((byte)bytes.Length);
                data.AddRange(bytes);
            }
            data.Add(0);
        }
    }
}
