using GseTechPro.Core.Contracts;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Local AI service client. Implemented in Phase 3 as an HTTP client to the FastAPI service on
/// 127.0.0.1 (default port 8765, <c>X-GSE-Token</c> header) — see ADR-008. The HTTP implementation
/// lives in <c>GseTechPro.App/Services/AiServiceClient.cs</c> (owned by <c>ui-winui</c>); Core
/// carries this interface and the DTOs only. The <c>ChatAsync</c>/<c>SearchManualsAsync</c> signatures
/// are stable; the full-response variants below expose citations and the degradation flag.
/// </summary>
public interface IAiClient
{
    /// <summary>Single- (or multi-message) chat against the local model. Returns assistant text.</summary>
    Task<string> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Hybrid (BM25 + vector, RRF) manual search; returns top-k chunks with citations.</summary>
    Task<IReadOnlyList<SearchResult>> SearchManualsAsync(string query, int topK, CancellationToken cancellationToken = default);

    /// <summary>Chat with citations + <see cref="ChatResponse.IsSuggestion"/> (for the suggestion label).</summary>
    Task<ChatResponse> ChatFullAsync(AiChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Manual search with filters and the <see cref="SearchManualsResponse.Fallback"/> flag.</summary>
    Task<SearchManualsResponse> SearchManualsFullAsync(SearchManualsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Similar-failure search over repair history, with the <see cref="RepairHistorySearchResponse.Fallback"/> flag.
    /// Default implementation throws: <c>ui-winui</c> implements the real HTTP call against
    /// <see cref="AiServiceProtocol.SearchRepairHistoryPath"/> in <c>GseTechPro.App/Services/AiServiceClient.cs</c>
    /// (Phase 5, ADR-010). Kept a default member so adding it does not break existing implementers.
    /// </summary>
    Task<RepairHistorySearchResponse> SearchRepairHistoryAsync(RepairHistorySearchRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "SearchRepairHistoryAsync is implemented in GseTechPro.App/Services/AiServiceClient.cs (Phase 5, ADR-010).");

    /// <summary>
    /// POST /vision/inspect: the model describes one photo and the service suggests manual pages (Phase 7.4).
    /// Default implementation throws so existing implementers keep building; the HTTP call is in
    /// <c>GseTechPro.App/Services/AiServiceClient.cs</c>.
    /// </summary>
    Task<PhotoInspectionResponse> InspectPhotoAsync(PhotoInspectionRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("InspectPhotoAsync is implemented in GseTechPro.App/Services/AiServiceClient.cs (Phase 7.4).");

    /// <summary>GET /health — liveness + degradation probe (drives "warming up / ready / degraded" UI).</summary>
    Task<AiServiceHealth> HealthAsync(CancellationToken cancellationToken = default);

    /// <summary>GET /config — effective runtime config (models, dimension, idle-unload).</summary>
    Task<AiServiceConfig> GetConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>POST /ingest — submit file(s)/folder(s) for import. Returns the ingestion job id.</summary>
    Task<string> IngestAsync(IngestRequest request, CancellationToken cancellationToken = default);

    /// <summary>GET /ingest/status — progress for an ingestion job.</summary>
    Task<IngestionStatus> GetIngestStatusAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>POST /guided/start — open a guided troubleshooting session (Phase 4 content).</summary>
    Task<GuidedStep> StartGuidedAsync(GuidedStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>POST /guided/next — advance the session with the tech's answer/reading.</summary>
    Task<GuidedStep> NextGuidedAsync(GuidedNextRequest request, CancellationToken cancellationToken = default);
}
