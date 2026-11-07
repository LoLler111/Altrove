using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Altrove.App;

/// <summary>
/// Windows' own Miracast ("Trasmetti", Win+K): the PC links to a TV or a Fire TV in its screen mirroring
/// mode, and Windows treats it as one more monitor (Win+P picks duplicate or extend). There is no public
/// API to start a Miracast link for the whole desktop, so this opens the same panel as Win+K.
/// </summary>
internal static class CastPanel
{
    /// <summary>Miracast runs over Wi-Fi Direct: without a Wi-Fi adapter Windows has nothing to cast with.</summary>
    public static bool HasWifi() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

    /// <summary>Presses Win+K for the user: Windows shows its list of wireless screens.</summary>
    public static void Open()
    {
        var inputs = new[]
        {
            Key(VkLeftWindows, down: true),
            Key(VkK, down: true),
            Key(VkK, down: false),
            Key(VkLeftWindows, down: false),
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException($"SendInput non riuscito ({Marshal.GetLastWin32Error()})");
    }

    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkK = 0x4B;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    private static Input Key(ushort key, bool down) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = down ? 0 : KeyEventKeyUp } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    // Sized like the biggest member (MOUSEINPUT), as SendInput checks cbSize against the whole INPUT.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
