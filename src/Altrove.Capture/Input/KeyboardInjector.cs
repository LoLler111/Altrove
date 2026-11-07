using System.Runtime.InteropServices;
using Altrove.Core.Input;
using Altrove.Core.Protocol;

namespace Altrove.Capture.Input;

/// <summary>
/// The receiver's keyboard on the PC: text is typed as Unicode (any layout, accents and emoji included), named
/// keys and shortcuts are pressed as real keys through SendInput. It goes to whatever window has the focus.
/// </summary>
public sealed class KeyboardInjector
{
    private readonly object _lock = new();

    /// <summary>Inputs Windows refused, e.g. while an elevated window or the secure desktop has the focus.</summary>
    public int Failures { get; private set; }

    public void Inject(KeyMessage message)
    {
        var strokes = KeyStrokes.For(message, KeyForChar);
        if (strokes.Count == 0)
            return;
        var inputs = strokes.Select(ToInput).ToArray();
        lock (_lock)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
                Failures++;
        }
    }

    /// <summary>VkKeyScanW: the key of a character on this PC's layout, and whether it takes Shift.</summary>
    private static (ushort Vk, bool Shift)? KeyForChar(char c)
    {
        var scan = VkKeyScanW(c);
        if (scan == -1)
            return null;
        return ((ushort)(scan & 0xFF), (scan & 0x100) != 0);
    }

    private static Input ToInput(KeyStroke stroke)
    {
        var flags = 0u;
        if (stroke.Up)
            flags |= KeyEventKeyUp;
        if (stroke.Extended)
            flags |= KeyEventExtendedKey;
        if (stroke.VirtualKey == 0)
            flags |= KeyEventUnicode;
        return new Input
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInput { VirtualKey = stroke.VirtualKey, ScanCode = stroke.Unicode, Flags = flags },
        };
    }

    // ---- user32 ----

    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // INPUT: type, then a union whose largest member (MOUSEINPUT) is 32 bytes on x64; 40 bytes in all.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char c);
}
