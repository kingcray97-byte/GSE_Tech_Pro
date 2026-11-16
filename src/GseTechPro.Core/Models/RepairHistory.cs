using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>A diagnostic conclusion. Table: diagnoses.</summary>
public sealed record Diagnosis : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string Statement { get; init; } = "";
    public string? System { get; init; }
    public double? Confidence { get; init; }
    /// <summary>JSON array of citations: manual+page, WO numbers, DTC ids.</summary>
    public string SourcesJson { get; init; } = "[]";
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>What was done. Table: repairs.</summary>
public sealed record Repair : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string? DiagnosisId { get; init; }
    public string Description { get; init; } = "";
    public string? System { get; init; }
    public double? LaborHours { get; init; }
    public string PerformedAt { get; init; } = Clock.UtcNow;
    public string? PerformedBy { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>Parts catalog entry. Table: parts.</summary>
public sealed record Part : EntityBase
{
    public string PartNumber { get; init; } = "";
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Vendor { get; init; }
    public double? UnitCost { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>Part-replacement history (per asset, per work order). Table: parts_replaced.</summary>
public sealed record PartReplaced : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string? PartId { get; init; }
    /// <summary>Denormalized snapshot.</summary>
    public string PartNumber { get; init; } = "";
    /// <summary>Snapshot at install.</summary>
    public string? Description { get; init; }
    public int Quantity { get; init; } = 1;
    public string? Vendor { get; init; }
    public string? Warranty { get; init; }
    public string InstalledAt { get; init; } = Clock.UtcNow;
    public double? HoursAtInstall { get; init; }
    public double? MileageAtInstall { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>How the repair actually went. Table: outcomes.</summary>
public sealed record Outcome : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public OutcomeResult Result { get; init; }
    public string? Reason { get; init; }
    public string? ClosedBy { get; init; }
    public string ClosedAt { get; init; } = Clock.UtcNow;
    public bool ComebackDetected { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>Auto-detected repeat failure. Table: comebacks.</summary>
public sealed record Comeback : EntityBase
{
    public string OriginalWorkOrderId { get; init; } = "";
    public string ComebackWorkOrderId { get; init; } = "";
    /// <summary>Same asset + same system/symptom within window.</summary>
    public string? MatchReason { get; init; }
    public int? WindowDays { get; init; }
    public double? WindowHours { get; init; }
    /// <summary>True = system-detected; false = manual.</summary>
    public bool Auto { get; init; } = true;
    /// <summary>Tech confirms the comeback.</summary>
    public bool Confirmed { get; init; }
    public string DetectedAt { get; init; } = Clock.UtcNow;
    public string CreatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>Memoized repair suggestion (audit + learning). Table: recommendations.</summary>
public sealed record Recommendation : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string? CandidateRepairId { get; init; }
    public double SimilarityScore { get; init; }
    /// <summary>Smoothed success rate.</summary>
    public double SuccessRate { get; init; }
    /// <summary>JSON evidence, e.g. "Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, WO #4471".</summary>
    public string EvidenceJson { get; init; } = "";
    public int Rank { get; init; }
    /// <summary>Was shown to the tech?</summary>
    public bool Presented { get; init; }
    /// <summary>Did the tech follow it?</summary>
    public bool Accepted { get; init; }
    public string GeneratedAt { get; init; } = Clock.UtcNow;
    public string CreatedAt { get; init; } = Clock.UtcNow;
}
