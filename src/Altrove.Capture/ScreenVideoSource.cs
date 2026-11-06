using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Altrove.Core.Streaming;
using Vortice.Direct3D11;

namespace Altrove.Capture;

/// <summary>What broke while streaming and what took its place, for the log and the status line.</summary>
public sealed record VideoRecovery(string Before, string After, Exception Error);

/// <summary>A monitor, captured, scaled to the stream size and encoded to H.264.</summary>
/// <remarks>
/// When the encoder or the GPU conversion fails while streaming, the pipeline is rebuilt without the part that
/// broke (the next hardware encoder, then software; the CPU conversion), and the stream goes on: Intel Quick
/// Sync fails on the first frame on some laptops (E_UNEXPECTED) where the AMD and software paths work.
/// An encoder that takes frames but stops producing output for about two seconds counts as broken too.
/// <see cref="Failed"/> is raised only once nothing is left to try.
/// </remarks>
public sealed class ScreenVideoSource : IEncodedVideoSource
{
    // Parts that broke, for the rest of the run: every new source skips them too.
    private static readonly ConcurrentDictionary<string, byte> BrokenEncoders = new();
    private static readonly ConcurrentDictionary<string, byte> BrokenGpuConverters = new();
    private const int MaxRecoveries = 3;

    private readonly MonitorInfo _monitor;
    private readonly bool _preferSoftware;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private EncoderSettings? _settings;
    private GraphicsDevice? _device;
    private ScreenCapture? _capture;
    private INv12Converter? _converter;
    private H264Encoder? _encoder;
    private long _minimumTicks;
    // Far in the past, but not so far that "now - last" overflows.
    private long _lastAccepted = long.MinValue / 2;
    private volatile bool _disposed;
    private int _recovering;
    private int _recoveries;

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

    /// <summary>Raised on a background thread after a broken part was replaced and the stream went on.</summary>
    public event Action<VideoRecovery>? Recovered;

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
                _settings = settings;
                Build(settings);
            }
        }, token);
    }

    /// <summary>Device, conversion, encoder and capture, leaving out what already broke. Caller holds <see cref="_lock"/>.</summary>
    private void Build(EncoderSettings settings)
    {
        try
        {
            var device = GraphicsDevice.CreateFor(_monitor);
            _device = device;
            var converter = Nv12Converters.Create(device, settings.Width, settings.Height, allowGpu: !BrokenGpuConverters.ContainsKey(device.AdapterName));
            _converter = converter;
            var encoder = H264Encoder.Create(device, settings, converter.ProducesTextures, _preferSoftware, BrokenEncoders.Keys.ToHashSet());
            _encoder = encoder;
            encoder.FrameEncoded += frame => FrameEncoded?.Invoke(frame);
            encoder.Failed += error => OnFailure(error, encoder, null);
            encoder.Start();

            // Accept frames a little faster than the target rate so jitter does not halve it.
            _minimumTicks = (long)(Stopwatch.Frequency / (double)settings.Fps * 0.75);
            _stallFrames = Math.Max(30, settings.Fps * 2);
            _capture = new ScreenCapture(device, _monitor);
            _capture.SetMinimumInterval(TimeSpan.FromSeconds(1.0 / settings.Fps));
            _capture.FrameArrived += OnFrame;
            _capture.Start();
        }
        catch
        {
            TearDown();
            throw;
        }
    }

    /// <summary>Stops capture, then frees what the frame in flight uses. Caller holds <see cref="_lock"/>.</summary>
    private void TearDown()
    {
        var capture = _capture;
        _capture = null;
        // First no new frames (outside the frame lock: disposal waits for a callback in progress)…
        if (capture is not null)
        {
            capture.FrameArrived -= OnFrame;
            capture.Dispose();
        }
        // …then wait for the one in flight, then tear down what it uses.
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
            Nv12Frame frame;
            try
            {
                var desc = texture.Description;
                frame = converter.Convert(texture, (int)desc.Width, (int)desc.Height);
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException or InvalidOperationException)
            {
                OnFailure(ex, null, converter);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            // Media Foundation counts in 100 ns units.
            encoder.Submit(frame, (long)(now * (10_000_000.0 / Stopwatch.Frequency)));
            if (encoder.FramesWithoutOutput > _stallFrames)
                OnFailure(new TimeoutException($"{encoder.Name} non produce più video: {encoder.FramesWithoutOutput} fotogrammi senza uscita."), encoder, null);
        }
    }

    // About two seconds of a changing screen with nothing coming out: the encoder went silent.
    private int _stallFrames = int.MaxValue;

    /// <summary>
    /// A part failed: rebuild without it on another thread (the encoder's own thread is the one calling, and
    /// tearing the encoder down waits for it). Failures of parts already replaced are ignored.
    /// </summary>
    private void OnFailure(Exception error, H264Encoder? encoder, INv12Converter? converter)
    {
        if (_disposed
            || (encoder is not null && !ReferenceEquals(encoder, _encoder))
            || (converter is not null && !ReferenceEquals(converter, _converter)))
            return;
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
            return;
        if (Interlocked.Increment(ref _recoveries) > MaxRecoveries)
        {
            Failed?.Invoke(error); // _recovering stays set: nothing more to try
            return;
        }
        _ = Task.Run(() => Recover(error, encoder, converter));
    }

    private void Recover(Exception error, H264Encoder? encoder, INv12Converter? converter)
    {
        string before, after;
        try
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                before = EncoderName;
                if (encoder is { IsHardware: true })
                    BrokenEncoders.TryAdd(encoder.Name, 0);
                if (converter is { ProducesTextures: true } && _device is { } device)
                    BrokenGpuConverters.TryAdd(device.AdapterName, 0);
                TearDown();
                // From here a failure of the new parts starts another round.
                Interlocked.Exchange(ref _recovering, 0);
                Build(_settings!);
                after = EncoderName;
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(new InvalidOperationException($"{error.Message} Nessuna alternativa ha funzionato: {ex.Message}", error));
            return;
        }
        Recovered?.Invoke(new VideoRecovery(before, after, error));
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
        lock (_lock)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            TearDown();
        }
        return ValueTask.CompletedTask;
    }
}
