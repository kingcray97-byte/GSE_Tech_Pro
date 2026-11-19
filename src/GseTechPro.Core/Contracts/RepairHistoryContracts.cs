using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Contracts;

// Wire contract for POST /search/repair-history (ADR-010): similar-failure search over closed work
// orders, ranked by smoothed success rate. Field names are the snake_case JSON the FastAPI service
// (pydantic) and the C# client (System.Text.Json with SnakeCaseLower) both use; ai-engineer mirrors
// these field-for-field in ai-service/app/models.py.

/// <summary>
/// <c>POST /search/repair-history</c> request. The primary trigger is <see cref="WorkOrderId"/>:
/// the recommender reads that work order's symptoms, DTCs, readings, and complaint text to build
/// the query. <see cref="QueryText"/> appends extra free text the tech typed (or spoke).
/// </summary>
public sealed record RepairHistorySearchRequest
{
    /// <summary>UUID of the work order whose failure we are matching against history.</summary>
    public string WorkOrderId { get; init; } = "";

    /// <summary>Optional extra free text appended to the query blob (e.g. a tech's own words).</summary>
    public string? QueryText { get; init; }

    /// <summary>How many candidates to return. Default 5, min 1, max 50.</summary>
    public int TopK { get; init; } = 5;
}

/// <summary>
/// One candidate repair, grouped from one or more historical <c>repairs</c> rows that share the
/// same normalized identity (case-folded, trimmed <c>system</c> + <c>description</c>). All
/// success-rate fields are computed from recorded outcomes only — never invented (ADR-010 §6).
/// </summary>
public sealed record RepairCandidate
{
    /// <summary>The representative <c>repairs.id</c> (the most recent contributing repair row).</summary>
    public string CandidateRepairId { get; init; } = "";

    /// <summary>The repair's description text (from the most recent contributing row).</summary>
    public string RepairDescription { get; init; } = "";

    /// <summary>The repair's system (from <c>repairs.system</c>); null when unset.</summary>
    public string? System { get; init; }

    /// <summary>Which search tier produced this candidate (model / family / all).</summary>
    public MatchTier MatchTier { get; init; } = MatchTier.All;

    /// <summary>
    /// Similarity in [0,1]. Always the deterministic composite (ADR-010 §3); the optional vector
    /// path only changes which candidates surface and their order (via RRF), never this value.
    /// </summary>
    public double SimilarityScore { get; init; }

    /// <summary>Smoothed success rate in [0,1] (Wilson lower bound, 95%). Not the raw ratio.</summary>
    public double SuccessRate { get; init; }

    /// <summary>Raw count of <c>fixed</c> outcomes (minus confirmed comebacks).</summary>
    public int SuccessCount { get; init; }

    /// <summary>Raw count of non-<c>unsure</c> outcomes (<c>fixed</c> + <c>not_fixed</c>).</summary>
    public int AttemptCount { get; init; }

    /// <summary>Mean <c>repairs.labor_hours</c> across contributing rows; null when none recorded.</summary>
    public double? AverageLaborHours { get; init; }

    /// <summary>Most recent contributing <c>repairs.performed_at</c> (ISO-8601 UTC); null when unknown.</summary>
    public string? LastDoneAt { get; init; }

    /// <summary>UUIDs of the contributing work orders (the clickable sources).</summary>
    public IReadOnlyList<string> SourceWorkOrderIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Pre-rendered evidence string, e.g. "Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, last done 3
    /// weeks ago, WO #4471" (ADR-010 §5). Uses raw counts; the smoothed rate is in <see cref="SuccessRate"/>.
    /// </summary>
    public string Evidence { get; init; } = "";
}

/// <summary>
/// <c>POST /search/repair-history</c> response. <see cref="Fallback"/> is true when the embedding
/// model was unavailable and ranking was deterministic-only (degraded); false for the hybrid path.
/// </summary>
public sealed record RepairHistorySearchResponse
{
    /// <summary>Already ordered (rank 0 = best). The app must not re-sort.</summary>
    public IReadOnlyList<RepairCandidate> Candidates { get; init; } = Array.Empty<RepairCandidate>();

    public bool Fallback { get; init; }
}
