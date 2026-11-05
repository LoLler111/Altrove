using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Altrove.Core.Protocol;

namespace Altrove.Core.Discovery;

public sealed record DiscoveredReceiver(string Id, string Name, string Host, int Port, int Protocol, DateTimeOffset LastSeen)
{
    public string Address => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>
/// Finds Altrove receivers on the local network: asks for _altrove._tcp.local every few seconds, from an
/// ordinary port on each IPv4 interface, so receivers answer straight back to it (RFC 6762 §6.7) and
/// nothing has to share port 5353 with Windows' own mDNS service.
/// </summary>
public sealed class MdnsBrowser : IDisposable
{
    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan AddressHold = TimeSpan.FromSeconds(7);

    private readonly string _service;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DiscoveredReceiver> _receivers = new();
    private readonly Dictionary<IPAddress, UdpClient> _sockets = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public MdnsBrowser(string serviceType = AltroveProtocol.ServiceType, TimeProvider? time = null)
    {
        _service = serviceType.TrimEnd('.') + ".local";
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread whenever a receiver appears, changes or disappears.</summary>
    public event Action<IReadOnlyList<DiscoveredReceiver>>? Changed;

    public IReadOnlyList<DiscoveredReceiver> Receivers =>
        _receivers.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public void Start() => _loop ??= Task.Run(() => QueryLoopAsync(_stop.Token));

    private async Task QueryLoopAsync(CancellationToken token)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        while (!token.IsCancellationRequested)
        {
            RefreshSockets(token);
            var query = DnsMessage.BuildQuery(id, _service, DnsType.Ptr);
            foreach (var socket in _sockets.Values.ToList())
            {
                try
                {
                    await socket.SendAsync(query, MdnsGroup, token);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                }
            }
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
        var current = LocalIPv4Addresses().ToHashSet();
        RefreshSharedListener(current, token);
        foreach (var gone in _sockets.Keys.Where(a => !current.Contains(a)).ToList())
        {
            _sockets[gone].Dispose();
            _sockets.Remove(gone);
        }
        foreach (var address in current.Where(a => !_sockets.ContainsKey(a)))
        {
            try
            {
                var socket = new UdpClient(AddressFamily.InterNetwork);
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
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
            var changed = false;
            foreach (var found1 in found)
            {
                var receiver = found1;
                var previous = _receivers.GetValueOrDefault(receiver.Id);
                // A device answering on two networks (LAN and a virtual switch) would flip between addresses:
                // keep the one we have while it is still answering there, switch only once it falls silent.
                if (previous is not null && previous.Host != receiver.Host && receiver.LastSeen - previous.LastSeen < AddressHold)
                    continue;
                _receivers[receiver.Id] = receiver;
                changed |= previous is null || previous with { LastSeen = receiver.LastSeen } != receiver;
            }
            if (changed)
                Changed?.Invoke(Receivers);
        }
    }

    private void Expire()
    {
        var limit = _time.GetUtcNow() - Expiry;
        var removed = false;
        foreach (var (id, receiver) in _receivers)
        {
            if (receiver.LastSeen < limit)
                removed |= _receivers.TryRemove(id, out _);
        }
        if (removed)
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
            var id = txt.GetValueOrDefault("id") is { Length: > 0 } i ? i : $"{host}:{port}";
            var protocol = int.TryParse(txt.GetValueOrDefault("protocol"), out var p) ? p : AltroveProtocol.Version;
            result.Add(new DiscoveredReceiver(id, name, host, port, protocol, now));
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

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
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
                    yield return unicast.Address;
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
