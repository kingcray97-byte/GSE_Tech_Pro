using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// The vehicle a diagnostic session is (or will be) working on. This is the richer, session-level
/// model used for routing, the selector modal, and OEM-module loading. It is distinct from
/// <see cref="VehicleInfo"/>, which is the raw identification the adapter reports over the bus:
/// <c>VehicleInfo</c> carries only <c>Vin</c> / <c>Protocol</c> / <c>Ecus</c>, while
/// <see cref="VehicleProfile"/> adds the decoded <see cref="Year"/> / <see cref="Make"/> /
/// <see cref="Model"/> / <see cref="Engine"/> / <see cref="Region"/> and the
/// <see cref="Source"/> provenance. The vehicle identifier maps <see cref="VehicleInfo"/> → a
/// profile (Auto); the selector modal builds a profile directly (Manual). Both remain separate so
/// the adapter's raw report never carries decoded values and never needs to know about regions.
/// <para>Fields the app does not yet know are <c>null</c> — nothing is invented (offline, no VIN
/// lookup service).</para>
/// </summary>
public sealed record VehicleProfile
{
    /// <summary>17-character VIN when known; <c>null</c> when not read or not entered.</summary>
    public string? Vin { get; init; }

    /// <summary>Model year; <c>null</c> when unknown (not decodable / not entered).</summary>
    public int? Year { get; init; }

    /// <summary>Vehicle make (e.g. "Freightliner"); <c>null</c> when unknown.</summary>
    public string? Make { get; init; }

    /// <summary>Vehicle model (e.g. "Cascadia"); <c>null</c> when unknown.</summary>
    public string? Model { get; init; }

    /// <summary>Engine, only where the VIN maps it publicly (position 8); <c>null</c> = "Unknown" in the UI.</summary>
    public string? Engine { get; init; }

    /// <summary>Market region; <c>null</c> when unknown (e.g. auto-detection could not map the WMI).</summary>
    public VehicleRegion? Region { get; init; }

    /// <summary>How this profile was produced. Defaults to <see cref="VehicleProfileSource.Manual"/>;
    /// <see cref="Abstractions.IVehicleIdentifier"/> overrides to <see cref="VehicleProfileSource.Auto"/>.</summary>
    public VehicleProfileSource Source { get; init; } = VehicleProfileSource.Manual;

    /// <summary>Bus protocol; <c>null</c> when unknown (manual selection leaves protocol to the adapter on connect).</summary>
    public ProtocolType? Protocol { get; init; }
}

/// <summary>
/// The saved default VCI for one diagnostic mode (light-duty J2534 vs heavy-duty RP1210). Stores the
/// device's stable <see cref="Id"/> and <see cref="Kind"/> — never a hardcoded name or DLL path —
/// plus a display <see cref="Name"/> snapshot. Persisted by <c>VciSettingsService</c> (step 2,
/// owner ui-winui) under the <c>vci.default.j2534</c> / <c>vci.default.heavy_duty</c> settings keys.
/// </summary>
public sealed record DefaultVciSelection
{
    /// <summary>Device id, matching <see cref="DeviceInfo.Id"/>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display-name snapshot (from <see cref="DeviceInfo.Name"/>).</summary>
    public string Name { get; init; } = "";

    /// <summary>Device class: <see cref="DeviceKinds.J2534"/> | <see cref="DeviceKinds.Rp1210"/> | <see cref="DeviceKinds.Simulator"/>.</summary>
    public string Kind { get; init; } = "";
}

/// <summary>A model year in the seeded vehicle catalog, returned newest-first.</summary>
public sealed record VehicleYear
{
    /// <summary>The year, e.g. 2026.</summary>
    public int Year { get; init; }
}

/// <summary>A vehicle make in the seeded catalog for a region (optionally narrowed to a year).</summary>
public sealed record VehicleMake
{
    /// <summary>Display/matching name, e.g. "Freightliner".</summary>
    public string Name { get; init; } = "";
}

/// <summary>A vehicle model in the seeded catalog for a region + year + make.</summary>
public sealed record VehicleModel
{
    /// <summary>Display/matching name, e.g. "Cascadia".</summary>
    public string Name { get; init; } = "";
}
