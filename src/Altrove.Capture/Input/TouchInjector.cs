using System.ComponentModel;
using System.Runtime.InteropServices;
using Altrove.Core.Protocol;

namespace Altrove.Capture.Input;

/// <summary>
/// Turns a receiver's touch messages into Windows touch on one monitor, through a synthetic touch
/// device (Windows 10 1809 and later). Works on PCs without a touch screen. Windows does not let a
/// normal app inject into elevated windows or the secure desktop (UAC prompts, the lock screen): those
/// touches are lost, and <see cref="Failures"/> counts them.
/// </summary>
public sealed class TouchInjector : IDisposable
{
    private const int MaxContacts = 10;

    // A held finger that does not move sends nothing: repeat its position so Windows keeps the contact.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _lock = new();
    private readonly Dictionary<int, uint> _contacts = []; // receiver's pointer id -> Windows contact id
    private readonly Dictionary<uint, PointerTypeInfo> _held = []; // contacts down, as last injected
    private readonly Timer _keepAlive;
    private IntPtr _device;
    private (int Left, int Top, int Width, int Height) _target;
    private long _lastInjection;

    public TouchInjector(MonitorInfo target)
    {
        _device = CreateSyntheticPointerDevice(PointerTypeTouch, MaxContacts, PointerFeedbackDefault);
        if (_device == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows non permette di simulare il tocco.");
        _target = (target.Left, target.Top, target.Width, target.Height);
        _keepAlive = new Timer(_ => KeepAlive(), null, KeepAliveInterval, KeepAliveInterval);
    }

    /// <summary>Injections Windows refused, e.g. into an elevated window.</summary>
    public int Failures { get; private set; }

    /// <summary>Touches land on this monitor from now on; fingers still down are lifted first.</summary>
    public void SetTarget(MonitorInfo target)
    {
        lock (_lock)
        {
            CancelAll();
            _target = (target.Left, target.Top, target.Width, target.Height);
        }
    }

    public void Inject(TouchMessage message)
    {
        lock (_lock)
        {
            if (_device == IntPtr.Zero)
                return;
            if (message.Pointers.Any(p => p.State == TouchStates.Cancel))
            {
                CancelAll();
                return;
            }

            var frame = new List<PointerTypeInfo>();
            var seen = new HashSet<int>();
            foreach (var pointer in message.Pointers)
            {
                if (!seen.Add(pointer.Id))
                    continue;
                var known = _contacts.TryGetValue(pointer.Id, out var contact);
                uint flags;
                switch (pointer.State)
                {
                    case TouchStates.Up:
                        if (!known)
                            continue;
                        flags = PointerFlagUp;
                        break;
                    case TouchStates.Down:
                    case TouchStates.Move:
                        if (!known)
                        {
                            // A move for a finger never seen going down (it started before this stream): start it here.
                            if (FreeContact() is not { } free)
                                continue;
                            contact = free;
                            _contacts[pointer.Id] = contact;
                            flags = PointerFlagDown | PointerFlagInRange | PointerFlagInContact;
                        }
                        else
                            flags = PointerFlagUpdate | PointerFlagInRange | PointerFlagInContact;
                        break;
                    default:
                        continue;
                }
                frame.Add(Contact(contact, pointer.X, pointer.Y, flags));
            }

            // Fingers the receiver no longer lists were lifted without us hearing it.
            foreach (var (id, contact) in _contacts.Where(c => !seen.Contains(c.Key)).ToList())
            {
                if (_held.TryGetValue(contact, out var last))
                    frame.Add(WithFlags(last, PointerFlagUp));
                _contacts.Remove(id);
                _held.Remove(contact);
            }

            if (frame.Count == 0)
                return;
            Send(frame);

            foreach (var info in frame)
            {
                var contact = info.TouchInfo.PointerInfo.PointerId;
                if ((info.TouchInfo.PointerInfo.PointerFlags & PointerFlagUp) != 0)
                {
                    _held.Remove(contact);
                    foreach (var id in _contacts.Where(c => c.Value == contact).Select(c => c.Key).ToList())
                        _contacts.Remove(id);
                }
                else
                    _held[contact] = WithFlags(info, PointerFlagUpdate | PointerFlagInRange | PointerFlagInContact);
            }
        }
    }

    private void KeepAlive()
    {
        lock (_lock)
        {
            if (_device == IntPtr.Zero || _held.Count == 0)
                return;
            if (Environment.TickCount64 - _lastInjection < KeepAliveInterval.TotalMilliseconds)
                return;
            Send(_held.Values.ToList());
        }
    }

    private void CancelAll()
    {
        if (_held.Count > 0)
            Send(_held.Values.Select(info => WithFlags(info, PointerFlagUp | PointerFlagCanceled)).ToList());
        _held.Clear();
        _contacts.Clear();
    }

    private uint? FreeContact()
    {
        for (uint id = 0; id < MaxContacts; id++)
        {
            if (!_contacts.ContainsValue(id))
                return id;
        }
        return null;
    }

    private PointerTypeInfo Contact(uint contact, double x, double y, uint flags)
    {
        var (left, top, width, height) = _target;
        var px = left + (int)Math.Round(Math.Clamp(x, 0, 1) * (width - 1));
        var py = top + (int)Math.Round(Math.Clamp(y, 0, 1) * (height - 1));
        var info = new PointerTypeInfo { Type = PointerTypeTouch };
        info.TouchInfo.PointerInfo.PointerType = PointerTypeTouch;
        info.TouchInfo.PointerInfo.PointerId = contact;
        info.TouchInfo.PointerInfo.PointerFlags = flags;
        info.TouchInfo.PointerInfo.PixelLocation = new Point { X = px, Y = py };
        info.TouchInfo.TouchMask = TouchMaskContactArea | TouchMaskOrientation | TouchMaskPressure;
        info.TouchInfo.Contact = new Rect { Left = px - 2, Top = py - 2, Right = px + 2, Bottom = py + 2 };
        info.TouchInfo.Orientation = 90;
        info.TouchInfo.Pressure = 512;
        return info;
    }

    private static PointerTypeInfo WithFlags(PointerTypeInfo info, uint flags)
    {
        info.TouchInfo.PointerInfo.PointerFlags = flags;
        return info;
    }

    private void Send(List<PointerTypeInfo> frame)
    {
        _lastInjection = Environment.TickCount64;
        if (!InjectSyntheticPointerInput(_device, [.. frame], (uint)frame.Count))
            Failures++;
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        lock (_lock)
        {
            if (_device == IntPtr.Zero)
                return;
            CancelAll();
            DestroySyntheticPointerDevice(_device);
            _device = IntPtr.Zero;
        }
    }

    // ---- user32 synthetic pointer API ----

    private const uint PointerTypeTouch = 2; // PT_TOUCH
    private const uint PointerFeedbackDefault = 1;
    private const uint PointerFlagInRange = 0x2;
    private const uint PointerFlagInContact = 0x4;
    private const uint PointerFlagCanceled = 0x8000;
    private const uint PointerFlagDown = 0x10000;
    private const uint PointerFlagUpdate = 0x20000;
    private const uint PointerFlagUp = 0x40000;
    private const uint TouchMaskContactArea = 0x1;
    private const uint TouchMaskOrientation = 0x2;
    private const uint TouchMaskPressure = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    // POINTER_INFO: 96 bytes on x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public Point PixelLocation;
        public Point HimetricLocation;
        public Point PixelLocationRaw;
        public Point HimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    // POINTER_TOUCH_INFO: 144 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTouchInfo
    {
        public PointerInfo PointerInfo;
        public uint TouchFlags;
        public uint TouchMask;
        public Rect Contact;
        public Rect ContactRaw;
        public uint Orientation;
        public uint Pressure;
    }

    // POINTER_TYPE_INFO: the type, then a union of touch (144 bytes) and pen (120) info, 8-aligned.
    [StructLayout(LayoutKind.Explicit, Size = 152)]
    private struct PointerTypeInfo
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public PointerTouchInfo TouchInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, [In] PointerTypeInfo[] pointerInfo, uint count);

    [DllImport("user32.dll")]
    private static extern void DestroySyntheticPointerDevice(IntPtr device);
}
