using GseTechPro.Core.Contracts;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Local AI service client. Implemented in Phase 3 as an HTTP client to the FastAPI service on
/// 127.0.0.1 (default port 8765, <c>X-GSE-Token</c> header). Stub now so callers can depend on
/// the shape before the service exists.
/// </summary>
public interface IAiClient
{
    /// <summary>Single- (or multi-message) chat against the local model. Returns assistant text.</summary>
    Task<string> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Hybrid (BM25 + vector, RRF) manual search; returns top-k chunks with citations.</summary>
    Task<IReadOnlyList<SearchResult>> SearchManualsAsync(string query, int topK, CancellationToken cancellationToken = default);
}
