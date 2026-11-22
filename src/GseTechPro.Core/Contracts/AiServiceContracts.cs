using GseTechPro.Core.Ai;
using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// Wire-level constants for the App ↔ local AI service (FastAPI on 127.0.0.1) HTTP protocol
/// (ARCHITECTURE.md §1.1, §4; ADR-008). These live in Core so the app's client, <c>ai-engineer</c>'s
/// FastAPI handlers, and any test double all reference the same names/numbers. The HTTP client itself
/// is implemented in <c>GseTechPro.App</c> (the only consumer); Core carries the constants and DTOs only.
/// </summary>
public static class AiServiceProtocol
{
    /// <summary>
    /// API contract version (integer protocol version, not the build string). Distinct from
    /// <see cref="AiServiceHealth.Version"/>, which is the service's build string (e.g. "0.3.0").
    /// </summary>
    public const int Version = 1;

    /// <summary>Bind host. Never <c>0.0.0.0</c>.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>Default port (config <c>service.port</c>; the app may override via <c>--port</c>).</summary>
    public const int DefaultPort = 8765;

    /// <summary>Default base URL when the port is <see cref="DefaultPort"/>.</summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:8765";

    /// <summary>Auth header required on every endpoint (per-install token, constant-time compared).</summary>
    public const string TokenHeader = "X-GSE-Token";

    /// <summary>Per-install token length in bytes (32 random bytes → 64 lowercase hex chars; same as ADR-007).</summary>
    public const int TokenBytes = 32;

    /// <summary>Per-install token length in hex characters (2 × <see cref="TokenBytes"/>).</summary>
    public const int TokenHexLength = TokenBytes * 2;

    // Endpoint paths (single source of truth for the client and the FastAPI router).
    public const string HealthPath = "/health";
    public const string ConfigPath = "/config";
    public const string ChatPath = "/chat";
    public const string SearchManualsPath = "/search/manuals";
    public const string SearchRepairHistoryPath = "/search/repair-history";
    public const string IngestPath = "/ingest";
    public const string IngestStatusPath = "/ingest/status";
    public const string GuidedStartPath = "/guided/start";
    public const string GuidedNextPath = "/guided/next";
    /// <summary>Phase 7.4 photo inspection (<see cref="PhotoInspectionRequest"/>).</summary>
    public const string VisionInspectPath = "/vision/inspect";
    /// <summary>Phase 7.5a speech to text (<see cref="VoiceTranscriptionRequest"/>).</summary>
    public const string VoiceTranscribePath = "/voice/transcribe";
}

/// <summary>Error codes returned in <see cref="AiError.Code"/> (mirrors <c>HostErrorCodes</c> style).</summary>
public static class AiErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string InvalidRequest = "invalid_request";
    public const string ValidationError = "validation_error";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    /// <summary>Chat capability unavailable (model runtime unreachable or chat model missing, on every allowed route).</summary>
    public const string AiUnavailable = "ai_unavailable";
    /// <summary>Chat model still warming up / loading (transient).</summary>
    public const string AiStarting = "ai_starting";
    public const string IngestFailed = "ingest_failed";
    public const string Unsupported = "unsupported";
    public const string Internal = "internal";
}

/// <summary>Structured error envelope returned for every non-2xx response (ADR-008).</summary>
public sealed record AiError
{
    public string Code { get; init; } = AiErrorCodes.Internal;
    public string Message { get; init; } = "";
    public string? Detail { get; init; }
}

/// <summary>
/// Outer wrapper for the non-2xx response body: <c>{ "error": { "code", "message", "detail" } }</c>.
/// The service serializes this shape; the client deserializes it to surface <see cref="AiError"/>.
/// </summary>
public sealed record AiErrorEnvelope
{
    public AiError Error { get; init; } = new();
}

