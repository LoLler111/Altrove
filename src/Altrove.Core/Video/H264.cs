namespace Altrove.Core.Video;

/// <summary>Just enough H.264 Annex B parsing to label frames and find SPS/PPS.</summary>
public static class H264
{
    public const int NalSlice = 1;
    public const int NalIdr = 5;
    public const int NalSei = 6;
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalAud = 9;

    /// <summary>Each NAL unit in an Annex B buffer, without its start code.</summary>
    public static IEnumerable<ReadOnlyMemory<byte>> NalUnits(ReadOnlyMemory<byte> annexB)
    {
        var start = NextStart(annexB.Span, 0, out var codeLength);
        while (start >= 0)
        {
            var payload = start + codeLength;
            var next = NextStart(annexB.Span, payload, out var nextCodeLength);
            var end = next >= 0 ? next : annexB.Length;
            if (end > payload)
                yield return annexB[payload..end];
            start = next;
            codeLength = nextCodeLength;
        }
    }

    public static int NalType(ReadOnlySpan<byte> nal) => nal.IsEmpty ? -1 : nal[0] & 0x1F;

    public static (bool HasIdr, bool HasParameterSets) Describe(ReadOnlyMemory<byte> annexB)
    {
        bool idr = false, sps = false, pps = false;
        foreach (var nal in NalUnits(annexB))
        {
            switch (NalType(nal.Span))
            {
                case NalIdr: idr = true; break;
                case NalSps: sps = true; break;
                case NalPps: pps = true; break;
            }
        }
        return (idr, sps && pps);
    }

    /// <summary>Concatenates NAL units back into Annex B with 4-byte start codes.</summary>
    public static byte[] ToAnnexB(IEnumerable<ReadOnlyMemory<byte>> nals)
    {
        var list = nals.ToList();
        var result = new byte[list.Sum(n => n.Length + 4)];
        var offset = 0;
        foreach (var nal in list)
        {
            result[offset + 3] = 1;
            nal.Span.CopyTo(result.AsSpan(offset + 4));
            offset += nal.Length + 4;
        }
        return result;
    }

    // Finds 00 00 01 or 00 00 00 01 at or after `from`; returns the index of the first zero.
    private static int NextStart(ReadOnlySpan<byte> data, int from, out int codeLength)
    {
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
                continue;
            if (data[i + 2] == 1)
            {
                if (i > from && data[i - 1] == 0)
                {
                    codeLength = 4;
                    return i - 1;
                }
                codeLength = 3;
                return i;
            }
        }
        codeLength = 0;
        return -1;
    }
}
