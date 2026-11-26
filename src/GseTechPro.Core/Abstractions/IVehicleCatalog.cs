using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Vehicle year/make/model catalog behind the selector modal's Years / Makes / Models tabs.
/// Implemented in a later step (owner: gse-domain-expert) over seeded JSON files
/// <c>data/vehicles/{domestic,asian,european,heavy_duty}.json</c>, loaded once and cached in memory.
///
/// <para><b>Sync vs async:</b> <see cref="LoadAsync"/> is the only I/O (reading the JSON) and must
/// be awaited off the UI thread — once at startup, or lazily on first use. The
/// <see cref="GetYears"/> / <see cref="GetMakes"/> / <see cref="GetModels"/> queries are pure
/// in-memory lookups over already-loaded data, so they are synchronous: no async plumbing for a
/// dictionary read, and trivially testable against an in-memory implementation.</para>
///
/// <para><b>Nullable-argument semantics:</b> a <c>null</c> filter means "all" (no filtering on that
/// dimension). <paramref name="region"/> is always required (non-null) because the catalog is
/// partitioned by region.</para>
/// </summary>
public interface IVehicleCatalog
{
    /// <summary>
    /// Loads (or reloads) the catalog from its seed data. Idempotent and safe to call more than once;
    /// implementations must not block the calling thread.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>All model years in the catalog, newest first (e.g. 2026, 2025, …).</summary>
    IReadOnlyList<VehicleYear> GetYears();

    /// <summary>Makes for <paramref name="region"/>, optionally narrowed to a model year (<c>null</c> = all years).</summary>
    IReadOnlyList<VehicleMake> GetMakes(VehicleRegion region, int? year = null);

    /// <summary>
    /// Models for <paramref name="region"/> + <paramref name="year"/>, optionally narrowed to a make
    /// (<c>null</c> = all makes). A null <paramref name="year"/> means "all years"; a null
    /// <paramref name="make"/> means "all makes".
    /// </summary>
    IReadOnlyList<VehicleModel> GetModels(VehicleRegion region, int? year = null, string? make = null);
}
