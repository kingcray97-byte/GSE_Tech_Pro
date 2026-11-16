using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices.Abstractions;

/// <summary>
/// Creates <see cref="IDiagnosticAdapter"/> instances. The UI (and DI container) depend on this
/// interface so it never has to know whether it is talking to the in-process simulator (Phase 1)
/// or a real J2534/RP1210 adapter behind the 32-bit device host (Phase 2).
/// </summary>
public interface IDiagnosticAdapterFactory
{
    /// <summary>
    /// Creates a simulated adapter that emits clearly-labeled, reproducible fake traffic for the
    /// given protocol. Used by the UI "Simulator" device and by every test before real hardware.
    /// </summary>
    /// <param name="protocol">One of J1939, J1708/J1587, or OBD II. UDS/Manufacturer are not simulated.</param>
    /// <param name="seed">Deterministic seed for the noise generator. Omit for the default seed.</param>
    IDiagnosticAdapter CreateSimulator(ProtocolType protocol, int? seed = null);

    // Phase 2: create real J2534 / RP1210 adapters here. Those return adapters that marshal
    // commands to the 32-bit device host (GseTechPro.Devices.Host32.exe) over named pipes.
    //   IDiagnosticAdapter CreateJ2534(DeviceInfo device, string channel);
    //   IDiagnosticAdapter CreateRp1210(DeviceInfo device, string clientId);
}
