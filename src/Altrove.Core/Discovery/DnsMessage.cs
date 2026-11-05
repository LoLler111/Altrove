using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Altrove.Core.Discovery;

internal enum DnsType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
}

internal sealed record DnsRecord(string Name, DnsType Type, uint Ttl, ReadOnlyMemory<byte> Data, int DataOffset, byte[] Message)
{
    public string ReadName() => DnsMessage.ReadName(Message, DataOffset, out _);

    public (ushort Port, string Target) ReadSrv()
    {
        var port = BinaryPrimitives.ReadUInt16BigEndian(Data.Span.Slice(4, 2));
        return (port, DnsMessage.ReadName(Message, DataOffset + 6, out _));
    }

    public Dictionary<string, string> ReadTxt()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var span = Data.Span;
        var i = 0;
        while (i < span.Length)
        {
            int length = span[i++];
            if (i + length > span.Length)
                break;
            var entry = Encoding.UTF8.GetString(span.Slice(i, length));
            i += length;
            var equals = entry.IndexOf('=');
            if (equals > 0)
                result[entry[..equals]] = entry[(equals + 1)..];
            else if (entry.Length > 0)
                result[entry] = "";
        }
        return result;
    }

    public IPAddress? ReadAddress() => Type switch
    {
        DnsType.A when Data.Length == 4 => new IPAddress(Data.Span),
        DnsType.Aaaa when Data.Length == 16 => new IPAddress(Data.Span),
        _ => null,
    };
}

/// <summary>The few bits of the DNS wire format (RFC 1035) that mDNS service browsing needs.</summary>
internal static class DnsMessage
{
    public static byte[] BuildQuery(ushort id, string name, DnsType type)
    {
        var bytes = new List<byte>(64);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // one question
        bytes.AddRange(header.ToArray());
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            if (encoded.Length is 0 or > 63)
                throw new ArgumentException($"Invalid DNS label in '{name}'.", nameof(name));
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.Add(0);
        bytes.Add(0);
        bytes.Add((byte)type);
        bytes.Add(0);
        bytes.Add(1); // class IN
        return bytes.ToArray();
    }

    /// <summary>All resource records (answers, authority, additional) in a response; empty for queries or garbage.</summary>
    public static List<DnsRecord> ParseRecords(byte[] message)
    {
        var records = new List<DnsRecord>();
        try
        {
            if (message.Length < 12)
                return records;
            var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2));
            if ((flags & 0x8000) == 0)
                return records; // a query, not a response
            int questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
            var total = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6))
                + BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(8))
                + BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10));
            var offset = 12;
            for (var q = 0; q < questions; q++)
            {
                ReadName(message, offset, out offset);
                offset += 4;
            }
            for (var r = 0; r < total; r++)
            {
                var name = ReadName(message, offset, out offset);
                var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
                var ttl = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 4));
                int length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
                var dataOffset = offset + 10;
                if (dataOffset + length > message.Length)
                    break;
                records.Add(new DnsRecord(name, type, ttl, message.AsMemory(dataOffset, length), dataOffset, message));
                offset = dataOffset + length;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
        {
            // A truncated or malformed packet: keep what was readable.
        }
        return records;
    }

    /// <summary>Reads a possibly compressed name; <paramref name="next"/> is the offset just after it in the record.</summary>
    public static string ReadName(byte[] message, int offset, out int next)
    {
        var labels = new List<string>();
        next = -1;
        var jumps = 0;
        while (true)
        {
            if (offset >= message.Length)
                throw new FormatException("Name runs past the end of the message.");
            int length = message[offset];
            if (length == 0)
            {
                if (next < 0)
                    next = offset + 1;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= message.Length || ++jumps > 32)
                    throw new FormatException("Bad name compression.");
                if (next < 0)
                    next = offset + 2;
                offset = ((length & 0x3F) << 8) | message[offset + 1];
                continue;
            }
            if (offset + 1 + length > message.Length)
                throw new FormatException("Label runs past the end of the message.");
            labels.Add(Encoding.UTF8.GetString(message, offset + 1, length));
            offset += 1 + length;
        }
        return string.Join('.', labels);
    }
}
