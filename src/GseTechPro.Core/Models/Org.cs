using GseTechPro.Core.Enums;
using GseTechPro.Core.Units;

namespace GseTechPro.Core.Models;

/// <summary>One row per install (customer shop name from first-run wizard). Table: shops.</summary>
public sealed record Shop : EntityBase
{
    public string Name { get; init; } = "";
    public UnitSystem DefaultUnitSystem { get; init; } = UnitSystem.Imperial;
    public string? DefaultLocale { get; init; }
    public string? Timezone { get; init; }
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>A technician who can sign work and take PIN-protected actions. Table: technicians.</summary>
public sealed record Technician : EntityBase
{
    public string ShopId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>Salted hash; null = no PIN.</summary>
    public string? PinHash { get; init; }
    public TechnicianRole? Role { get; init; }
    public bool Active { get; init; } = true;
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>Key/value settings (theme, AI port, idle timeout, ...). Table: app_settings. PK = key.</summary>
public sealed record AppSettings
{
    public string Key { get; init; } = "";
    /// <summary>JSON value (use System.Text.Json to read/write).</summary>
    public string ValueJson { get; init; } = "null";
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}
