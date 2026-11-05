using System.Runtime.InteropServices;
using Altrove.Capture.Native;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Altrove.Capture;

/// <summary>
/// Captures one monitor with Windows.Graphics.Capture, mouse pointer included. Each frame is handed out
/// as a BGRA texture that is only valid during the callback.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _lock = new();
    private SizeInt32 _size;
    private bool _disposed;

    public ScreenCapture(GraphicsDevice device, MonitorInfo monitor)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("Questa versione di Windows non permette di catturare lo schermo.");
        _device = device;
        var itemPointer = Interop.CreateCaptureItemForMonitor(monitor.Handle);
        try
        {
            _item = GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
        _size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        TryDisableBorder(_session);
    }

    public int Width => _size.Width;
    public int Height => _size.Height;

    /// <summary>Raised on a capture thread with the new image; the texture must not be kept after returning.</summary>
    public event Action<ID3D11Texture2D, TimeSpan>? FrameArrived;

    /// <summary>Raised when the monitor's resolution changes; frames keep coming at the new size.</summary>
    public event Action<int, int>? SizeChanged;

    /// <summary>Lowest time between two frames, from Windows 11 24H2; earlier versions deliver up to the refresh rate.</summary>
    public void SetMinimumInterval(TimeSpan interval)
    {
        try
        {
            _session.MinUpdateInterval = interval;
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or COMException or EntryPointNotFoundException)
        {
        }
    }

    public void Start() => _session.StartCapture();

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
            return;
        lock (_lock)
        {
            if (_disposed)
                return;
            var contentSize = frame.ContentSize;
            if (contentSize.Width != _size.Width || contentSize.Height != _size.Height)
            {
                _size = contentSize;
                sender.Recreate(_device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
                SizeChanged?.Invoke(contentSize.Width, contentSize.Height);
                return; // this frame still has the old size
            }

            var surface = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
            try
            {
                var texturePointer = Interop.GetTextureFromSurface(surface);
                using var texture = new ID3D11Texture2D(texturePointer);
                FrameArrived?.Invoke(texture, frame.SystemRelativeTime);
            }
            finally
            {
                Marshal.Release(surface);
            }
        }
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        // Windows 11 draws a yellow frame around captured screens unless the app asks for borderless capture.
        try
        {
            var access = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
            if (access == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
                session.IsBorderRequired = false;
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or COMException or EntryPointNotFoundException or TypeLoadException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
    }
}
