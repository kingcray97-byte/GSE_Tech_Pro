using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// An optional OEM-specific diagnostic module (screens, PIDs, procedures for one make or family of
/// makes). Modules are registered with <see cref="IOemModuleRegistry"/> at startup and resolved by
/// <see cref="CanHandle"/> when a <see cref="VehicleProfile"/> is saved; add-on modules therefore
/// need no UI change. One built-in generic module (owner: ui-winui, step 5) is registered last so
/// the flow works end to end with no OEM module installed.
/// </summary>
public interface IOemModule
{
    /// <summary>Stable module id (e.g. "gse.generic", "freightliner.hd"); unique within a registry.</summary>
    string Id { get; }

    /// <summary>
    /// Market region this module targets, or <c>null</c> when it is region-agnostic (the generic
    /// module). When non-null, <see cref="CanHandle"/> rejects profiles whose
    /// <see cref="VehicleProfile.Region"/> is a different region.
    /// </summary>
    VehicleRegion? Region { get; }

    /// <summary>
    /// Makes this module supports, matched case-insensitively. An empty list means "any make" (the
    /// generic module). A make that is not listed is rejected by <see cref="CanHandle"/>.
    /// </summary>
    IReadOnlyList<string> SupportedMakes { get; }

    /// <summary>
    /// Whether this module can handle <paramref name="profile"/>. The default implementation applies
    /// the shared rule (override for bespoke matching):
    /// <list type="bullet">
    /// <item>a <c>null</c> profile is never handled;</item>
    /// <item>a make-specific module (non-empty <see cref="SupportedMakes"/>) requires a known
    /// <see cref="VehicleProfile.Make"/> listed in it (case-insensitive);</item>
    /// <item>a region-specific module (non-null <see cref="Region"/>) requires a known
    /// <see cref="VehicleProfile.Region"/> equal to it.</item>
    /// </list>
    /// A generic module (empty makes, null region) therefore handles every non-null profile.
    /// </summary>
    bool CanHandle(VehicleProfile? profile)
    {
        if (profile is null)
            return false;

        // A make-specific module (non-empty list) requires a known, listed make.
        if (SupportedMakes.Count > 0)
        {
            if (profile.Make is null)
                return false;

            var matched = false;
            foreach (var make in SupportedMakes)
            {
                if (string.Equals(make, profile.Make, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
                return false;
        }

        // A region-specific module (non-null) requires a known, matching region.
        if (Region is not null)
        {
            if (profile.Region is null || profile.Region != Region)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Loads this module for <paramref name="profile"/> against the connected
    /// <paramref name="adapter"/> (negotiate protocol, load screens/PIDs, etc.). Called once the
    /// module is resolved and the session is saved. Must honour <paramref name="cancellationToken"/>.
    /// </summary>
    Task LoadAsync(VehicleProfile profile, IDiagnosticAdapter adapter, CancellationToken cancellationToken = default);
}
