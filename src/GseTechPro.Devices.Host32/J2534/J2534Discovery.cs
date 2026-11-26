using GseTechPro.Devices.Discovery;
using Microsoft.Win32;

namespace GseTechPro.Devices.Host32.J2534;

/// <summary>
/// Host-side entry point for discovering installed J2534 pass-thru devices. The actual registry
/// enumeration lives in <see cref="J2534RegistryDiscovery"/> so it can be unit-tested without
/// loading the x86 host process. Because this helper process is 32-bit, it can only load 32-bit
/// vendor pass-thru DLLs. Those DLLs are registered under the 32-bit registry view
/// (<c>HKLM\SOFTWARE\WOW6432Node\...</c> on x64 Windows, exposed as
/// <see cref="RegistryView.Registry32"/>), so <see cref="Discover"/> reads only that view. Reading
/// the 64-bit view would return 64-bit vendor DLLs (for example TOPDON RLink X3's
/// <c>PassThru464.dll</c>) and <see cref="NativeLibrary.Load"/> would throw
/// <see cref="BadImageFormatException"/>.
/// </summary>
public static class J2534Discovery
{
    /// <summary>
    /// Discovers installed J2534 devices from the 32-bit registry view. The x86 host can only load
    /// 32-bit vendor DLLs, so the 64-bit registry view is intentionally not read.
    /// </summary>
    public static IReadOnlyList<J2534DeviceDescriptor> Discover()
        => J2534RegistryDiscovery.Discover(RegistryView.Registry32);

    /// <summary>
    /// Discovers installed J2534 devices from the specified registry view(s). The default
    /// <see cref="Discover()"/> path uses only the 32-bit view; this overload is exposed for callers
    /// that need explicit control.
    /// </summary>
    /// <param name="views">The registry view(s) to read.</param>
    public static IReadOnlyList<J2534DeviceDescriptor> Discover(params RegistryView[] views)
        => J2534RegistryDiscovery.Discover(views);
}
