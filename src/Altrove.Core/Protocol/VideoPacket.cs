using System.Buffers.Binary;

namespace Altrove.Core.Protocol;

[Flags]
public enum VideoPacketFlags : byte
{
    None = 0,
    Keyframe = 1,
    ParameterSets = 2,
}

/// <summary>
/// One binary WebSocket message: a compressed frame with a 10-byte header (docs/PROTOCOLLO.md).
/// </summary>
public static class VideoPacket
{
    public const byte KindVideo = 1;
    public const byte KindAudio = 2;
    public const byte KindMic = 3;
    public const int HeaderSize = 10;

    public static byte[] Encode(ReadOnlySpan<byte> accessUnit, long presentationMicros, VideoPacketFlags flags)
    {
        var packet = new byte[HeaderSize + accessUnit.Length];
        packet[0] = KindVideo;
        packet[1] = (byte)flags;
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(2, 8), presentationMicros);
        accessUnit.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>An audio packet (kind 2): an Opus packet or a block of PCM, after the same 10-byte header.</summary>
    public static byte[] EncodeAudio(ReadOnlySpan<byte> payload, long presentationMicros)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = KindAudio;
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(2, 8), presentationMicros);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    public static bool TryDecode(ReadOnlyMemory<byte> packet, out VideoPacketFlags flags, out long presentationMicros, out ReadOnlyMemory<byte> accessUnit)
    {
        flags = VideoPacketFlags.None;
        presentationMicros = 0;
        accessUnit = default;
        var span = packet.Span;
        if (span.Length < HeaderSize || span[0] != KindVideo)
            return false;
        flags = (VideoPacketFlags)span[1];
        presentationMicros = BinaryPrimitives.ReadInt64BigEndian(span.Slice(2, 8));
        accessUnit = packet[HeaderSize..];
        return true;
    }
}
