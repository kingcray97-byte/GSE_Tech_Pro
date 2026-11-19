using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Discovery;
using GseTechPro.Devices.Ipc;

namespace GseTechPro.Devices.Host32.J2534;

/// <summary>
/// Vendor-backed <see cref="IHostDeviceProvider"/> for J2534 pass-thru devices. <c>list-devices</c>
/// reports every device discovered from the registry; <c>connect</c> loads the matching vendor DLL
/// (in the x86 host only) and returns a <see cref="J2534DiagnosticAdapter"/>.
/// </summary>
public sealed class J2534DeviceProvider : IHostDeviceProvider
{
    private static readonly ProtocolType[] J2534Protocols = { ProtocolType.J1939, ProtocolType.J1708, ProtocolType.OBD2, ProtocolType.UDS };

    private readonly IReadOnlyList<J2534DeviceDescriptor> _devices;

    /// <summary>Creates a provider over a fixed descriptor set (used by tests).</summary>
    public J2534DeviceProvider(IEnumerable<J2534DeviceDescriptor> devices)
    {
        _devices = devices.ToArray();
    }

    /// <summary>Creates a provider by discovering installed devices from the registry.</summary>
    public static J2534DeviceProvider Discover() => new(J2534Discovery.Discover());

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DeviceInfo>>(_devices.Select(d => new DeviceInfo
        {
            Id = d.Id,
            Name = d.Name,
            Kind = "j2534",
            Protocols = J2534Protocols,
            IsSimulated = false,
        }).ToArray());
    }

    public Task<IDiagnosticAdapter> ConnectAsync(string deviceId, ProtocolType? protocol, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var descriptor = _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal));
        if (descriptor.Id is null)
            throw new HostIpcException(HostErrorCodes.DeviceNotFound, $"Unknown J2534 device '{deviceId}'.");

        var selected = protocol ?? ProtocolType.J1939;
        if (selected is not (ProtocolType.J1939 or ProtocolType.J1708 or ProtocolType.OBD2 or ProtocolType.UDS))
            throw new HostIpcException(HostErrorCodes.Unsupported, $"Protocol '{selected}' is not supported over J2534.");

        var passThru = J2534PassThru.Open(descriptor.DllPath);
        return Task.FromResult<IDiagnosticAdapter>(new J2534DiagnosticAdapter(passThru, descriptor, selected));
    }
}
