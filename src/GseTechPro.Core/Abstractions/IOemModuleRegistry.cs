using GseTechPro.Core.Contracts;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Ordered registry of <see cref="IOemModule"/>s. A DI singleton; modules register at startup and
/// are resolved by <see cref="IOemModule.CanHandle"/> when a <see cref="VehicleProfile"/> is saved,
/// so add-on modules need no UI change. <b>Registration order is significant</b>: the first
/// matching module wins, so specific OEM modules are registered before the generic fallback (last).
/// Resolution is synchronous (pure matching); the caller then awaits
/// <see cref="IOemModule.LoadAsync"/> on the result.
/// </summary>
public interface IOemModuleRegistry
{
    /// <summary>The modules in registration order (specific first, generic last).</summary>
    IReadOnlyList<IOemModule> Modules { get; }

    /// <summary>
    /// Registers <paramref name="module"/>, appending it to the end of the order (so it has lower
    /// priority than anything registered earlier). Re-registering an <see cref="IOemModule.Id"/>
    /// replaces the earlier registration in place.
    /// </summary>
    void Register(IOemModule module);

    /// <summary>
    /// Returns the first registered module whose <see cref="IOemModule.CanHandle"/> accepts
    /// <paramref name="profile"/>, or <c>null</c> when <paramref name="profile"/> is <c>null</c> or
    /// no module matches. A <c>null</c> result means "no OEM module installed for this make" and the
    /// caller loads generic diagnostics instead.
    /// </summary>
    IOemModule? Resolve(VehicleProfile? profile);
}
