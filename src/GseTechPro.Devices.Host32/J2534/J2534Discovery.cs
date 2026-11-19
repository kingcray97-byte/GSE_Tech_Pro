using Microsoft.Win32;
using GseTechPro.Devices.Discovery;

namespace GseTechPro.Devices.Host32.J2534;

/// <summary>
/// Reads the real Windows registry to discover installed J2534 pass-thru devices, per the published
/// SAE J2534-1 install convention (subkeys under <c>PassThruSupport.04.04</c>/<c>.05.00</c> whose
/// default value is the DLL path). The pure parsing lives in
/// <see cref="J2534RegistryParser"/> (AnyCPU, unit-tested); only the registry read lives here in the
/// x86 host. Reading these keys is the published install convention, not reverse-engineering.
/// </summary>
public static class J2534Discovery
{
    /// <summary>The registry root value names defined by the J2534-1 install convention.</summary>
    public const string Key0404 = @"SOFTWARE\PassThruSupport.04.04";
    public const string Key0500 = @"SOFTWARE\PassThruSupport.05.00";

    /// <summary>Reads the registry (32-bit view first, then 64-bit) and returns installed devices.</summary>
    public static IReadOnlyList<J2534DeviceDescriptor> Discover()
    {
        var entries = new List<(string KeyName, string DllPath, string? FriendlyName)>();
        CollectFromView(RegistryView.Registry32, entries);
        CollectFromView(RegistryView.Registry64, entries);
        return J2534RegistryParser.Parse(entries);
    }

    private static void CollectFromView(RegistryView view, List<(string, string, string?)> entries)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        foreach (var root in new[] { Key0404, Key0500 })
        {
            using var support = baseKey.OpenSubKey(root);
            if (support is null)
                continue;

            foreach (var subKeyName in support.GetSubKeyNames())
            {
                using var device = support.OpenSubKey(subKeyName);
                if (device is null)
                    continue;

                var dllPath = (string?)device.GetValue(null)
                              ?? (string?)device.GetValue("FunctionLibrary");
                var name = (string?)device.GetValue("Name");
                if (!string.IsNullOrWhiteSpace(dllPath))
                    entries.Add((subKeyName, dllPath, name));
            }
        }
    }
}
