using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>Standard SPN/FMI, OBD, manufacturer code reference. Table: dtc_library.</summary>
public sealed record DtcLibraryEntry : EntityBase
{
    public ProtocolType Protocol { get; init; }
    /// <summary>e.g. 'SPN 94 FMI 3' or 'P0230'.</summary>
    public string Code { get; init; } = "";
    public int? Spn { get; init; }
    public int? Fmi { get; init; }
    public string? Description { get; init; }
    public string? System { get; init; }
    public string? Severity { get; init; }
    public string? LikelyCauses { get; init; }
    /// <summary>Manual/standard citation.</summary>
    public string? SourceCitation { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>Captured freeze-frame PID snapshot. Table: freeze_frames.</summary>
public sealed record FreezeFrame : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string CapturedAt { get; init; } = Clock.UtcNow;
    /// <summary>JSON: [{name,value,unit}, ...].</summary>
    public string PidsJson { get; init; } = "[]";
    public string CreatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>A code seen on a specific vehicle/equipment. Table: dtcs.</summary>
public sealed record Dtc : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string? LibraryId { get; init; }
    /// <summary>As read from the bus.</summary>
    public string Code { get; init; } = "";
    public ProtocolType Protocol { get; init; }
    public int? Spn { get; init; }
    public int? Fmi { get; init; }
    public DtcStatus Status { get; init; } = DtcStatus.Active;
    public string? Description { get; init; }
    public string? Severity { get; init; }
    public string ObservedAt { get; init; } = Clock.UtcNow;
    public string? FreezeFrameId { get; init; }
    public DtcSource Source { get; init; } = DtcSource.Scan;
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}
