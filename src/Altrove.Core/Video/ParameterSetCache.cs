namespace Altrove.Core.Video;

/// <summary>
/// Remembers the latest SPS and PPS and puts them in front of any IDR that lacks them, so every
/// keyframe is a point where a receiver can start decoding (docs/PROTOCOLLO.md).
/// </summary>
public sealed class ParameterSetCache
{
    private ReadOnlyMemory<byte>? _sps, _pps;

    public ReadOnlyMemory<byte> Complete(ReadOnlyMemory<byte> accessUnit)
    {
        bool idr = false, hasSps = false, hasPps = false;
        foreach (var nal in H264.NalUnits(accessUnit))
        {
            switch (H264.NalType(nal.Span))
            {
                case H264.NalSps:
                    _sps = nal.ToArray();
                    hasSps = true;
                    break;
                case H264.NalPps:
                    _pps = nal.ToArray();
                    hasPps = true;
                    break;
                case H264.NalIdr:
                    idr = true;
                    break;
            }
        }
        if (!idr || (hasSps && hasPps) || _sps is null || _pps is null)
            return accessUnit;
        var prefix = H264.ToAnnexB([_sps.Value, _pps.Value]);
        var result = new byte[prefix.Length + accessUnit.Length];
        prefix.CopyTo(result, 0);
        accessUnit.Span.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }
}
