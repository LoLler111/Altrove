using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Altrove.Core.Protocol;

namespace Altrove.Core.Discovery;

public sealed record DiscoveredReceiver(string Id, string Name, string Host, int Port, int Protocol, DateTimeOffset LastSeen)
{
    public string Address => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>The mDNS instance name, without the service.</summary>
    internal string Instance { get; init; } = "";

    /// <summary>True when the response had no TXT id, so <see cref="Id"/> is just the address.</summary>
    internal bool IdFromAddress { get; init; }
}

/// <summary>
/// Finds Altrove receivers on the local network: asks for _altrove._tcp.local every few seconds, from an
/// ordinary port on each IPv4 interface, so receivers answer straight back to it (RFC 6762 §6.7) and
/// nothing has to share port 5353 with Windows' own mDNS service.
/// </summary>
/// <remarks>
/// The question goes to the multicast group, and also straight to every address of the local network
/// (at most a /24) every <see cref="SweepInterval"/>: many routers drop multicast between two Wi-Fi devices
/// while passing their ordinary traffic, and Android answers a question sent to it directly. Who answered is
/// then asked directly at every round, so it stays listed.
/// </remarks>
public sealed class MdnsBrowser : IDisposable
{
    private const int MdnsPort = 5353;
    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), MdnsPort);
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan AddressHold = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResponderMemory = TimeSpan.FromMinutes(1);

    private readonly string _service;
    private readonly TimeProvider _time;
    private readonly int _unicastPort;
    private readonly bool _loopbackOnly;
    private readonly ReceiverTable _receivers = new(AddressHold);
    private readonly Dictionary<IPAddress, UdpClient> _sockets = [];
    private readonly Dictionary<IPAddress, int> _prefixes = [];
    // Addresses that answered for our service, and when: asked directly at every round.
    private readonly ConcurrentDictionary<IPAddress, DateTimeOffset> _responders = new();
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;
    private Task? _loop;

    /// <param name="multicast">False asks only directly, address by address: tries that path alone (altrove-cli discover --direct).</param>
    public MdnsBrowser(string serviceType = AltroveProtocol.ServiceType, TimeProvider? time = null, bool multicast = true)
        : this(serviceType, time, MdnsPort, loopbackOnly: false)
    {
        _multicast = multicast;
    }

    /// <summary>For tests: direct questions go to <paramref name="unicastPort"/>, and only 127.0.0.1/24 is searched.</summary>
    internal MdnsBrowser(string serviceType, TimeProvider? time, int unicastPort, bool loopbackOnly)
    {
        _service = serviceType.TrimEnd('.') + ".local";
        _time = time ?? TimeProvider.System;
        _unicastPort = unicastPort;
        _loopbackOnly = loopbackOnly;
        _multicast = !loopbackOnly;
    }

    private readonly bool _multicast;

    /// <summary>Raised on a background thread whenever a receiver appears, changes or disappears.</summary>
    public event Action<IReadOnlyList<DiscoveredReceiver>>? Changed;

    public IReadOnlyList<DiscoveredReceiver> Receivers => _receivers.Receivers;

    public void Start() => _loop ??= Task.Run(() => QueryLoopAsync(_stop.Token));

    private async Task QueryLoopAsync(CancellationToken token)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        while (!token.IsCancellationRequested)
        {
            RefreshSockets(token);
            var query = DnsMessage.BuildQuery(id, _service, DnsType.Ptr);
            if (_multicast)
            {
                foreach (var socket in _sockets.Values.ToList())
                    await SendAsync(socket, query, MdnsGroup, token);
            }
            var now = _time.GetUtcNow();
            if (now - _lastSweep >= SweepInterval)
            {
                _lastSweep = now;
                await SweepAsync(query, token);
            }
            else
                await AskRespondersAsync(query, now, token);
            Expire();
            try
            {
                await Task.Delay(QueryInterval, _time, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task SendAsync(UdpClient socket, byte[] query, IPEndPoint target, CancellationToken token)
    {
        try
        {
            await socket.SendAsync(query, target, token);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The question, straight to every other address of each local network.</summary>
    private async Task SweepAsync(byte[] query, CancellationToken token)
    {
        foreach (var (local, socket) in _sockets.ToList())
        {
            foreach (var target in SweepTargets(local, _prefixes.GetValueOrDefault(local), _loopbackOnly))
                await SendAsync(socket, query, new IPEndPoint(target, _unicastPort), token);
        }
    }

    /// <summary>The question, straight to whoever answered lately, from the interface on its network.</summary>
    private async Task AskRespondersAsync(byte[] query, DateTimeOffset now, CancellationToken token)
    {
        foreach (var (address, lastSeen) in _responders)
        {
            if (now - lastSeen > ResponderMemory)
            {
                _responders.TryRemove(address, out _);
                continue;
            }
            var socket = _sockets.FirstOrDefault(s => SameNetwork(s.Key, _prefixes.GetValueOrDefault(s.Key), address)).Value;
            if (socket is not null)
                await SendAsync(socket, query, new IPEndPoint(address, _unicastPort), token);
        }
    }

    /// <summary>
    /// The other addresses of a private IPv4 network, at most the /24 around <paramref name="local"/> (a bigger
    /// network, such as a virtual switch's, would mean thousands of packets). Nothing for public addresses.
    /// </summary>
    internal static IEnumerable<IPAddress> SweepTargets(IPAddress local, int prefixLength, bool allowLoopback = false)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 1 or > 30)
            yield break;
        var bytes = local.GetAddressBytes();
        var isPrivate = bytes[0] == 10 || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) || (bytes[0] == 192 && bytes[1] == 168);
        if (!isPrivate && !(allowLoopback && bytes[0] == 127))
            yield break;
        var self = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var mask = uint.MaxValue << (32 - Math.Max(prefixLength, 24));
        var network = self & mask;
        var broadcast = network | ~mask;
        for (var host = network + 1; host < broadcast; host++)
        {
            if (host == self)
                continue;
            var target = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(target, host);
            yield return new IPAddress(target);
        }
    }

    internal static bool SameNetwork(IPAddress local, int prefixLength, IPAddress other)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || other.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 1 or > 32)
            return false;
        var mask = uint.MaxValue << (32 - prefixLength);
        return (BinaryPrimitives.ReadUInt32BigEndian(local.GetAddressBytes()) & mask)
            == (BinaryPrimitives.ReadUInt32BigEndian(other.GetAddressBytes()) & mask);
    }

    /// <summary>mDNS packets seen so far, any service: zero means nothing reaches us (firewall, no multicast).</summary>
    public int PacketsReceived => _packets;

    private int _packets;
    private UdpClient? _shared;
    private readonly HashSet<IPAddress> _joined = [];

    /// <summary>
    /// Many responders (Windows' own among them) answer on the multicast group even to a query from another
    /// port: listen there too, sharing 5353 with the system's mDNS service.
    /// </summary>
    private void RefreshSharedListener(IEnumerable<IPAddress> addresses, CancellationToken token)
    {
        if (_shared is null)
        {
            try
            {
                var shared = new UdpClient(AddressFamily.InterNetwork);
                shared.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                shared.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsGroup.Port));
                _shared = shared;
                _ = Task.Run(() => ReceiveLoopAsync(shared, token), token);
            }
            catch (SocketException)
            {
                return; // the port is held exclusively: unicast answers still work
            }
        }
        foreach (var address in addresses.Where(a => !_joined.Contains(a)))
        {
            try
            {
                _shared.JoinMulticastGroup(MdnsGroup.Address, address);
                _joined.Add(address);
            }
            catch (SocketException)
            {
            }
        }
    }

    private void RefreshSockets(CancellationToken token)
    {
        var addresses = _loopbackOnly
            ? new Dictionary<IPAddress, int> { [IPAddress.Loopback] = 8 }
            : LocalIPv4Addresses().GroupBy(a => a.Address).ToDictionary(g => g.Key, g => g.First().PrefixLength);
        var current = addresses.Keys.ToHashSet();
        foreach (var (address, prefix) in addresses)
            _prefixes[address] = prefix;
        if (_multicast)
            RefreshSharedListener(current, token);
        foreach (var gone in _sockets.Keys.Where(a => !current.Contains(a)).ToList())
        {
            _sockets[gone].Dispose();
            _sockets.Remove(gone);
            _prefixes.Remove(gone);
        }
        foreach (var address in current.Where(a => !_sockets.ContainsKey(a)))
        {
            try
            {
                var socket = new UdpClient(AddressFamily.InterNetwork);
                if (!_loopbackOnly)
                {
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                }
                if (OperatingSystem.IsWindows())
                    socket.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0, 0, 0, 0], null);
                socket.Client.Bind(new IPEndPoint(address, 0));
                _sockets[address] = socket;
                _ = Task.Run(() => ReceiveLoopAsync(socket, token), token);
            }
            catch (SocketException)
            {
                // An interface that cannot multicast (some VPNs): skip it.
            }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            Interlocked.Increment(ref _packets);
            var found = Interpret(result.Buffer, result.RemoteEndPoint.Address, _service, _time.GetUtcNow());
            if (found.Count > 0)
            {
                var source = result.RemoteEndPoint.Address;
                _responders[source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source] = _time.GetUtcNow();
            }
            var changed = false;
            foreach (var receiver in found)
                changed |= _receivers.Update(receiver);
            if (changed)
                Changed?.Invoke(Receivers);
        }
    }

    private void Expire()
    {
        if (_receivers.Expire(_time.GetUtcNow() - Expiry))
            Changed?.Invoke(Receivers);
    }

    /// <summary>Turns one mDNS response into the receivers it describes.</summary>
    internal static List<DiscoveredReceiver> Interpret(byte[] packet, IPAddress source, string service, DateTimeOffset now)
    {
        var records = DnsMessage.ParseRecords(packet);
        var result = new List<DiscoveredReceiver>();
        var instances = records
            .Where(r => r.Type == DnsType.Ptr && r.Name.Equals(service, StringComparison.OrdinalIgnoreCase))
            .Select(SafeName)
            .Where(n => n is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in instances)
        {
            var srv = records.FirstOrDefault(r => r.Type == DnsType.Srv && r.Name.Equals(instance, StringComparison.OrdinalIgnoreCase));
            var txt = records.FirstOrDefault(r => r.Type == DnsType.Txt && r.Name.Equals(instance, StringComparison.OrdinalIgnoreCase))?.ReadTxt()
                ?? new Dictionary<string, string>();
            var port = AltroveProtocol.DefaultPort;
            if (srv is not null)
            {
                try
                {
                    port = srv.ReadSrv().Port;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
                {
                }
            }
            if (srv?.Ttl == 0)
                continue; // a "goodbye" packet: the service is going away

            // The packet's own source is an address we can certainly reach; A records may list others.
            var host = source.IsIPv4MappedToIPv6 ? source.MapToIPv4().ToString() : source.ToString();
            var label = instance![..^(service.Length + 1)];
            var name = txt.GetValueOrDefault("name") is { Length: > 0 } n ? n : label;
            var ownId = txt.GetValueOrDefault("id") is { Length: > 0 } i ? i : null;
            var protocol = int.TryParse(txt.GetValueOrDefault("protocol"), out var p) ? p : AltroveProtocol.Version;
            result.Add(new DiscoveredReceiver(ownId ?? $"{host}:{port}", name, host, port, protocol, now)
            {
                Instance = label,
                IdFromAddress = ownId is null,
            });
        }
        return result;
    }

    private static string? SafeName(DnsRecord record)
    {
        try
        {
            return record.ReadName();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IEnumerable<(IPAddress Address, int PrefixLength)> LocalIPv4Addresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || !nic.SupportsMulticast)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                    yield return (unicast.Address, unicast.PrefixLength);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var socket in _sockets.Values)
            socket.Dispose();
        _sockets.Clear();
        _shared?.Dispose();
        _stop.Dispose();
    }
}
