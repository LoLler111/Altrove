using System.Security.AccessControl;
using System.Security.Principal;
using Altrove.Capture.Native;
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

    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public static bool IsAdministrator =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>True when a device for the driver exists (it may still be disabled).</summary>
    public static bool IsInstalled() => RootDevice.Exists(DisplayClass, HardwareId);

    public static void Install(string infPath, string initialSettingsXml)
    {
        if (!File.Exists(infPath))
            throw new FileNotFoundException("Driver package not found.", infPath);
        PrepareSettings(initialSettingsXml);
        RootDevice.Install(DisplayClass, "Display", HardwareId, infPath);
    }

    /// <summary>Removes the device; the driver package stays in the driver store (harmless, reused on reinstall).</summary>
    public static void Uninstall() => RootDevice.Remove(DisplayClass, HardwareId);

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
}
