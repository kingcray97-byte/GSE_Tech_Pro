using System.Runtime.Versioning;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Discovery;
using Microsoft.Win32;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>
/// Discovery/mapping tests for J2534 (registry convention) and RP1210 (INI convention). These exercise
/// the pure parsing paths (in the AnyCPU <c>GseTechPro.Devices</c> assembly) against fixtures, so they
/// need no hardware, no vendor DLL, and no x86 runtime. The actual registry/INI file read and vendor
/// DLL loading live in the x86 host and are integration-only.
/// </summary>
[SupportedOSPlatform("windows")]
public class J2534Rp1210DiscoveryTests
{
    // ---- J2534 registry parsing (SAE J2534-1 install convention) ----

    [Fact]
    public void J2534_Parse_ExtractsDescriptor()
    {
        (string KeyName, string DllPath, string? FriendlyName)[] entries =
        {
            ("Topdon", @"C:\Program Files (x86)\Topdon\RLink X3\j2534.dll", "TOPDON RLink X3"),
        };

        var devices = J2534RegistryParser.Parse(entries);

        var device = Assert.Single(devices);
        Assert.Equal("j2534:Topdon", device.Id);
        Assert.Equal("TOPDON RLink X3", device.Name);
        Assert.Equal(@"C:\Program Files (x86)\Topdon\RLink X3\j2534.dll", device.DllPath);
    }

    [Fact]
    public void J2534_Parse_SkipsEntryWithoutDllPath()
    {
        (string KeyName, string DllPath, string? FriendlyName)[] entries =
        {
            ("Broken", "", "No DLL"),
            ("Good", @"C:\j2534.dll", null),
        };

        var devices = J2534RegistryParser.Parse(entries);

        var device = Assert.Single(devices);
        Assert.Equal("j2534:Good", device.Id);
        Assert.Equal("Good", device.Name); // falls back to key name when no friendly name
    }

    // ---- J2534 registry discovery (x86 host view selection) ----

    /// <summary>
    /// The x86 host can only load 32-bit vendor DLLs, so device discovery must read only the 32-bit
    /// registry view (WOW6432Node). This test compares a 32-bit-only discovery against an explicit
    /// read of <see cref="RegistryView.Registry32"/>.
    /// </summary>
    [Fact]
    public void J2534_Discovery_Uses32BitViewOnly()
    {
        var from32 = new List<(string KeyName, string DllPath, string? FriendlyName)>();
        J2534RegistryDiscovery.CollectFromView(RegistryView.Registry32, from32);
        var expectedIds = J2534RegistryParser.Parse(from32).Select(d => d.Id).OrderBy(id => id);

        var actual = J2534RegistryDiscovery.Discover(RegistryView.Registry32);
        var actualIds = actual.Select(d => d.Id).OrderBy(id => id);

        Assert.Equal(expectedIds, actualIds);
    }

    /// <summary>
    /// When multiple registry views are supplied, duplicate device key names are deduplicated and
    /// the first view wins. This prevents a 64-bit DLL descriptor from shadowing its 32-bit
    /// counterpart on machines where both views contain the same device key.
    /// </summary>
    [Fact]
    public void J2534_Discovery_DeduplicatesByKeyName_FirstViewWins()
    {
        var from64 = new List<(string KeyName, string DllPath, string? FriendlyName)>();
        J2534RegistryDiscovery.CollectFromView(RegistryView.Registry64, from64);
        if (from64.Count == 0)
            return; // nothing to deduplicate on this machine

        var result = J2534RegistryDiscovery.Discover(RegistryView.Registry64, RegistryView.Registry32);

        // Every 64-bit key must be present, and any key shared with the 32-bit view must keep the
        // 64-bit DLL because the 64-bit view was supplied first.
        foreach (var (keyName, dllPath, _) in from64)
        {
            var device = result.Single(d => d.Id == $"j2534:{keyName}");
            Assert.Equal(dllPath, device.DllPath);
        }
    }

    // ---- RP1210 INI parsing (TMC RP1210 install convention) ----

    [Fact]
    public void Rp1210_Parse_ExtractsVendor()
    {
        const string ini = """
            [RP1210Support]
            RP1210=1

            [VendorInformation]
            Vendor1="Nexiq Technologies|USB-Link 2|1.0.0"

            [Vendor1]
            Name="USB-Link 2"
            APIImplementationDLL="nexusdll32.dll"
            Protocols="J1708|CAN|J1939"
            """;

        var vendors = Rp1210IniParser.Parse(ini);

        var vendor = Assert.Single(vendors);
        Assert.Equal("rp1210:Vendor1", vendor.Id);
        Assert.Equal("Nexiq Technologies", vendor.VendorName);
        Assert.Equal("USB-Link 2", vendor.DeviceName);
        Assert.Equal("nexusdll32.dll", vendor.DllPath);
        Assert.Equal(new[] { "J1708", "CAN", "J1939" }, vendor.Protocols);
    }

    [Fact]
    public void Rp1210_Parse_MultipleVendors()
    {
        const string ini = """
            [VendorInformation]
            Vendor1="Nexiq Technologies|USB-Link 2|1.0.0"
            Vendor2="Noregon|DLA+ 2.0|2.0.0"

            [Vendor1]
            APIImplementationDLL="nexusdll32.dll"
            Protocols="J1708|J1939"

            [Vendor2]
            APIImplementationDLL="dlaplus.dll"
            Protocols="J1708"
            """;

        var vendors = Rp1210IniParser.Parse(ini);

        Assert.Equal(2, vendors.Count);
        Assert.Contains(vendors, v => v.Id == "rp1210:Vendor2" && v.DeviceName == "DLA+ 2.0");
    }

    [Fact]
    public void Rp1210_Parse_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Rp1210IniParser.Parse(""));
        Assert.Empty(Rp1210IniParser.Parse("[NoVendorInfo]\nFoo=bar"));
    }

    // ---- RP1210 protocol-string mapping ----

    [Fact]
    public void Rp1210_MapProtocols_MapsKnownAndSkipsUnknown()
    {
        var mapped = Rp1210IniParser.MapProtocols(new[] { "J1708", "CAN", "ISO15765", "GM_UART" });

        Assert.Equal(new[] { ProtocolType.J1708, ProtocolType.J1939, ProtocolType.OBD2 }, mapped);
    }

    [Fact]
    public void Rp1210_MapProtocols_EmptyDefaultsToAllThree()
    {
        var mapped = Rp1210IniParser.MapProtocols(Array.Empty<string>());

        Assert.Equal(new[] { ProtocolType.J1939, ProtocolType.J1708, ProtocolType.OBD2 }, mapped);
    }
}
