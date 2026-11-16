using GseTechPro.Core.Enums;
using GseTechPro.Core.Units;

namespace GseTechPro.Core.Models;

/// <summary>Shared make/model catalog (TUG 660-28, 600i, ...). Table: equipment_models.</summary>
public sealed record EquipmentModel : EntityBase
{
    public string Make { get; init; } = "";
    public string Model { get; init; } = "";
    public EquipmentCategory? Category { get; init; }
    public string? EngineMake { get; init; }
    public string? EngineModel { get; init; }
    public string? Transmission { get; init; }
    public string? FuelType { get; init; }
    public string? SystemNotes { get; init; }
    public UnitSystem? DefaultUnitSystem { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>Universal registry: a piece of equipment OR a vehicle. Table: assets.</summary>
public sealed record Asset : EntityBase
{
    public AssetKind AssetKind { get; init; } = AssetKind.Equipment;
    /// <summary>Human asset number (unique).</summary>
    public string AssetNumber { get; init; } = "";
    public string? Name { get; init; }
    public string? ModelId { get; init; }
    public string? SerialNumber { get; init; }
    public AssetStatus Status { get; init; } = AssetStatus.InService;
    public string? LocationStation { get; init; }
    public double? HourMeter { get; init; }
    public string? HourMeterAsOf { get; init; }
    /// <summary>Flexible per-asset config (JSON).</summary>
    public string? ConfigurationJson { get; init; }
    public string? Notes { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>1:1 extension for fleet/HD vehicles. Table: vehicles (PK = asset_id).</summary>
public sealed record Vehicle
{
    public string AssetId { get; init; } = "";
    public string? Vin { get; init; }
    public string? LicensePlate { get; init; }
    public string? FleetNumber { get; init; }
    public int? Year { get; init; }
    public string? Engine { get; init; }
    public string? Transmission { get; init; }
    public double? Odometer { get; init; }
    public string? OdometerAsOf { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}
