using GseTechPro.Core.Contracts;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Discovers and opens oscilloscope devices (PicoScope, Hantek, simulator) behind the shared
/// <see cref="IScopeDevice"/> interface. Implemented in Phase 6 by <c>GseTechPro.Scope</c>; mirrors
/// the diagnostic adapter pattern so the scope screen can switch between simulator and real
/// hardware without touching vendor SDKs (ADR-011 §1/§3).
/// </summary>
public interface IScopeDeviceFactory
{
    /// <summary>
    /// Enumerate attached scope hardware. The returned list <b>always includes the simulator entry</b>
    /// (kind <see cref="Enums.ScopeDeviceKind.Simulator"/>, <c>IsSimulated = true</c>) so the app is
    /// usable with no hardware attached. Real devices have <c>IsSimulated = false</c>.
    /// </summary>
    Task<IReadOnlyList<ScopeDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a handle to a previously discovered device. The returned <see cref="IScopeDevice"/> is
    /// constructed and ready to use; call <see cref="IScopeDevice.ConnectAsync"/> to establish the
    /// connection. Throws when <paramref name="device"/> is unknown or the SDK failed to load.
    /// </summary>
    Task<IScopeDevice> OpenAsync(ScopeDeviceInfo device, CancellationToken cancellationToken = default);
}
