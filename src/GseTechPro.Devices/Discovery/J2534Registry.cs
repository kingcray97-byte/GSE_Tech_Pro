namespace GseTechPro.Devices.Discovery;

/// <summary>An installed J2534 pass-thru device descriptor (registry key → friendly name + DLL path).</summary>
public readonly record struct J2534DeviceDescriptor(string Id, string Name, string DllPath);

/// <summary>
/// Pure parser for the J2534 registry convention (SAE J2534-1 install): each subkey under
/// <c>HKLM\SOFTWARE\PassThruSupport.04.04</c>/<c>.05.00</c> names a device whose default value is the
/// pass-thru DLL path. This class is hardware- and platform-free so it is unit-testable in the AnyCPU
/// assembly; the actual registry read lives in the x86 host (<c>J2534Discovery</c>).
/// </summary>
public static class J2534RegistryParser
{
    /// <summary>Parses discovered (key name, dll path, friendly name) triples into descriptors.</summary>
    public static IReadOnlyList<J2534DeviceDescriptor> Parse(
        IEnumerable<(string KeyName, string DllPath, string? FriendlyName)> entries)
    {
        var result = new List<J2534DeviceDescriptor>();
        foreach (var (keyName, dllPath, friendlyName) in entries)
        {
            if (string.IsNullOrWhiteSpace(dllPath))
                continue;
            var name = string.IsNullOrWhiteSpace(friendlyName) ? keyName : friendlyName;
            result.Add(new J2534DeviceDescriptor($"j2534:{keyName}", name, dllPath));
        }
        return result;
    }
}
