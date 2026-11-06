using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Altrove.Capture;

/// <summary>
/// A frame ready for the encoder: NV12 (Y plane, then interleaved U/V at half size), in a GPU texture
/// or, on the CPU path, in a tightly packed byte array (stride = width).
/// </summary>
public sealed class Nv12Frame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public ID3D11Texture2D? Texture { get; init; }
    public byte[]? Bytes { get; init; }
}

public interface INv12Converter : IDisposable
{
    string Name { get; }
    bool ProducesTextures { get; }

    /// <summary>Scales and converts a BGRA texture; the result stays valid until a few more frames are converted.</summary>
    Nv12Frame Convert(ID3D11Texture2D bgra, int width, int height);
}

public static class Nv12Converters
{
    public static INv12Converter Create(GraphicsDevice device, int outputWidth, int outputHeight, bool allowGpu = true)
    {
        if (!allowGpu)
            return new CpuNv12Converter(device, outputWidth, outputHeight);
        try
        {
            return new GpuNv12Converter(device, outputWidth, outputHeight);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException or InvalidCastException or NotSupportedException)
        {
            return new CpuNv12Converter(device, outputWidth, outputHeight);
        }
    }
}

/// <summary>The GPU's video processor does scaling and colour conversion in one pass.</summary>
internal sealed class GpuNv12Converter : INv12Converter
{
    // Frames the encoder may still be reading when capture moves on.
    private const int RingSize = 6;

    private readonly GraphicsDevice _device;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly int _outputWidth, _outputHeight;
    private readonly ID3D11Texture2D[] _outputs = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[RingSize];
    private readonly Nv12Frame[] _frames = new Nv12Frame[RingSize];
    private int _next;

    // Recreated when the captured size changes.
    private int _inputWidth, _inputHeight;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _input;
    private ID3D11VideoProcessorInputView? _inputView;

    public GpuNv12Converter(GraphicsDevice device, int outputWidth, int outputHeight)
    {
        _device = device;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _videoDevice = device.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.Context.QueryInterface<ID3D11VideoContext>();
        // Probe once with the output size so an unusable video processor is found now, not on the first frame.
        Prepare(outputWidth, outputHeight);

        for (var i = 0; i < RingSize; i++)
        {
            _outputs[i] = CreateOutputTexture();
            _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_outputs[i], _enumerator!, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            });
            _frames[i] = new Nv12Frame { Width = outputWidth, Height = outputHeight, Texture = _outputs[i] };
        }
    }

    public string Name => "GPU";
    public bool ProducesTextures => true;

    private ID3D11Texture2D CreateOutputTexture()
    {
        var description = new Texture2DDescription
        {
            Width = (uint)_outputWidth,
            Height = (uint)_outputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.VideoEncoder,
        };
        try
        {
            return _device.Device.CreateTexture2D(description);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Not every driver accepts the encoder bind flag on a render target.
            description.BindFlags = BindFlags.RenderTarget;
            return _device.Device.CreateTexture2D(description);
        }
    }

    private void Prepare(int width, int height)
    {
        if (width == _inputWidth && height == _inputHeight && _processor is not null)
            return;
        _inputView?.Dispose();
        _input?.Dispose();
        _processor?.Dispose();
        var oldEnumerator = _enumerator;

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            InputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)_outputWidth,
            OutputHeight = (uint)_outputHeight,
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // Our own copy of the frame: capture surfaces are recycled by Windows and lack the bind flags we need.
        _input = _device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _inputView = _videoDevice.CreateVideoProcessorInputView(_input, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });

        ConfigureColour();
        _inputWidth = width;
        _inputHeight = height;

        if (oldEnumerator is not null)
        {
            // Output views belong to the old enumerator: rebuild them on the new one.
            for (var i = 0; i < RingSize; i++)
            {
                _outputViews[i]?.Dispose();
                _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_outputs[i], _enumerator, new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                });
            }
            oldEnumerator.Dispose();
        }
    }

    private void ConfigureColour()
    {
        try
        {
            using var context1 = _videoContext.QueryInterface<ID3D11VideoContext1>();
            context1.VideoProcessorSetStreamColorSpace1(_processor!, 0, ColorSpaceType.RgbFullG22NoneP709);
            context1.VideoProcessorSetOutputColorSpace1(_processor!, ColorSpaceType.YcbcrStudioG22LeftP709);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Older drivers: their default (BT.601) shifts colours slightly but works.
        }
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor!, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor!, 0, false);
    }

    public Nv12Frame Convert(ID3D11Texture2D bgra, int width, int height)
    {
        Prepare(width, height);
        _device.Context.CopyResource(_input!, bgra);

        var index = _next;
        _next = (_next + 1) % RingSize;

        var destination = FitRect(width, height, _outputWidth, _outputHeight);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, width, height));
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, destination);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor!, true, new RawRect(0, 0, _outputWidth, _outputHeight));
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor!, true, new VideoColor { YCbCr = new VideoColorYCbCrA { Y = 0.0625f, Cb = 0.5f, Cr = 0.5f, A = 1 } });
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView! };
        _videoContext.VideoProcessorBlt(_processor!, _outputViews[index], 0, 1, [stream]).CheckError();
        return _frames[index];
    }

    internal static RawRect FitRect(int sourceWidth, int sourceHeight, int width, int height)
    {
        var scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
        var w = (int)Math.Round(sourceWidth * scale);
        var h = (int)Math.Round(sourceHeight * scale);
        var x = (width - w) / 2;
        var y = (height - h) / 2;
        return new RawRect(x, y, x + w, y + h);
    }

    public void Dispose()
    {
        foreach (var view in _outputViews)
            view?.Dispose();
        foreach (var texture in _outputs)
            texture?.Dispose();
        _inputView?.Dispose();
        _input?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}

