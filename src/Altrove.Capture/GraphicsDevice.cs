using System.Runtime.InteropServices;
using Altrove.Capture.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using IDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace Altrove.Capture;

/// <summary>
/// One Direct3D 11 device shared by capture, colour conversion and the hardware encoder, created on the
/// GPU that drives the captured monitor so frames never cross adapters.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, IDXGIAdapter1 adapter, IDirect3DDevice winrtDevice)
    {
        Device = device;
        Context = context;
        Adapter = adapter;
        WinRtDevice = winrtDevice;
        var description = adapter.Description1;
        AdapterName = description.Description;
        VendorId = description.VendorId;
        IsSoftware = (description.Flags & AdapterFlags.Software) != 0 || description.VendorId == 0x1414; // Microsoft: WARP / Basic Render
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIAdapter1 Adapter { get; }
    public IDirect3DDevice WinRtDevice { get; }
    public string AdapterName { get; }
    public uint VendorId { get; }
    public bool IsSoftware { get; }

    public static GraphicsDevice CreateFor(MonitorInfo monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            var owns = false;
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                using (output)
                    owns |= output.Description.Monitor == monitor.Handle;
            }
            if (owns && chosen is null)
                chosen = adapter;
            else
                adapter.Dispose();
        }
        // A virtual (indirect) display has no output on any adapter: use the default GPU.
        if (chosen is null)
            factory.EnumAdapters1(0, out chosen).CheckError();

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var result = D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, flags, levels, out var device, out ID3D11DeviceContext context);
        if (result.Failure)
        {
            // Some drivers (and remote sessions) refuse VIDEO_SUPPORT; capture still works, the encoder falls back to software.
            D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out device, out context).CheckError();
        }

        // Capture callbacks, the encoder and the UI thread all touch the immediate context.
        using (var multithread = device!.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Interop.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        IDirect3DDevice winrt;
        try
        {
            winrt = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
        return new GraphicsDevice(device, context!, chosen, winrt);
    }

    public void Dispose()
    {
        (WinRtDevice as IDisposable)?.Dispose();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
    }
}
