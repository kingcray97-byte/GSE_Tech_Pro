using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>Named test port per model, with spec from the manual. Table: test_points.</summary>
public sealed record TestPoint : EntityBase
{
    public string EquipmentModelId { get; init; } = "";
    /// <summary>e.g. 'Main hydraulic test port (G3)'.</summary>
    public string Name { get; init; } = "";
    public string? System { get; init; }
    public string? Location { get; init; }
    public ReadingType ReadingType { get; init; } = ReadingType.Other;
    public string? ExpectedUnit { get; init; }
    public double? SpecMin { get; init; }
    public double? SpecMax { get; init; }
    /// <summary>Citation "manual + page" or 'TODO: needs spec from manual'.</summary>
    public string? SpecSource { get; init; }
    public string? ProcedureRef { get; init; }
    /// <summary>JSON array of safety warning ids.</summary>
    public string? SafetyWarningIds { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>Every measurement a tech records. Table: readings.</summary>
public sealed record Reading : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string? TestPointId { get; init; }
    public ReadingType ReadingType { get; init; } = ReadingType.Other;
    public string? System { get; init; }
    /// <summary>e.g. 'P1 pump discharge', 'battery', 'chassis ground'.</summary>
    public string? ChannelComponent { get; init; }
    /// <summary>Canonical (SI) value: Pa, °C, m, L/min, V, A.</summary>
    public double ValueCanonical { get; init; }
    /// <summary>The SI unit the canonical value is in.</summary>
    public string UnitCanonical { get; init; } = "";
    /// <summary>Exactly what the tech typed/heard (preserved).</summary>
    public double? EnteredValue { get; init; }
    public string? EnteredUnit { get; init; }
    public double? SpecMin { get; init; }
    public double? SpecMax { get; init; }
    public string? SpecUnit { get; init; }
    /// <summary>Citation or 'TODO: needs spec from manual'.</summary>
    public string? SpecSource { get; init; }
    public PassFail? PassFail { get; init; }
    public ReadingSource Source { get; init; } = ReadingSource.Manual;
    /// <summary>
    /// True when the value came from a simulated source (ADR-009). Derived at write time:
    /// a reading saved from a replay/scan copies <c>recordings.source == 'simulated'</c>; a
    /// manually typed or voice-entered reading is <c>false</c>. Column: <c>readings.is_simulated</c>.
    /// </summary>
    public bool IsSimulated { get; init; }
    public string EnteredAt { get; init; } = Clock.UtcNow;
    public string? EnteredBy { get; init; }
    public string? Notes { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}
