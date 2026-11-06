using Altrove.Core.Protocol;
using Altrove.Core.UsbMic;
using Concentus;

namespace Altrove.Capture.Microphone;

/// <summary>
/// The receiver's microphone as "Microfono Altrove" on Windows: decodes its packets (Opus or PCM) into the jitter
/// buffer that feeds the USB/IP microphone, and keeps the microphone attached while usbip-win2 is installed.
/// </summary>
public sealed class MicBridge : IDisposable
{
    private static readonly TimeSpan ReattachInterval = TimeSpan.FromSeconds(5);
    private readonly MicJitterBuffer _jitter = new();
    private readonly UsbIpMicServer _server;
    private readonly Timer _reattach;
    private readonly object _lock = new();
    private IOpusDecoder? _opus;
    private string _codec = AudioCodecs.Pcm;
    private int _rate = 48_000, _channels = 1;
    private short[] _pcm = new short[5760 * 2];
    private float[] _mono = new float[5760];

    public MicBridge()
    {
        _server = new UsbIpMicServer(_jitter);
        _reattach = new Timer(_ => KeepAttached(), null, TimeSpan.Zero, ReattachInterval);
    }

    /// <summary>Windows shows the microphone.</summary>
    public bool Attached => _server.Attached;

    /// <summary>An app is recording from it right now.</summary>
    public bool Recording => _server.Streaming;

    /// <summary>Packets of the receiver's voice received so far.</summary>
    public long PacketsReceived => Interlocked.Read(ref _packets);

    private long _packets;
    private long _decodedSamples;

    /// <summary>Seconds of the receiver's voice decoded so far.</summary>
    public double SecondsDecoded => Interlocked.Read(ref _decodedSamples) / 48_000.0;

    /// <summary>The last attach error, for the log; null when it went well.</summary>
    public string? AttachError { get; private set; }

    private void KeepAttached()
    {
        if (_server.Attached || !UsbIpSetup.IsInstalled)
            return;
        try
        {
            UsbIpSetup.Attach();
            AttachError = null;
        }
        catch (InvalidOperationException ex)
        {
            AttachError = ex.Message;
        }
    }

    /// <summary>The receiver started its microphone in this format.</summary>
    public void Begin(MicStartMessage format)
    {
        lock (_lock)
        {
            _codec = format.Codec;
            _rate = format.SampleRate;
            _channels = Math.Max(1, format.Channels);
            _opus = _codec == AudioCodecs.Opus ? OpusCodecFactory.CreateDecoder(_rate, _channels) : null;
            _jitter.Clear();
            _server.Feeding = true;
        }
    }

    /// <summary>The receiver stopped: the microphone records silence until it starts again.</summary>
    public void End()
    {
        _server.Feeding = false;
        _jitter.Clear();
    }

    /// <summary>One packet from the receiver (network thread).</summary>
    public void Push(ReadOnlyMemory<byte> packet)
    {
        lock (_lock)
        {
            if (!_server.Feeding)
                return;
            Interlocked.Increment(ref _packets);
            int frames;
            if (_opus is not null)
            {
                try
                {
                    frames = _opus.Decode(packet.Span, _pcm, _pcm.Length / _channels);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
                {
                    return; // a damaged packet: a few ms of silence
                }
            }
            else
            {
                frames = Math.Min(packet.Length / 2 / _channels, _pcm.Length / _channels);
                for (var i = 0; i < frames * _channels; i++)
                    _pcm[i] = (short)(packet.Span[i * 2] | packet.Span[i * 2 + 1] << 8);
            }
            if (_mono.Length < frames)
                _mono = new float[frames];
            for (var f = 0; f < frames; f++)
            {
                var sum = 0;
                for (var ch = 0; ch < _channels; ch++)
                    sum += _pcm[f * _channels + ch];
                _mono[f] = sum / (32768f * _channels);
            }
            _jitter.Push(_mono.AsSpan(0, frames), _rate);
            Interlocked.Add(ref _decodedSamples, frames * 48_000L / Math.Max(1, _rate));
        }
    }

    public void Dispose()
    {
        _reattach.Dispose();
        _server.Feeding = false;
        // Off Windows' list while Altrove is not giving it a voice; usbip-win2 would otherwise keep retrying.
        try
        {
            UsbIpSetup.Detach();
        }
        catch (InvalidOperationException)
        {
        }
        _server.Dispose();
    }
}
