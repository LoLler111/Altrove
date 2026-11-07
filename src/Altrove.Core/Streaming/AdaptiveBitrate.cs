namespace Altrove.Core.Streaming;

/// <summary>One second of what the connection did, for <see cref="AdaptiveBitrate"/>.</summary>
/// <param name="RoundTripMs">The latest ping time, if a pong came back.</param>
/// <param name="Overflows">Times the send queue filled up and was thrown away in that second.</param>
/// <param name="QueuedFrames">Frames waiting to be sent at the end of the second.</param>
/// <param name="FramesSent">Video frames sent in that second: with a still screen nothing is sent, and slow pings mean nothing.</param>
public readonly record struct NetworkSample(double? RoundTripMs, int Overflows, int QueuedFrames, int FramesSent = 1);

/// <summary>
/// The bitrate the encoder should use now, between a floor and the user's choice (the ceiling): down by a
/// third as soon as the network falls behind, back up a tenth of the ceiling at a time once it keeps up.
/// The network falls behind when the send queue overflows or fills up, or when pings take much longer
/// than they did at their best in the last half minute (the socket's buffers are filling) for two seconds in
/// a row while video is going out. A lone slow ping, or slow pings with nothing to send, are Wi-Fi power
/// saving on an idle phone (seen in the emulator too, with spikes over 70 ms on a still screen).
/// </summary>
public sealed class AdaptiveBitrate
{
    /// <summary>Seconds of good network before going up again, and between two steps up.</summary>
    internal const int SecondsBeforeRaise = 5, SecondsBetweenRaises = 3;

    /// <summary>A step down needs this long to show its effect before another one.</summary>
    internal const int SecondsBetweenCuts = 2;

    /// <summary>Queued frames that already mean the network is behind (the queue holds <see cref="SenderSession.MaxQueuedFrames"/>).</summary>
    internal const int QueuedFramesLimit = 4;

    private const int BaselineSeconds = 30;
    private readonly Queue<double> _roundTrips = new();
    private int _goodSeconds, _slowSeconds;
    private int _secondsSinceCut = SecondsBetweenCuts;

    public AdaptiveBitrate(int ceilingKbps, int? startKbps = null)
    {
        CeilingKbps = Math.Max(1, ceilingKbps);
        MinimumKbps = Math.Min(CeilingKbps, Math.Max(1500, CeilingKbps / 5));
        CurrentKbps = Math.Clamp(startKbps ?? CeilingKbps, MinimumKbps, CeilingKbps);
    }

    /// <summary>The user's choice: never more than this.</summary>
    public int CeilingKbps { get; }

    /// <summary>Never less than this: a fifth of the ceiling, at least 1.5 Mbit/s.</summary>
    public int MinimumKbps { get; }

    public int CurrentKbps { get; private set; }

    /// <summary>The best ping time of the last half minute, null before the first pong.</summary>
    public double? BaselineMs => _roundTrips.Count > 0 ? _roundTrips.Min() : null;

    /// <summary>Takes one second of measurements; true when <see cref="CurrentKbps"/> changed.</summary>
    public bool Update(NetworkSample sample)
    {
        var baseline = BaselineMs;
        if (sample.RoundTripMs is { } rtt)
        {
            _roundTrips.Enqueue(rtt);
            while (_roundTrips.Count > BaselineSeconds)
                _roundTrips.Dequeue();
        }
        _secondsSinceCut++;

        // 60 ms over the best, and at least twice it: Wi-Fi jitter alone stays under that.
        var slowPing = sample.FramesSent > 0 && sample.RoundTripMs is { } now && baseline is { } best && now > best + 60 && now > best * 2;
        _slowSeconds = slowPing ? _slowSeconds + 1 : 0;
        if (sample.Overflows > 0 || sample.QueuedFrames >= QueuedFramesLimit || _slowSeconds >= 2)
        {
            _goodSeconds = 0;
            if (_secondsSinceCut < SecondsBetweenCuts || CurrentKbps == MinimumKbps)
                return false;
            _secondsSinceCut = 0;
            return Set(CurrentKbps * 2 / 3);
        }

        if (++_goodSeconds < SecondsBeforeRaise || CurrentKbps == CeilingKbps)
            return false;
        _goodSeconds = SecondsBeforeRaise - SecondsBetweenRaises;
        return Set(CurrentKbps + Math.Max(250, CeilingKbps / 10));
    }

    private bool Set(int kbps)
    {
        var clamped = Math.Clamp(kbps, MinimumKbps, CeilingKbps);
        if (clamped == CurrentKbps)
            return false;
        CurrentKbps = clamped;
        return true;
    }
}
