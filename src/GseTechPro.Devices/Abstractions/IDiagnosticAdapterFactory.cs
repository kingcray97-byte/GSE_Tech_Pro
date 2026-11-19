using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices.Abstractions;

/// <summary>
/// Creates <see cref="IDiagnosticAdapter"/> instances. The UI (and DI container) depend on this
/// interface so it never has to know whether it is talking to the in-process simulator (Phase 1)
/// or a real J2534/RP1210 adapter behind the 32-bit device host (Phase 2).
/// </summary>
/// <remarks>
/// Implementations that spawn the 32-bit device host own its lifecycle: <see cref="IAsyncDisposable"/>
/// tears the host process down (spawned on first use, disposed when the app is done).
/// </remarks>
public interface IDiagnosticAdapterFactory : IAsyncDisposable
{
    /// <summary>
    /// Creates a simulated adapter that emits clearly-labeled, reproducible fake traffic for the
    /// given protocol. Used by the UI "Simulator" device and by every test before real hardware.
    /// </summary>
    /// <param name="protocol">One of J1939, J1708/J1587, or OBD II. UDS/Manufacturer are not simulated.</param>
    /// <param name="seed">Deterministic seed for the noise generator. Omit for the default seed.</param>
    IDiagnosticAdapter CreateSimulator(ProtocolType protocol, int? seed = null);

    /// <summary>
    /// Lists devices known to the 32-bit device host (spawning the host on first use). In round 1 the
    /// host reports only the simulator; round 2 adds real J2534/RP1210 devices discovered in-process.
    /// </summary>
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a host-backed adapter for <paramref name="device"/> (spawning the host on first use).
    /// The returned adapter's <see cref="IDiagnosticAdapter.ConnectAsync"/> negotiates the device.
    /// </summary>
    /// <param name="protocol">Optional protocol; when null the adapter picks the device's first (or auto) protocol.</param>
    Task<IDiagnosticAdapter> ConnectHostAsync(DeviceInfo device, ProtocolType? protocol = null, CancellationToken cancellationToken = default);
}