/// <summary>GET /health — liveness + degradation probe. Always 200, even degraded.</summary>
public sealed record AiServiceHealth
{
    public AiServiceStatus Status { get; init; } = AiServiceStatus.Starting;
    /// <summary>The tablet's chat <c>llama-server</c> answered its health check (ADR-012).</summary>
    public bool ModelRuntimeReachable { get; init; }
    /// <summary>
    /// Deprecated wire field kept one release for older services (ADR-012); same value as
    /// <see cref="ModelRuntimeReachable"/>. Read <see cref="RuntimeReachable"/> instead.
    /// </summary>
    public bool OllamaReachable { get; init; }
    /// <summary>True when either the new or the deprecated field says the runtime answered.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RuntimeReachable => ModelRuntimeReachable || OllamaReachable;
    /// <summary>Chat is usable now on the tablet route (runtime ready and chat model loaded).</summary>
    public bool ChatAvailable { get; init; }
    /// <summary>Manual search is usable (SQLite open; BM25-only when no embedding model).</summary>
    public bool SearchAvailable { get; init; }
    /// <summary>Configured chat model name (from config, not necessarily loaded).</summary>
    public string? ChatModel { get; init; }
    /// <summary>Configured embedding model name (from config).</summary>
    public string? EmbeddingModel { get; init; }
    /// <summary>
    /// e.g. "model_runtime_unreachable", "model_loading" (older services: "ollama_unreachable");
    /// null when <see cref="Status"/> is <see cref="AiServiceStatus.Ok"/>.
    /// </summary>
    public string? DegradationReason { get; init; }
    /// <summary>One entry per route (tablet, shop server, cloud), ADR-013. Empty from older services.</summary>
    public IReadOnlyList<AiRouteStatus> Routes { get; init; } = Array.Empty<AiRouteStatus>();
    /// <summary>Service build version string.</summary>
    public string Version { get; init; } = "";
}

/// <summary>GET /config — the effective runtime config the app displays.</summary>
public sealed record AiServiceConfig
{
    /// <summary>The service's own bound URL including the effective port.</summary>
    public string BaseUrl { get; init; } = AiServiceProtocol.DefaultBaseUrl;
    public string ChatModel { get; init; } = "";
    public string EmbeddingModel { get; init; } = "";
    /// <summary>Vector dimension (768 for nomic-embed-text); never mix dimensions.</summary>
    public int EmbeddingDimension { get; init; } = 768;
    /// <summary>Minutes of idle traffic before the service stops its llama-server processes (ADR-012).</summary>
    public int IdleUnloadMinutes { get; init; } = 10;
}

/// <summary>
/// POST /chat response. <see cref="Text"/> is the assistant reply; <see cref="Citations"/> are its
/// sources; <see cref="IsSuggestion"/> drives the "AI output is a suggestion" safety label (§4.5 #3).
/// </summary>
public sealed record ChatResponse
{
    public string Text { get; init; } = "";
    /// <summary>True for every model-generated answer (the service always sets true for AI text).</summary>
    public bool IsSuggestion { get; init; } = true;
    public IReadOnlyList<Citation> Citations { get; init; } = Array.Empty<Citation>();
    /// <summary>Route that produced the answer (ADR-013). Null from older services, which are tablet-only.</summary>
    public AiRoute? Route { get; init; }
    /// <summary>Model label that answered (from the service config).</summary>
    public string? Model { get; init; }
}

/// <summary>A source cited by the model (manual page, past work order, or DTC).</summary>
public sealed record Citation
{
    public CitationKind Kind { get; init; } = CitationKind.ManualPage;
    /// <summary>Human-readable source string, e.g. "TUG 660-28 Maintenance Manual, page 12".</summary>
    public string DisplayText { get; init; } = "";
    /// <summary>Present when <see cref="Kind"/> is <see cref="CitationKind.ManualPage"/>.</summary>
    public string? ManualId { get; init; }
    /// <summary>Present when <see cref="Kind"/> is <see cref="CitationKind.ManualPage"/>.</summary>
    public int? PageStart { get; init; }
    /// <summary>Present when <see cref="Kind"/> is <see cref="CitationKind.WorkOrder"/>.</summary>
    public string? WorkOrderNumber { get; init; }
}

/// <summary>POST /search/manuals request (full form; the stable <c>SearchManualsAsync</c> is a convenience).</summary>
public sealed record SearchManualsRequest
{
    public string Query { get; init; } = "";
    /// <summary>Default 5, max 50.</summary>
    public int TopK { get; init; } = 5;
    /// <summary>Optional manual filter (null = all manuals).</summary>
    public string? ManualId { get; init; }
    /// <summary>Optional chunk-kind filter (null = all kinds).</summary>
    public IReadOnlyList<ChunkKind>? ChunkKinds { get; init; }
    /// <summary>Include chunk text in results (default true; false returns citations only).</summary>
    public bool IncludeText { get; init; } = true;
}

