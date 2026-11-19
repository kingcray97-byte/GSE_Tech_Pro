using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>
/// Which widening tier of the recommendation search produced a candidate
/// (ARCHITECTURE.md §4.3; ADR-010). The tier is a hard precedence — same model
/// outranks same family outranks everything — recorded on each
/// <see cref="GseTechPro.Core.Contracts.RepairCandidate"/> so the UI can group results.
/// </summary>
public enum MatchTier
{
    /// <summary>Candidate's work order is on an asset with the same <c>assets.model_id</c> as the query asset.</summary>
    [EnumMember(Value = "model")] Model,

    /// <summary>Same <c>equipment_models.make</c> and <c>equipment_models.category</c>, different model.</summary>
    [EnumMember(Value = "family")] Family,

    /// <summary>Anything else (the widest tier).</summary>
    [EnumMember(Value = "all")] All,
}
