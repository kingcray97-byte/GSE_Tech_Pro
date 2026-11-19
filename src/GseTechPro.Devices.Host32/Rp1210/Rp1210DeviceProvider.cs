using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Discovery;
using GseTechPro.Devices.Ipc;

namespace GseTechPro.Devices.Host32.Rp1210;

/// <summary>
/// Vendor-backed <see cref="IHostDeviceProvider"/> for RP1210 adapters. <c>list-devices</c> reports
/// each vendor/device from RP121032.ini; <c>connect</c> loads the matching vendor API DLL (x86 host
/// only) and returns a <see cref="Rp1210DiagnosticAdapter"/>.
/// </summary>
public sealed class Rp1210DeviceProvider : IHostDeviceProvider
{
    private readonly IReadOnlyList<Rp1210VendorDescriptor> _vendors;

    /// <summary>Creates a provider over a fixed descriptor set (used by tests).</summary>
    public Rp1210DeviceProvider(IEnumerable<Rp1210VendorDescriptor> vendors)
    {
        _vendors = vendors.ToArray();
    }

    /// <summary>Creates a provider by discovering vendors from RP121032.ini.</summary>
    public static Rp1210DeviceProvider Discover() => new(Rp1210Discovery.Discover());

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DeviceInfo>>(_vendors.Select(v => new DeviceInfo
        {
            Id = v.Id,
            Name = $"{v.VendorName} {v.DeviceName}".Trim(),
            Kind = "rp1210",
            Protocols = Rp1210IniParser.MapProtocols(v.Protocols),
            IsSimulated = false,
        }).ToArray());
    }

    public Task<IDiagnosticAdapter> ConnectAsync(string deviceId, ProtocolType? protocol, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var vendor = _vendors.FirstOrDefault(v => string.Equals(v.Id, deviceId, StringComparison.Ordinal));
        if (vendor.Id is null)
            throw new HostIpcException(HostErrorCodes.DeviceNotFound, $"Unknown RP1210 device '{deviceId}'.");

        var selected = protocol ?? ProtocolType.J1708;
        if (selected is not (ProtocolType.J1939 or ProtocolType.J1708 or ProtocolType.OBD2))
            throw new HostIpcException(HostErrorCodes.Unsupported, $"Protocol '{selected}' is not supported over RP1210.");

        var api = Rp1210Api.Open(vendor.DllPath);
        return Task.FromResult<IDiagnosticAdapter>(new Rp1210DiagnosticAdapter(api, vendor, selected));
    }
}
