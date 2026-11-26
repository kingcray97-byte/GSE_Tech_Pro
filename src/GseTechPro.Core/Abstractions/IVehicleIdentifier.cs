using GseTechPro.Core.Contracts;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Identifies the connected vehicle and produces a <see cref="VehicleProfile"/> with
/// <see cref="Enums.VehicleProfileSource.Auto"/>. Implemented by device-comms (step 3): it reads the
/// VIN via <see cref="IDiagnosticAdapter.GetVehicleInfoAsync"/> (OBD-II Mode 9 PID 02 for light
/// duty; J1939 PGN 65260 / J1587 for heavy duty) and decodes it with standard rules (WMI → make and
/// region, 10th character → model year, 8th character → engine only where publicly mapped).
///
/// <para><b>Contract obligations for implementers:</b></para>
/// <list type="bullet">
/// <item>Run off the UI thread (async, never blocks).</item>
/// <item>Honour <paramref name="cancellationToken"/>; the ~15 s timeout is the implementer's
/// concern (link a <c>CancellationTokenSource</c> with a 15 s delay).</item>
/// <item>Never invent values: undecodable fields stay <c>null</c> and the UI shows "Unknown".
/// No online lookup (offline rule).</item>
/// <item>Work against the simulator first (simulator-first rule).</item>
/// </list>
/// </summary>
public interface IVehicleIdentifier
{
    /// <summary>
    /// Reads and decodes the vehicle identity via <paramref name="adapter"/>. Returns <c>null</c>
    /// when the vehicle could not be identified (no answer, timeout, or cancellation) — never a
    /// profile with invented values. The result carries <see cref="VehicleProfile.Source"/> =
    /// <see cref="Enums.VehicleProfileSource.Auto"/>.
    /// </summary>
    Task<VehicleProfile?> IdentifyAsync(IDiagnosticAdapter adapter, CancellationToken cancellationToken = default);
}
