using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>
/// A reusable safety warning shown before a diagnostic step or test-point procedure.
/// Table: safety_warnings. Content is authored by gse-domain-expert in
/// <c>data/troubleshooting/safety_warnings.json</c> and seeded verbatim (id = kebab-case slug,
/// ADR-009). Referenced by <see cref="TestPoint.SafetyWarningIds"/>,
/// <see cref="TroubleshootingStep.SafetyWarningIds"/>, and the <c>guided_tests</c> table as a JSON
/// array of ids; the AI service resolves those ids to display strings on the <c>GuidedStep</c> wire DTO.
/// </summary>
public sealed record SafetyWarning : EntityBase
{
    public SafetyWarningCategory Category { get; init; } = SafetyWarningCategory.General;
    /// <summary>The warning text the tech sees (shown before the related step, §4.5 #2).</summary>
    public string Text { get; init; } = "";
    /// <summary>Citation (manual + page, regulatory/OSHA ref) or 'TODO: needs spec from manual'.</summary>
    public string? SourceCitation { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}
