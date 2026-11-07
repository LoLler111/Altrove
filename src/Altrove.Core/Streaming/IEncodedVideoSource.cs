namespace Altrove.Core.Streaming;

/// <summary>One compressed H.264 access unit in Annex B format.</summary>
public readonly record struct EncodedFrame(ReadOnlyMemory<byte> Data, long TimestampMicros, bool IsKeyframe);

public sealed record EncoderSettings(int Width, int Height, int Fps, int BitrateKbps);

/// <summary>Something that produces H.264: the screen capture + encoder on Windows, a fake one in tests.</summary>
public interface IEncodedVideoSource : IAsyncDisposable
{
    /// <summary>Size of what is captured, before any scaling.</summary>
    (int Width, int Height) SourceSize { get; }

    /// <summary>Human-readable name of the encoder in use, e.g. "AMD H.264 (hardware)".</summary>
    string EncoderName { get; }

    /// <summary>Raised on a background thread for every encoded frame, in presentation order.</summary>
    event Action<EncodedFrame>? FrameEncoded;

    /// <summary>Raised when capture or encoding fails for good.</summary>
    event Action<Exception>? Failed;

    Task StartAsync(EncoderSettings settings, CancellationToken token);

    /// <summary>Asks for the next frame to be an IDR, re-encoding the last image if the screen is still.</summary>
    void RequestKeyframe();

    /// <summary>Changes the mean bitrate while encoding (adaptive bitrate); ignored by encoders that cannot.</summary>
    void SetBitrate(int kbps);
}
