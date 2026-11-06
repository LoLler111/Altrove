using System.Diagnostics;
using Altrove.Core.Streaming;
using Vortice.Direct3D11;

namespace Altrove.Capture;

/// <summary>A monitor, captured, scaled to the stream size and encoded to H.264.</summary>
public sealed class ScreenVideoSource : IEncodedVideoSource
{
    private readonly MonitorInfo _monitor;
    private readonly bool _preferSoftware;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private GraphicsDevice? _device;
    private ScreenCapture? _capture;
    private INv12Converter? _converter;
    private H264Encoder? _encoder;
    private long _minimumTicks;
    // Far in the past, but not so far that "now - last" overflows.
    private long _lastAccepted = long.MinValue / 2;
    private bool _disposed;

    public ScreenVideoSource(MonitorInfo monitor, bool preferSoftware = false)
    {
        _monitor = monitor;
        _preferSoftware = preferSoftware;
    }

    public (int Width, int Height) SourceSize => _capture is { } c ? (c.Width, c.Height) : (_monitor.Width, _monitor.Height);

    public string EncoderName => _encoder is null ? ""
        : $"{_encoder.Name}{(_encoder.RateControl.Length > 0 ? " " + _encoder.RateControl : "")}, conversione {_converter?.Name}, {_device?.AdapterName}";

    public event Action<EncodedFrame>? FrameEncoded;
    public event Action<Exception>? Failed;

    public Task StartAsync(EncoderSettings settings, CancellationToken token)
    {
        // Device and capture setup block for a moment (driver calls): keep them off the UI thread.
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ScreenVideoSource));
                _device = GraphicsDevice.CreateFor(_monitor);
                _converter = Nv12Converters.Create(_device, settings.Width, settings.Height);
                _encoder = H264Encoder.Create(_device, settings, _converter.ProducesTextures, _preferSoftware);
                _encoder.FrameEncoded += frame => FrameEncoded?.Invoke(frame);
                _encoder.Failed += error => Failed?.Invoke(error);
                _encoder.Start();

                // Accept frames a little faster than the target rate so jitter does not halve it.
                _minimumTicks = (long)(Stopwatch.Frequency / (double)settings.Fps * 0.75);
                _capture = new ScreenCapture(_device, _monitor);
                _capture.SetMinimumInterval(TimeSpan.FromSeconds(1.0 / settings.Fps));
                _capture.FrameArrived += OnFrame;
                _capture.Start();
            }
        }, token);
    }

    /// <summary>Frames Windows delivered, and how many of those went to the encoder.</summary>
    public (int Captured, int Submitted) Counters => (_captured, _submitted);

    private int _captured, _submitted;

    // Held while a frame is converted and handed to the encoder, so disposal never pulls the converter,
    // encoder or device out from under a frame in flight on the capture thread.
    private readonly object _frameLock = new();

    private void OnFrame(ID3D11Texture2D texture, TimeSpan systemTime)
    {
        Interlocked.Increment(ref _captured);
        lock (_frameLock)
        {
            if (_disposed || _converter is not { } converter || _encoder is not { } encoder)
                return;
            var now = _clock.ElapsedTicks;
            if (now - _lastAccepted < _minimumTicks)
                return;
            _lastAccepted = now;
            Interlocked.Increment(ref _submitted);
            try
            {
                var desc = texture.Description;
                var frame = converter.Convert(texture, (int)desc.Width, (int)desc.Height);
                // Media Foundation counts in 100 ns units.
                encoder.Submit(frame, (long)(now * (10_000_000.0 / Stopwatch.Frequency)));
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException or ObjectDisposedException)
            {
                Failed?.Invoke(ex);
            }
        }
    }

    public void RequestKeyframe()
    {
        lock (_frameLock)
        {
            if (!_disposed)
                _encoder?.RequestKeyframe();
        }
    }

    public ValueTask DisposeAsync()
    {
        ScreenCapture? capture;
        lock (_lock)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            capture = _capture;
            _capture = null;
        }
        // First no new frames, then wait for the one in flight, then tear down what it uses.
        if (capture is not null)
        {
            capture.FrameArrived -= OnFrame;
            capture.Dispose();
        }
        lock (_lock)
        {
            lock (_frameLock)
            {
                _encoder?.Dispose();
                _converter?.Dispose();
                _device?.Dispose();
                _encoder = null;
                _converter = null;
                _device = null;
            }
        }
        return ValueTask.CompletedTask;
    }
}
