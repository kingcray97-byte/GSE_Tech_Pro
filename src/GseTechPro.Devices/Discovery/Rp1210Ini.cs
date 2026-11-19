namespace GseTechPro.Devices.Discovery;

/// <summary>A vendor/device entry parsed from RP121032.ini.</summary>
public readonly record struct Rp1210VendorDescriptor(
    string Id,
    string VendorName,
    string DeviceName,
    string DllPath,
    IReadOnlyList<string> Protocols);

/// <summary>
/// Pure parser for the RP1210 INI file (RP121032.ini / RP1210.ini), per the published TMC RP1210
/// install convention. This class is hardware- and platform-free so it is unit-testable in the AnyCPU
/// assembly; the actual file read lives in the x86 host (<c>Rp1210Discovery</c>).
/// Structure:
/// <code>
/// [VendorInformation]
/// Vendor1="&lt;vendor name&gt;|&lt;device name&gt;|&lt;version&gt;"
/// [Vendor1]
/// APIImplementationDLL="&lt;path&gt;"
/// Protocols="J1708|CAN|J1939"
/// </code>
/// </summary>
public static class Rp1210IniParser
{
    /// <summary>Parses RP1210 INI text into vendor/device descriptors.</summary>
    public static IReadOnlyList<Rp1210VendorDescriptor> Parse(string iniText)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        var sections = ParseSections(iniText);
        if (!sections.TryGetValue("VendorInformation", out var vendorInfo))
            return Array.Empty<Rp1210VendorDescriptor>();

        var result = new List<Rp1210VendorDescriptor>();
        foreach (var (key, value) in vendorInfo)
        {
            if (!key.StartsWith("Vendor", StringComparison.OrdinalIgnoreCase))
                continue;

            var fields = SplitFields(value);
            if (fields.Count < 2)
                continue;

            var vendorName = fields[0];
            var deviceName = fields[1];

            string dllPath = "";
            var protocols = new List<string>();
            if (sections.TryGetValue(key, out var vendorSection))
            {
                dllPath = FirstValue(vendorSection, "APIImplementationDLL", "DLL");
                protocols.AddRange(SplitFields(FirstValue(vendorSection, "Protocols", "")).Where(p => !string.IsNullOrWhiteSpace(p)));
            }

            if (string.IsNullOrWhiteSpace(dllPath))
                continue;

            result.Add(new Rp1210VendorDescriptor(
                Id: $"rp1210:{key}",
                VendorName: vendorName,
                DeviceName: string.IsNullOrWhiteSpace(deviceName) ? vendorName : deviceName,
                DllPath: dllPath,
                Protocols: protocols));
        }

        return result;
    }

    /// <summary>Parses an INI document into sections of ordered key/value pairs.</summary>
    public static IReadOnlyDictionary<string, List<(string Key, string Value)>> ParseSections(string iniText)
    {
        var sections = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        foreach (var rawLine in iniText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close > 1)
                    current = line[1..close].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null)
                continue;

            var key = line[..eq].Trim();
            var value = Unquote(line[(eq + 1)..].Trim());

            if (!sections.TryGetValue(current, out var pairs))
            {
                pairs = new List<(string, string)>();
                sections[current] = pairs;
            }
            pairs.Add((key, value));
        }

        return sections;
    }

    /// <summary>Splits a pipe-separated field list, honoring the surrounding quotes removed earlier.</summary>
    public static IReadOnlyList<string> SplitFields(string value) =>
        value.Split('|').Select(static f => f.Trim()).Where(static f => f.Length > 0).ToArray();

    /// <summary>
    /// Maps RP1210 protocol strings to the app's <see cref="Core.Enums.ProtocolType"/>. Unknown protocol
    /// strings are ignored; an empty list yields the default J1939/J1708/OBD2 set.
    /// </summary>
    public static IReadOnlyList<Core.Enums.ProtocolType> MapProtocols(IReadOnlyList<string> protocols)
    {
        if (protocols.Count == 0)
            return new[] { Core.Enums.ProtocolType.J1939, Core.Enums.ProtocolType.J1708, Core.Enums.ProtocolType.OBD2 };

        var result = new List<Core.Enums.ProtocolType>();
        foreach (var name in protocols)
        {
            switch (name.ToUpperInvariant())
            {
                case "J1708":
                case "J1587":
                    result.Add(Core.Enums.ProtocolType.J1708);
                    break;
                case "J1939":
                case "CAN":
                    result.Add(Core.Enums.ProtocolType.J1939);
                    break;
                case "OBD2":
                case "OBD":
                case "ISO15765":
                    result.Add(Core.Enums.ProtocolType.OBD2);
                    break;
            }
        }
        return result.Distinct().ToArray();
    }

    private static string FirstValue(List<(string Key, string Value)> pairs, params string[] keys)
    {
        foreach (var key in keys)
        {
            var match = pairs.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value))
                return match.Value;
        }
        return "";
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }
}
