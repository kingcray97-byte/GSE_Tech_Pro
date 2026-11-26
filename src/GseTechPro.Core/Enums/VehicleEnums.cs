using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>
/// Geographic market a vehicle was built for. Partitions the make/model catalog so the selector
/// modal and OEM-module routing filter to a manageable list. The <see cref="EnumMemberAttribute"/>
/// value is the stable string code used by the seeded catalog files
/// (<c>data/vehicles/{domestic,asian,european,heavy_duty}.json</c>, owned by gse-domain-expert)
/// and, later, by persistence.
/// <para>
/// These enums are <b>not persisted in this step</b> (nothing reads them from the database yet), so
/// they do not go through <see cref="DbEnum"/>. The string codes are declared now so a later
/// persistence change is additive and the file-name mapping is unambiguous.
/// </para>
/// </summary>
public enum VehicleRegion
{
    /// <summary>North-American light-duty vehicles (GM, Ford, Chrysler, …).</summary>
    [EnumMember(Value = "domestic")] Domestic,

    /// <summary>Asian light-duty vehicles (Toyota, Honda, Hyundai, …).</summary>
    [EnumMember(Value = "asian")] Asian,

    /// <summary>European light-duty vehicles (Volkswagen, Mercedes-Benz, BMW, …).</summary>
    [EnumMember(Value = "european")] European,

    /// <summary>Heavy-duty trucks and equipment (Freightliner, Kenworth, Peterbilt, Volvo, International, …).</summary>
    [EnumMember(Value = "heavy_duty")] HeavyDuty,
}

/// <summary>How a <see cref="GseTechPro.Core.Contracts.VehicleProfile"/> was produced.</summary>
public enum VehicleProfileSource
{
    /// <summary>Derived by <see cref="GseTechPro.Core.Abstractions.IVehicleIdentifier"/> from a read VIN.</summary>
    [EnumMember(Value = "auto")] Auto,

    /// <summary>Entered by the technician in the vehicle-selector modal (Years / Makes / Models).</summary>
    [EnumMember(Value = "manual")] Manual,
}
