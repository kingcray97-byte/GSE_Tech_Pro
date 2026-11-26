using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GseTechPro.Devices.Discovery;

/// <summary>
/// Platform-neutral registry enumerator for installed J2534 pass-thru devices, per the published
/// SAE J2534-1 install convention (subkeys under <c>PassThruSupport.04.04</c>/<c>.05.00</c> whose
/// default value is the DLL path). The pure parsing of the raw triples lives in
/// <see cref="J2534RegistryParser"/>; this class owns the registry read and the view-selection seam
/// so it can be unit-tested without loading the x86 host process or any vendor DLL.
/// </summary>
[SupportedOSPlatform("windows")]
public static class J2534RegistryDiscovery
{
    /// <summary>The registry root value names defined by the J2534-1 install convention.</summary>
    public const string Key0404 = @"SOFTWARE\PassThruSupport.04.04";
    public const string Key0500 = @"SOFTWARE\PassThruSupport.05.00";

    /// <summary>
    /// Discovers installed J2534 devices from the specified registry view(s). When more than one view
    /// is supplied, duplicate device key names are deduplicated and the first view wins.
    /// </summary>
    /// <param name="views">The registry view(s) to read.</param>
    public static IReadOnlyList<J2534DeviceDescriptor> Discover(params RegistryView[] views)
    {
        if (views is null || views.Length == 0)
            throw new ArgumentException("At least one registry view must be specified.", nameof(views));

        var entries = new List<(string KeyName, string DllPath, string? FriendlyName)>();
        foreach (var view in views)
            CollectFromView(view, entries);

        return J2534RegistryParser.Parse(entries);
    }

    /// <summary>
    /// Collects raw registry entries from a single registry view. Exposed internally for tests.
    /// Duplicate <paramref name="subKeyName"/> values are skipped so that, when multiple views are read,
    /// the first view wins (e.g. the 32-bit WOW6432Node entries take precedence over 64-bit entries).
    /// </summary>
    internal static void CollectFromView(
        RegistryView view,
        List<(string KeyName, string DllPath, string? FriendlyName)> entries)
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
                if (string.IsNullOrWhiteSpace(dllPath))
                    continue;

                // If the same key name already came from an earlier view/root, keep the first one.
                if (entries.Any(e => e.KeyName == subKeyName))
                    continue;

                entries.Add((subKeyName, dllPath, name));
            }
        }
    }
}
