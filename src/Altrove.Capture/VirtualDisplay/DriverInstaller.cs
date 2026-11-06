using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Altrove.Capture.VirtualDisplay;

/// <summary>
/// Installs the Virtual Display Driver (third_party/VirtualDisplayDriver) the way devcon does: a
/// root-enumerated device with the driver's hardware id, then the driver on it. Needs administrator rights.
/// </summary>
public static class DriverInstaller
{
    public const string HardwareId = @"Root\MttVDD";

    /// <summary>Where the driver reads vdd_settings.xml from; writable by every user so Altrove needs no rights later.</summary>
    public static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Altrove", "VirtualDisplay");

    private const string SettingsKey = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";

    public static bool IsAdministrator =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>True when a device for the driver exists (it may still be disabled).</summary>
    public static bool IsInstalled() => FindDevices().Count > 0;

    public static void Install(string infPath, string initialSettingsXml)
    {
        if (!File.Exists(infPath))
            throw new FileNotFoundException("Driver package not found.", infPath);

        PrepareSettings(initialSettingsXml);

        if (FindDevices().Count == 0)
        {
            var classGuid = DisplayClass;
            var list = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
            if (list == InvalidHandle)
                throw new Win32Exception();
            try
            {
                var info = new SpDevinfoData { Size = Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiCreateDeviceInfoW(list, "Display", ref classGuid, null, IntPtr.Zero, DicdGenerateId, ref info))
                    throw new Win32Exception();
                var ids = System.Text.Encoding.Unicode.GetBytes(HardwareId + "\0\0");
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

        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, Path.GetFullPath(infPath), InstallFlagForce, out _))
            throw new Win32Exception();
    }

    /// <summary>Removes the device; the driver package stays in the driver store (harmless, reused on reinstall).</summary>
    public static void Uninstall()
    {
        var classGuid = DisplayClass;
        var list = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (list == InvalidHandle)
            throw new Win32Exception();
        try
        {
            foreach (var info in Enumerate(list).Where(i => HasHardwareId(list, i)).ToList())
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

    /// <summary>The settings folder, open to all users, and the driver pointed at it.</summary>
    private static void PrepareSettings(string initialSettingsXml)
    {
        var folder = Directory.CreateDirectory(SettingsFolder);
        var security = folder.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        folder.SetAccessControl(security);

        var file = Path.Combine(SettingsFolder, "vdd_settings.xml");
        if (!File.Exists(file))
            File.WriteAllText(file, initialSettingsXml);

        using var key = Registry.LocalMachine.CreateSubKey(SettingsKey);
        key.SetValue("VDDPATH", SettingsFolder, RegistryValueKind.String);
    }

    private static List<string> FindDevices()
    {
        var classGuid = DisplayClass;
        var list = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (list == InvalidHandle)
            return [];
        try
        {
            return Enumerate(list).Where(i => HasHardwareId(list, i)).Select(_ => HardwareId).ToList();
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

    private static bool HasHardwareId(IntPtr list, SpDevinfoData info)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(list, ref info, SpdrpHardwareId, out _, buffer, buffer.Length, out var size))
            return false;
        var ids = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, size)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return ids.Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase));
    }

    // ---- SetupAPI ----

    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
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

    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiUninstallDevice(IntPtr parent, IntPtr list, ref SpDevinfoData info, uint flags, out bool reboot);
}
