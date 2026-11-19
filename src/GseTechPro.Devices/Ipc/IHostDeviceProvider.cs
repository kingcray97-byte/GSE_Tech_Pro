using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices.Ipc;

/// <summary>
/// Host-side source of devices the 32-bit device host can drive. Round 1 backs this with the
/// simulator (<see cref="SimulatorDeviceProvider"/>). Round 2 adds a vendor-backed implementation in
/// <c>GseTechPro.Devices.Host32</c> that loads the x86 J2534/RP1210 DLLs and is injected into
/// <see cref="HostServer"/> by the host's entry point.
/// </summary>
public interface IHostDeviceProvider
{
    /// <summary>Devices the host can currently connect (installed/attached + the simulator).</summary>
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates and connects an adapter for the requested device/protocol.</summary>
    /// <exception cref="HostIpcException">Mapped to a <c>HostErrorCodes</c> value when the device or
    /// protocol is unknown/unsupported.</exception>
    Task<IDiagnosticAdapter> ConnectAsync(string deviceId, ProtocolType? protocol, CancellationToken cancellationToken = default);
}
