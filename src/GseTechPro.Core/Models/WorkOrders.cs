using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>One per equipment/vehicle visit (Shop Foreman Pro "event file"). Table: work_orders.</summary>
public sealed record WorkOrder : EntityBase
{
    /// <summary>Human-readable work-order number (e.g. "004471"). Null for pre-numbering rows.</summary>
    public string? Number { get; init; }

    public string AssetId { get; init; } = "";
    public string? TechnicianId { get; init; }
    public WorkOrderStatus Status { get; init; } = WorkOrderStatus.Open;
    public string? Priority { get; init; }
    public string OpenedAt { get; init; } = Clock.UtcNow;
    public string? ClosedAt { get; init; }
    public string? ComplaintSummary { get; init; }
    public string? SymptomsSummary { get; init; }
    public string? DiagnosisSummary { get; init; }
    public string? RepairSummary { get; init; }
    public double? LaborHours { get; init; }
    public double? HoursAtOpen { get; init; }
    public double? MileageAtOpen { get; init; }
    /// <summary>Copy of asset config at open (JSON).</summary>
    public string? EquipmentSnapshotJson { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>Structured symptom pick-list entry. Table: symptoms.</summary>
public sealed record Symptom : EntityBase
{
    public string? Code { get; init; }
    public string Name { get; init; } = "";
    public SymptomCategory? Category { get; init; }
    public string? System { get; init; }
    public string? Description { get; init; }
    /// <summary>True = shipped pick-list entry; false = tech-created.</summary>
    public bool IsStandard { get; init; } = true;
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>Operator's own words (typed or spoken). Table: complaints.</summary>
public sealed record Complaint : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string Text { get; init; } = "";
    public RecordedBy RecordedBy { get; init; } = RecordedBy.Text;
    public string RecordedAt { get; init; } = Clock.UtcNow;
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>Many-to-many work order <-> symptom link. Table: work_order_symptoms (composite PK).</summary>
public sealed record WorkOrderSymptom
{
    public string WorkOrderId { get; init; } = "";
    public string SymptomId { get; init; } = "";
    public string NotedAt { get; init; } = Clock.UtcNow;
    public string? Notes { get; init; }
}
