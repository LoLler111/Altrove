using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Altrove.Capture.Native;

/// <summary>
/// Root-enumerated devices the way devcon (and usbip-win2's devnode.exe) make them: a device node with a hardware id,
/// then the driver package on it. Installing needs administrator rights.
/// </summary>
internal static class RootDevice
{
    /// <summary>True when a device of that class with that hardware id exists (it may still be disabled).</summary>
    public static bool Exists(Guid classGuid, string hardwareId) => Count(classGuid, hardwareId) > 0;

    /// <summary>Creates the device node unless it exists, then installs the driver on it. True if Windows wants a restart.</summary>
    public static bool Install(Guid classGuid, string className, string hardwareId, string infPath)
    {
        if (!File.Exists(infPath))
            throw new FileNotFoundException("Driver package not found.", infPath);

        if (!Exists(classGuid, hardwareId))
        {
            var list = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
            if (list == InvalidHandle)
                throw new Win32Exception();
            try
            {
                var info = new SpDevinfoData { Size = Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiCreateDeviceInfoW(list, className, ref classGuid, null, IntPtr.Zero, DicdGenerateId, ref info))
                    throw new Win32Exception();
                var ids = System.Text.Encoding.Unicode.GetBytes(hardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryPropertyW(list, ref info, SpdrpHardwareId, ids, ids.Length))
                    throw new Win32Exception();
                if (!SetupDiCallClassInstaller(DifRegisterDevice, list, ref info))
                    throw new Win32Exception();
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(list);
            }
        }

        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, Path.GetFullPath(infPath), InstallFlagForce, out var reboot))
            throw new Win32Exception();
        return reboot;
    }

    /// <summary>
    /// Adds a driver package to the driver store and installs it on the devices it matches (an extension INF such as a
    /// filter included), like pnputil /add-driver /install. True if Windows wants a restart.
    /// </summary>
    public static bool InstallPackage(string infPath)
    {
        if (!File.Exists(infPath))
            throw new FileNotFoundException("Driver package not found.", infPath);
        if (!DiInstallDriverW(IntPtr.Zero, Path.GetFullPath(infPath), 0, out var reboot))
            throw new Win32Exception();
        return reboot;
    }

    /// <summary>Removes the devices; the driver package stays in the driver store (harmless, reused on reinstall).</summary>
    public static void Remove(Guid classGuid, string hardwareId)
    {
        var list = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (list == InvalidHandle)
            throw new Win32Exception();
        try
        {
            foreach (var info in Enumerate(list).Where(i => HasHardwareId(list, i, hardwareId)).ToList())
            {
                var device = info;
                if (!DiUninstallDevice(IntPtr.Zero, list, ref device, 0, out _))
                    throw new Win32Exception();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(list);
        }
    }

    private static int Count(Guid classGuid, string hardwareId)
    {
        var list = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (list == InvalidHandle)
            return 0;
        try
        {
            return Enumerate(list).Count(i => HasHardwareId(list, i, hardwareId));
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(list);
        }
    }

    private static IEnumerable<SpDevinfoData> Enumerate(IntPtr list)
    {
        for (uint i = 0; ; i++)
        {
            var info = new SpDevinfoData { Size = Marshal.SizeOf<SpDevinfoData>() };
            if (!SetupDiEnumDeviceInfo(list, i, ref info))
                yield break;
            yield return info;
        }
    }

    private static bool HasHardwareId(IntPtr list, SpDevinfoData info, string hardwareId)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(list, ref info, SpdrpHardwareId, out _, buffer, buffer.Length, out var size))
            return false;
        var ids = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, size)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return ids.Any(id => id.Equals(hardwareId, StringComparison.OrdinalIgnoreCase));
    }

    // ---- SetupAPI ----

    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint DicdGenerateId = 1;
    private const uint SpdrpHardwareId = 1;
    private const uint DifRegisterDevice = 0x19;
    private const uint InstallFlagForce = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr parent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr list, string name, ref Guid classGuid, string? description, IntPtr parent, uint flags, ref SpDevinfoData info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr list, ref SpDevinfoData info, uint property, byte[] buffer, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr list, ref SpDevinfoData info, uint property, out uint type, byte[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr list, ref SpDevinfoData info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr list, uint index, ref SpDevinfoData info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr parent, string hardwareId, string infPath, uint flags, out bool reboot);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DiInstallDriverW(IntPtr parent, string infPath, uint flags, out bool reboot);

    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiUninstallDevice(IntPtr parent, IntPtr list, ref SpDevinfoData info, uint flags, out bool reboot);
}
