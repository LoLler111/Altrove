using System.Diagnostics;
using Altrove.Core.Protocol;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Altrove.Capture.Audio;

/// <summary>
/// What the PC is playing (the whole Windows mix of the default output, WASAPI loopback), as 48 kHz stereo cut in
/// 10 ms packets: Opus at 128 kbit/s, or 16-bit PCM for receivers without an Opus decoder (docs/PROTOCOLLO.md).
/// Windows delivers nothing while nothing plays, and so does this.
/// </summary>
public sealed class LoopbackAudio : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameMs = 10;
    private const int FrameSamples = SampleRate * FrameMs / 1000; // per channel

    private readonly WasapiLoopbackCapture _capture;
    private readonly IOpusEncoder? _opus;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly short[] _frame = new short[FrameSamples * Channels];
    private readonly byte[] _encoded = new byte[4000];
    private int _filled;

    // Linear resampling state when the mix is not 48 kHz.
    private double _position;
    private float _lastLeft, _lastRight;

    public LoopbackAudio(string codec)
    {
        Codec = codec;
        if (codec == AudioCodecs.Opus)
        {
            _opus = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
            _opus.Bitrate = 128_000;
        }
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                Failed?.Invoke(e.Exception);
        };
        DeviceName = TryDeviceName();
    }

    public string Codec { get; }

    /// <summary>The Windows output being captured, for the status line.</summary>
    public string DeviceName { get; }

    public AudioStartMessage Format => new(Codec, SampleRate, Channels, FrameMs);

    /// <summary>One packet ready, with its time in microseconds; raised on the capture thread.</summary>
    public event Action<byte[], long>? PacketReady;

    public event Action<Exception>? Failed;

    public void Start() => _capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _capture.WaveFormat;
        var channels = format.Channels;
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = e.BytesRecorded / (bytesPerSample * channels);
        var step = (double)format.SampleRate / SampleRate; // source frames per output frame
        for (var i = 0; i < frames; i++)
        {
            var left = Read(e.Buffer, (i * channels) * bytesPerSample, format);
            var right = channels > 1 ? Read(e.Buffer, (i * channels + 1) * bytesPerSample, format) : left;
            if (format.SampleRate == SampleRate)
            {
                Push(left, right);
                continue;
            }
            // Output samples falling between the previous source frame and this one.
            while (_position <= 1.0)
            {
                Push(_lastLeft + (left - _lastLeft) * (float)_position, _lastRight + (right - _lastRight) * (float)_position);
                _position += step;
            }
            _position -= 1.0;
            _lastLeft = left;
            _lastRight = right;
        }
    }

    private void Push(float left, float right)
    {
        _frame[_filled++] = ToShort(left);
        _frame[_filled++] = ToShort(right);
        if (_filled < _frame.Length)
            return;
        _filled = 0;
        var time = (long)(_clock.ElapsedTicks * (1_000_000.0 / Stopwatch.Frequency));
        byte[] packet;
        if (_opus is not null)
        {
            var length = _opus.Encode(_frame, FrameSamples, _encoded, _encoded.Length);
            packet = _encoded.AsSpan(0, length).ToArray();
        }
        else
        {
            packet = new byte[_frame.Length * 2];
            Buffer.BlockCopy(_frame, 0, packet, 0, packet.Length); // x64 is little-endian, as the protocol wants
        }
        PacketReady?.Invoke(packet, time);
    }

    private static float Read(byte[] buffer, int offset, WaveFormat format) => format.Encoding switch
    {
        WaveFormatEncoding.IeeeFloat => BitConverter.ToSingle(buffer, offset),
        WaveFormatEncoding.Extensible when format.BitsPerSample == 32 && IsFloat(format) => BitConverter.ToSingle(buffer, offset),
        _ => format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            24 => ((buffer[offset] | buffer[offset + 1] << 8 | (sbyte)buffer[offset + 2] << 16)) / 8388608f,
            32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0f,
        },
    };

    private static bool IsFloat(WaveFormat format) =>
        format is WaveFormatExtensible extensible && extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;

    private static short ToShort(float value) => (short)Math.Clamp((int)Math.Round(value * 32767f), short.MinValue, short.MaxValue);

    private static string TryDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return "uscita predefinita";
        }
    }

    public void Dispose()
    {
        try
        {
            _capture.StopRecording();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        _capture.Dispose();
    }
}