/// <summary>
/// POST /search/manuals response. <see cref="Fallback"/> is true when the embedding model was
/// unavailable and ranking was BM25-only (degraded); false for the normal hybrid path.
/// </summary>
public sealed record SearchManualsResponse
{
    public IReadOnlyList<SearchResult> Results { get; init; } = Array.Empty<SearchResult>();
    public bool Fallback { get; init; }
}

/// <summary>POST /ingest request — submit file(s)/folder(s) for import (async).</summary>
public sealed record IngestRequest
{
    /// <summary>Absolute file or folder paths chosen by the user.</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    /// <summary>Re-import even when a manual's file hash is unchanged.</summary>
    public bool ForceReingest { get; init; }
}

/// <summary>POST /ingest <c>202 Accepted</c> body: <c>{ "job_id": "&lt;uuid&gt;" }</c>.</summary>
public sealed record IngestResponse
{
    public string JobId { get; init; } = "";
}

/// <summary>GET /ingest/status — progress for one ingestion job.</summary>
public sealed record IngestionStatus
{
    public string JobId { get; init; } = "";
    public IngestionState State { get; init; } = IngestionState.Pending;
    public int FilesTotal { get; init; }
    public int FilesDone { get; init; }
    /// <summary>Chunks written so far.</summary>
    public int ChunksCreated { get; init; }
    /// <summary>Chunks that got a vector row; &lt; <see cref="ChunksCreated"/> when the embedding model was unavailable.</summary>
    public int ChunksEmbedded { get; init; }
    /// <summary>File currently being processed (null unless <see cref="State"/> is running).</summary>
    public string? CurrentFile { get; init; }
    public IReadOnlyList<IngestionError> Errors { get; init; } = Array.Empty<IngestionError>();
}

/// <summary>A per-file ingestion failure (non-fatal; reported in <see cref="IngestionStatus.Errors"/>).</summary>
public sealed record IngestionError
{
    public string File { get; init; } = "";
    public string Message { get; init; } = "";
    public int? Page { get; init; }
}

/// <summary>POST /guided/start request — open a guided troubleshooting session (Phase 4 content).</summary>
public sealed record GuidedStartRequest
{
    public string EquipmentModelId { get; init; } = "";
    public string? SymptomId { get; init; }
    /// <summary>Optional free-text complaint when no structured symptom is picked.</summary>
    public string? ComplaintText { get; init; }
}

/// <summary>POST /guided/next request — advance the session with the tech's answer/reading.</summary>
public sealed record GuidedNextRequest
{
    public string SessionId { get; init; } = "";
    /// <summary>The step being answered (from the previous <see cref="GuidedStep.StepId"/>).</summary>
    public string StepId { get; init; } = "";
    /// <summary>Pass/fail verdict the app computed against the spec range (§4.2).</summary>
    public PassFail? Verdict { get; init; }
    /// <summary>Optional raw reading (service canonicalizes + records).</summary>
    public double? ReadingValue { get; init; }
    /// <summary>Unit as entered; canonicalized server-side.</summary>
    public string? ReadingUnit { get; init; }
    /// <summary>Optional free-text answer for question steps.</summary>
    public string? Answer { get; init; }
}

/// <summary>
/// One troubleshooting step (or the "no tree yet" placeholder in Phase 3, when
/// <see cref="Available"/> is false). Shape is fixed now so Phase 4 tree content lands without a
/// contract change.
/// </summary>
public sealed record GuidedStep
{
    public string SessionId { get; init; } = "";
    /// <summary>False in Phase 3 (no tree content yet); true once a tree matches.</summary>
    public bool Available { get; init; }
    public string? TreeId { get; init; }
    public string? StepId { get; init; }
    public int StepOrder { get; init; }
    /// <summary>Instruction text, or the "no tree yet" message when <see cref="Available"/> is false.</summary>
    public string Instruction { get; init; } = "";
    public string? Question { get; init; }
    /// <summary>Resolved safety-warning display strings (shown before the step, §4.5 #2).</summary>
    public IReadOnlyList<string> SafetyWarnings { get; init; } = Array.Empty<string>();
    public ReadingType? ReadingType { get; init; }
    public string? TestPointId { get; init; }
    public double? SpecMin { get; init; }
    public double? SpecMax { get; init; }
    /// <summary>Citation or 'TODO: needs spec from manual'.</summary>
    public string? SpecSource { get; init; }
    /// <summary>Diagnosis/repair suggestion when this is a leaf.</summary>
    public string? TerminalResult { get; init; }
    public string? NextOnPass { get; init; }
    public string? NextOnFail { get; init; }
}
