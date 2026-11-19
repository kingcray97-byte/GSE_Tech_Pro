using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Ipc;

namespace GseTechPro.Devices;

/// <summary>
/// Round-1 host device source: exposes a single simulated device (J1939/J1708/OBD II) and connects
/// <see cref="SimulatedDiagnosticAdapter"/> instances. Real J2534/RP1210 devices replace this in
/// round 2 (vendor DLLs loaded only inside the x86 host process).
/// </summary>
public sealed class SimulatorDeviceProvider : IHostDeviceProvider
{
    public const string SimulatorDeviceId = "simulator";
    public const int DefaultSeed = 12345;

    public static readonly DeviceInfo SimulatorDevice = new()
    {
        Id = SimulatorDeviceId,
        Name = "Simulator",
        Kind = "simulator",
        Protocols = new[] { ProtocolType.J1939, ProtocolType.J1708, ProtocolType.OBD2 },
        IsSimulated = true,
    };

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DeviceInfo>>(new[] { SimulatorDevice });
    }

    /// <inheritdoc />
    public async Task<IDiagnosticAdapter> ConnectAsync(string deviceId, ProtocolType? protocol, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(deviceId, SimulatorDeviceId, StringComparison.Ordinal))
            throw new HostIpcException(HostErrorCodes.DeviceNotFound, $"Unknown device '{deviceId}'.");

        var selected = protocol ?? ProtocolType.J1939;
        if (selected is not (ProtocolType.J1939 or ProtocolType.J1708 or ProtocolType.OBD2))
            throw new HostIpcException(HostErrorCodes.Unsupported, $"Protocol '{selected}' is not supported by the simulator.");

        var adapter = new SimulatedDiagnosticAdapter(selected, DefaultSeed);
        await adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return adapter;
    }
}
