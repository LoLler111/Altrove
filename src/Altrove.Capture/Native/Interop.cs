using System.Runtime.InteropServices;

namespace Altrove.Capture.Native;

/// <summary>
/// COM interfaces that neither Vortice nor the Windows SDK projection expose, called through their
/// vtables directly so no marshalling layer can get in the way.
/// </summary>
internal static unsafe class Interop
{
    public static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    public static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public static readonly Guid IidDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    public static readonly Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    public static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>IGraphicsCaptureItemInterop::CreateForMonitor, returning an IGraphicsCaptureItem pointer the caller owns.</summary>
    public static IntPtr CreateCaptureItemForMonitor(IntPtr monitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, IidGraphicsCaptureItemInterop, out var interop));
            try
            {
                var vtable = *(IntPtr**)interop;
                var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[4];
                var iid = IidGraphicsCaptureItem;
                IntPtr item;
                Marshal.ThrowExceptionForHR(createForMonitor(interop, monitor, &iid, &item));
                return item;
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>IDirect3DDxgiInterfaceAccess::GetInterface on a WinRT surface: the ID3D11Texture2D behind it (caller owns it).</summary>
    public static IntPtr GetTextureFromSurface(IntPtr surface)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, IidDirect3DDxgiInterfaceAccess, out var access));
        try
        {
            var vtable = *(IntPtr**)access;
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
            var iid = IidD3D11Texture2D;
            IntPtr texture;
            Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
            return texture;
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public short Bool;
    }

    private const ushort VtUi4 = 19;
    private const ushort VtBool = 11;

    /// <summary>ICodecAPI::SetValue with a VT_UI4. Returns the HRESULT: encoders differ in what they accept.</summary>
    public static int CodecSetUInt32(IntPtr codecApi, Guid api, uint value)
    {
        var variant = new Variant { Type = VtUi4, UInt32 = value };
        return CodecSet(codecApi, api, &variant);
    }

    /// <summary>ICodecAPI::SetValue with a VT_BOOL.</summary>
    public static int CodecSetBool(IntPtr codecApi, Guid api, bool value)
    {
        var variant = new Variant { Type = VtBool, Bool = value ? (short)-1 : (short)0 };
        return CodecSet(codecApi, api, &variant);
    }

    private static int CodecSet(IntPtr codecApi, Guid api, Variant* value)
    {
        var vtable = *(IntPtr**)codecApi;
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtable[9];
        return setValue(codecApi, &api, value);
    }

    // ---- Monitors ----

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
