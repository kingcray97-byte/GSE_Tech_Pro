using GseTechPro.Devices.Discovery;

namespace GseTechPro.Devices.Host32.Rp1210;

/// <summary>
/// Locates and reads the RP1210 INI file. Per the TMC RP1210 install convention the 32-bit API reads
/// <c>RP121032.ini</c> from the Windows directory (falling back to <c>RP1210.ini</c>); this wrapper also
/// checks the application directory. The pure parsing lives in <see cref="Rp1210IniParser"/> (AnyCPU,
/// unit-tested); only the file read lives here in the x86 host.
/// </summary>
public static class Rp1210Discovery
{
    /// <summary>Returns the RP1210 INI file path, or null when none is present.</summary>
    public static string? FindIniFile()
    {
        var searchDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            AppContext.BaseDirectory,
        };

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            foreach (var name in new[] { "RP121032.ini", "RP1210.ini" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                    return path;
            }
        }
        return null;
    }

    /// <summary>Reads and parses the RP1210 INI file into vendor descriptors (empty when absent).</summary>
    public static IReadOnlyList<Rp1210VendorDescriptor> Discover()
    {
        var path = FindIniFile();
        if (path is null)
            return Array.Empty<Rp1210VendorDescriptor>();
        return Rp1210IniParser.Parse(File.ReadAllText(path));
    }
}
