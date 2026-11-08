namespace Altrove.Core.Discovery;

/// <summary>
/// The receivers found so far, one entry per device, fed with what <see cref="MdnsBrowser.Interpret"/> reads.
/// </summary>
/// <remarks>
/// A response may lack the TXT record (responders leave out what they sent a moment ago), and then the receiver
/// has no id of its own, only its address. Such a sighting refreshes the device already known by that instance
/// name or address instead of becoming a second entry; one listed by address alone is dropped as soon as the
/// same device shows up with its id.
/// </remarks>
internal sealed class ReceiverTable(TimeSpan addressHold)
{
    private readonly Dictionary<string, DiscoveredReceiver> _receivers = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<DiscoveredReceiver> Receivers
    {
        get
        {
            lock (_lock)
                return _receivers.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Records one sighting; true when the list changed (not just the time last seen).</summary>
    public bool Update(DiscoveredReceiver receiver)
    {
        lock (_lock)
        {
            var changed = false;
            if (receiver.IdFromAddress)
            {
                if (_receivers.Values.FirstOrDefault(r => !r.IdFromAddress && SameDevice(r, receiver)) is { } known)
                    receiver = known with { Host = receiver.Host, LastSeen = receiver.LastSeen }; // the port may be a guess too
            }
            else
            {
                foreach (var standIn in _receivers.Values.Where(r => r.IdFromAddress && SameDevice(r, receiver)).ToList())
                    changed |= _receivers.Remove(standIn.Id);
            }

            var previous = _receivers.GetValueOrDefault(receiver.Id);
            // A device answering on two networks (LAN and a virtual switch) would flip between addresses:
            // keep the one we have while it is still answering there, switch only once it falls silent.
            if (previous is not null && previous.Host != receiver.Host && receiver.LastSeen - previous.LastSeen < addressHold)
                return changed;
            _receivers[receiver.Id] = receiver;
            return changed || previous is null || previous with { LastSeen = receiver.LastSeen } != receiver;
        }
    }

    /// <summary>Forgets receivers not seen since <paramref name="limit"/>; true when any went.</summary>
    public bool Expire(DateTimeOffset limit)
    {
        lock (_lock)
        {
            var removed = false;
            foreach (var receiver in _receivers.Values.Where(r => r.LastSeen < limit).ToList())
                removed |= _receivers.Remove(receiver.Id);
            return removed;
        }
    }

    // mDNS instance names are unique on the network, and so is an address with its port.
    private static bool SameDevice(DiscoveredReceiver a, DiscoveredReceiver b) =>
        (a.Instance.Length > 0 && a.Instance.Equals(b.Instance, StringComparison.OrdinalIgnoreCase))
        || (a.Host == b.Host && a.Port == b.Port);
}
