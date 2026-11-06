using System.Runtime.InteropServices;
using Altrove.Core.Protocol;

namespace Altrove.Capture.Input;

/// <summary>
/// The receiver as a trackpad: relative moves (fractions of the streamed image) move the real pointer, kept
/// inside the streamed monitor; buttons and the wheel go through SendInput like a real mouse.
/// </summary>
public sealed class MouseInjector
{
    private const int WheelDelta = 120;
    private readonly object _lock = new();
    private (int Left, int Top, int Width, int Height) _target;
    private double _restX, _restY, _wheelRestX, _wheelRestY;

    public MouseInjector(MonitorInfo target) => SetTarget(target);

    /// <summary>Inputs Windows refused, e.g. while an elevated window or the secure desktop has the focus.</summary>
    public int Failures { get; private set; }

    /// <summary>The pointer moves on this monitor from now on; if it is elsewhere it jumps to its middle.</summary>
    public void SetTarget(MonitorInfo target)
    {
        lock (_lock)
        {
            _target = (target.Left, target.Top, target.Width, target.Height);
            if (GetCursorPos(out var p) && !Inside(p.X, p.Y))
                SetCursorPos(target.Left + target.Width / 2, target.Top + target.Height / 2);
        }
    }

    public void Inject(MouseMessage message)
    {
        lock (_lock)
        {
            switch (message.Action)
            {
                case MouseActions.Move:
                    Move(message.Dx ?? 0, message.Dy ?? 0);
                    break;
                case MouseActions.Down:
                    Button(message.Button, down: true);
                    break;
                case MouseActions.Up:
                    Button(message.Button, down: false);
                    break;
                case MouseActions.Click:
                    Button(message.Button, down: true);
                    Button(message.Button, down: false);
                    break;
                case MouseActions.Scroll:
                    Scroll(message.Dx ?? 0, message.Dy ?? 0);
                    break;
            }
        }
    }

    private void Move(double dx, double dy)
    {
        var (left, top, width, height) = _target;
        // Keep the fractions of a pixel, or slow finger moves would never move the pointer.
        _restX += dx * width;
        _restY += dy * height;
        var stepX = (int)Math.Truncate(_restX);
        var stepY = (int)Math.Truncate(_restY);
        if (stepX == 0 && stepY == 0)
            return;
        _restX -= stepX;
        _restY -= stepY;
        if (!GetCursorPos(out var p))
            return;
        var x = Math.Clamp(p.X + stepX, left, left + width - 1);
        var y = Math.Clamp(p.Y + stepY, top, top + height - 1);
        if (!SetCursorPos(x, y))
            Failures++;
        else
            Send(new MouseInput { Flags = MouseMove }); // a zero move, so apps see the pointer moved (hover)
    }

    private void Button(string? button, bool down)
    {
        var flags = button switch
        {
            MouseButtons.Right => down ? RightDown : RightUp,
            MouseButtons.Middle => down ? MiddleDown : MiddleUp,
            _ => down ? LeftDown : LeftUp,
        };
        Send(new MouseInput { Flags = flags });
    }

    private void Scroll(double dx, double dy)
    {
        _wheelRestY += dy * WheelDelta;
        _wheelRestX += dx * WheelDelta;
        var vertical = (int)Math.Truncate(_wheelRestY);
        var horizontal = (int)Math.Truncate(_wheelRestX);
        _wheelRestY -= vertical;
        _wheelRestX -= horizontal;
        if (vertical != 0)
            Send(new MouseInput { Flags = Wheel, MouseData = vertical });
        if (horizontal != 0)
            Send(new MouseInput { Flags = HorizontalWheel, MouseData = horizontal });
    }

    private bool Inside(int x, int y)
    {
        var (left, top, width, height) = _target;
        return x >= left && x < left + width && y >= top && y < top + height;
    }

    private void Send(MouseInput mouse)
    {
        var input = new Input { Type = InputMouse, Mouse = mouse };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            Failures++;
    }

    // ---- user32 ----

    private const uint InputMouse = 0;
    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint Wheel = 0x0800;
    private const uint HorizontalWheel = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public int MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // INPUT: type, then a union whose largest member (MOUSEINPUT) is 32 bytes on x64; 40 bytes in all.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
