using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Diagnostic bus adapter (J2534/RP1210, HD-OBD, OBD II, simulator). Implemented by the
/// simulator in Phase 1 and by real adapters behind the 32-bit device host in Phase 2.
///
/// Threading: methods are async and must never block; the in-process simulator runs directly
/// while the real host marshals over named pipes. <see cref="LiveDataReceived"/> is raised on a
/// background thread — consumers must marshal to the UI thread before touching view state.
///
/// Live-data streaming uses the <see cref="LiveDataReceived"/> event (push) rather than
/// <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/> because it maps cleanly to WinUI
/// binding, survives the named-pipe boundary, and supports multiple simultaneous listeners.
/// </summary>
public interface IDiagnosticAdapter
{
    /// <summary>Human-readable adapter name (e.g. "TOPDON RLink X3", "Simulator").</summary>
    string Name { get; }

    /// <summary>True once <see cref="ConnectAsync"/> has completed successfully.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// True when this adapter produces simulated/demo data rather than real hardware readings.
    /// Real adapters (J2534/RP1210 behind the 32-bit host) keep the default <c>false</c>. This is a
    /// default interface member (not abstract) only so the Phase-1 <c>SimulatedDiagnosticAdapter</c>
    /// keeps compiling until device-comms overrides it in Phase 2 — <b>the simulator MUST override
    /// this to <c>true</c></b> (one line: <c>public bool IsSimulated =&gt; true;</c>). Consumers use it
    /// to badge simulated data in the UI and to stop simulated values being persisted as if real
    /// (see <see cref="PidValue.IsSimulated"/> / <see cref="DtcReadout.IsSimulated"/> for the
    /// per-value flag that crosses the process boundary).
    /// </summary>
    bool IsSimulated => false;

    /// <summary>Protocols this adapter can speak.</summary>
    IReadOnlyList<ProtocolType> SupportedProtocols { get; }

    /// <summary>Open the connection (and negotiate protocol).</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Close the connection. Idempotent.</summary>
    Task DisconnectAsync();

    /// <summary>Read all DTCs currently reported by the ECU/bus.</summary>
    Task<IReadOnlyList<DtcReadout>> ReadDTCsAsync(CancellationToken cancellationToken = default);

    /// <summary>Clear DTCs. May be rejected by the device/ECU; throws on failure.</summary>
    Task ClearDTCsAsync(CancellationToken cancellationToken = default);

    /// <summary>Read a one-shot snapshot of the requested PIDs by name. Empty list = adapter's default set.</summary>
    Task<IReadOnlyList<PidValue>> ReadLiveDataAsync(IReadOnlyList<string> pidNames, CancellationToken cancellationToken = default);

    /// <summary>Read the most recent freeze frame, or null when none is available.</summary>
    Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>Vehicle identification (VIN, protocol, ECU info).</summary>
    Task<VehicleInfo> GetVehicleInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised for each live-data update while streaming.</summary>
    event EventHandler<LiveDataUpdateEventArgs>? LiveDataReceived;
}