/// <summary>
/// Fallback for GPUs without a usable video processor (remote sessions, basic display drivers):
/// read the frame back and convert it on the CPU, BT.709 limited range like the GPU path.
/// </summary>
internal sealed unsafe class CpuNv12Converter : INv12Converter
{
    private const int RingSize = 3;

    private readonly GraphicsDevice _device;
    private readonly int _outputWidth, _outputHeight;
    private readonly Nv12Frame[] _frames = new Nv12Frame[RingSize];
    private int _next;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth, _stagingHeight;
    private int[] _xMap = [];
    private int[] _yMap = [];

    public CpuNv12Converter(GraphicsDevice device, int outputWidth, int outputHeight)
    {
        _device = device;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        for (var i = 0; i < RingSize; i++)
            _frames[i] = new Nv12Frame { Width = outputWidth, Height = outputHeight, Bytes = new byte[outputWidth * outputHeight * 3 / 2] };
    }

    public string Name => "CPU";
    public bool ProducesTextures => false;

    public Nv12Frame Convert(ID3D11Texture2D bgra, int width, int height)
    {
        if (_staging is null || width != _stagingWidth || height != _stagingHeight)
        {
            _staging?.Dispose();
            _staging = _device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _stagingWidth = width;
            _stagingHeight = height;
            BuildMaps(width, height);
        }

        _device.Context.CopyResource(_staging, bgra);
        var frame = _frames[_next];
        _next = (_next + 1) % RingSize;
        var mapped = _device.Context.Map(_staging, 0, MapMode.Read);
        try
        {
            Convert((byte*)mapped.DataPointer, (int)mapped.RowPitch, frame.Bytes!);
        }
        finally
        {
            _device.Context.Unmap(_staging, 0);
        }
        return frame;
    }

    // For each output pixel, the source pixel it samples; -1 for the letterbox bars.
    private void BuildMaps(int sourceWidth, int sourceHeight)
    {
        var fit = GpuNv12Converter.FitRect(sourceWidth, sourceHeight, _outputWidth, _outputHeight);
        _xMap = new int[_outputWidth];
        _yMap = new int[_outputHeight];
        for (var x = 0; x < _outputWidth; x++)
            _xMap[x] = x < fit.Left || x >= fit.Right ? -1 : Math.Min(sourceWidth - 1, (int)((x - fit.Left + 0.5) * sourceWidth / (fit.Right - fit.Left)));
        for (var y = 0; y < _outputHeight; y++)
            _yMap[y] = y < fit.Top || y >= fit.Bottom ? -1 : Math.Min(sourceHeight - 1, (int)((y - fit.Top + 0.5) * sourceHeight / (fit.Bottom - fit.Top)));
    }

    private void Convert(byte* source, int pitch, byte[] destination)
    {
        var width = _outputWidth;
        var height = _outputHeight;
        var xMap = _xMap;
        var yMap = _yMap;
        fixed (byte* dst = destination)
        {
            var dstAddress = (nint)dst;
            var srcAddress = (nint)source;
            // Two output rows at a time: they share one row of chroma.
            Parallel.For(0, height / 2, pair =>
            {
                var d = (byte*)dstAddress;
                var s = (byte*)srcAddress;
                var uvRow = d + width * height + pair * width;
                for (var half = 0; half < 2; half++)
                {
                    var y = pair * 2 + half;
                    var yRow = d + y * width;
                    var sy = yMap[y];
                    for (var x = 0; x < width; x++)
                    {
                        var sx = xMap[x];
                        int r = 0, g = 0, b = 0;
                        if (sy >= 0 && sx >= 0)
                        {
                            var p = s + sy * pitch + sx * 4;
                            b = p[0];
                            g = p[1];
                            r = p[2];
                        }
                        yRow[x] = (byte)(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8));
                        if (half == 0 && (x & 1) == 0)
                        {
                            uvRow[x] = (byte)Math.Clamp(128 + ((-26 * r - 87 * g + 112 * b + 128) >> 8), 16, 240);
                            uvRow[x + 1] = (byte)Math.Clamp(128 + ((112 * r - 102 * g - 10 * b + 128) >> 8), 16, 240);
                        }
                    }
                }
            });
        }
    }

    public void Dispose() => _staging?.Dispose();
}
