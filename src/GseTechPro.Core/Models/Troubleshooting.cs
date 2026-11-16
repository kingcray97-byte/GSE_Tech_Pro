using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>A troubleshooting decision tree. Table: troubleshooting_trees.</summary>
public sealed record TroubleshootingTree : EntityBase
{
    public string EquipmentModelId { get; init; } = "";
    public string? SymptomId { get; init; }
    public string Title { get; init; } = "";
    public string? System { get; init; }
    public TreeStatus Status { get; init; } = TreeStatus.Draft;
    public TreeSource Source { get; init; } = TreeSource.Handwritten;
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>A decision-tree node. Table: troubleshooting_steps.</summary>
public sealed record TroubleshootingStep : EntityBase
{
    public string TreeId { get; init; } = "";
    public int StepOrder { get; init; }
    /// <summary>e.g. "Measure pressure at port G3".</summary>
    public string Instruction { get; init; } = "";
    public string? Question { get; init; }
    public ReadingType? ReadingType { get; init; }
    public string? TestPointId { get; init; }
    public double? SpecMin { get; init; }
    public double? SpecMax { get; init; }
    /// <summary>Citation or 'TODO: needs spec from manual'.</summary>
    public string? SpecSource { get; init; }
    /// <summary>JSON array of safety warning ids.</summary>
    public string? SafetyWarningIds { get; init; }
    /// <summary>Step ids (null = terminal).</summary>
    public string? NextOnPass { get; init; }
    public string? NextOnFail { get; init; }
    /// <summary>Diagnosis/repair suggestion at a leaf.</summary>
    public string? TerminalResult { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}
