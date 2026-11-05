using System.Buffers.Binary;
using System.Net;
using System.Text;
using Altrove.Core.Discovery;

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
