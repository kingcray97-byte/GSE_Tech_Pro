"""Wire models for the AI service (ADR-008). Field names are snake_case and match
src/GseTechPro.Core/Contracts/AiServiceContracts.cs and AiContracts.cs exactly.

Changing a field here is a contract change: update the ADR and the C# DTOs in the same change.
"""

from __future__ import annotations

from typing import Literal, Optional

from pydantic import BaseModel, ConfigDict, Field

ChunkKindCode = Literal[
    "text", "table", "procedure", "specs", "troubleshooting", "wiring", "torque", "diagram_caption"
]
ChatRole = Literal["system", "user", "assistant", "tool"]
ServiceStatus = Literal["starting", "ok", "degraded"]
DegradationReason = Literal[
    "model_runtime_unreachable", "model_loading", "chat_model_missing", "embedding_model_missing", "db_unavailable"
]
RouteName = Literal["tablet", "shop-server", "cloud"]
RoutingMode = Literal["tablet-only", "prefer-shop-server", "prefer-cloud"]
IngestState = Literal["pending", "running", "succeeded", "failed", "cancelled"]
Verdict = Literal["pass", "fail", "na"]
CitationKind = Literal["manual-page", "work-order", "dtc"]
ErrorCode = Literal[
    "unauthorized", "invalid_request", "validation_error", "not_found", "conflict",
    "ai_unavailable", "ai_starting", "ingest_failed", "unsupported", "internal",
]


class _Wire(BaseModel):
    model_config = ConfigDict(extra="forbid")


# ---------------------------------------------------------------- errors


class AiError(_Wire):
    code: ErrorCode
    message: str
    detail: Optional[str] = None


class AiErrorEnvelope(_Wire):
    error: AiError


# ---------------------------------------------------------------- health / config


class RouteStatusOut(_Wire):
    route: RouteName
    configured: bool
    available: bool
    model: Optional[str] = None
    message: Optional[str] = None


class AiServiceHealth(_Wire):
    status: ServiceStatus
    model_runtime_reachable: bool
    ollama_reachable: bool  # deprecated (ADR-012): same value as model_runtime_reachable, kept one release
    chat_available: bool
    search_available: bool
    chat_model: str
    embedding_model: str
    degradation_reason: Optional[DegradationReason] = None
    version: str
    routes: list[RouteStatusOut] = Field(default_factory=list)


class AiServiceConfigResponse(_Wire):
    base_url: str
    chat_model: str
    embedding_model: str
    embedding_dimension: int
    idle_unload_minutes: int


# ---------------------------------------------------------------- chat


class ChatMessageIn(BaseModel):
    """Request message. `extra=ignore` so the App may send fields this version does not use."""

    model_config = ConfigDict(extra="ignore")

    role: ChatRole
    content: str = Field(default="", max_length=8000)
    tool_call_id: Optional[str] = None


class RoutingIn(BaseModel):
    """ADR-013. Missing = tablet only. cloud_allowed / share_job_details_with_cloud are administrator settings."""

    model_config = ConfigDict(extra="ignore")

    mode: RoutingMode = "tablet-only"
    cloud_allowed: bool = False
    share_job_details_with_cloud: bool = False


class ChatRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    messages: list[ChatMessageIn] = Field(min_length=1, max_length=50)
    work_order_id: Optional[str] = None
    asset_id: Optional[str] = None
    use_tools: bool = True
    system_instruction: Optional[str] = Field(default=None, max_length=2000)
    routing: Optional[RoutingIn] = None


class Citation(_Wire):
    kind: CitationKind
    display_text: str
    manual_id: Optional[str] = None
    page_start: Optional[int] = None
    work_order_number: Optional[str] = None


class ChatResponse(_Wire):
    text: str
    is_suggestion: bool = True
    citations: list[Citation] = Field(default_factory=list)
    route: RouteName = "tablet"  # ADR-013: which route answered
    model: Optional[str] = None


# ---------------------------------------------------------------- vision (Phase 7.4)


class VisionInspectRequest(BaseModel):
    """POST /vision/inspect. Matches Core PhotoInspectionRequest. The image is checked in vision.inspect.decode_image."""

    image_base64: str = Field(min_length=1, max_length=5_600_000)  # 4 MB of image, base64
    media_type: Literal["image/jpeg", "image/png"] = "image/jpeg"
    caption: Optional[str] = Field(default=None, max_length=500)
    work_order_id: Optional[str] = None
    asset_id: Optional[str] = None
    is_simulated_photo: bool = False
    routing: Optional[RoutingIn] = None


class VisionInspectResponse(_Wire):
    description: str
    notes: list[str] = Field(default_factory=list)
    citations: list[Citation] = Field(default_factory=list)
    is_suggestion: bool = True
    search_query: Optional[str] = None
    route: RouteName = "tablet"
    model: Optional[str] = None


# ---------------------------------------------------------------- search


class SearchManualsRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    query: str = Field(min_length=1, max_length=500)
    top_k: int = Field(default=5, ge=1, le=50)
    manual_id: Optional[str] = None
    chunk_kinds: Optional[list[ChunkKindCode]] = None
    include_text: bool = True


class SearchResultOut(_Wire):
    chunk_id: str
    manual_id: str
    manual_title: str
    page_start: int
    page_end: int
    section: Optional[str] = None
    text: Optional[str] = None
    score: float
    citation: str


class SearchManualsResponse(_Wire):
    results: list[SearchResultOut]
    fallback: bool


# ---------------------------------------------------------------- repair history (ADR-010 §7)

MatchTier = Literal["model", "family", "all"]


class RepairHistorySearchRequest(BaseModel):
    """Request for POST /search/repair-history. `extra=ignore` so the App may send fields this version does not use."""

    model_config = ConfigDict(extra="ignore")

    work_order_id: str = Field(min_length=1)
    query_text: Optional[str] = Field(default=None, max_length=2000)
    top_k: int = Field(default=5, ge=1, le=50)


class RepairCandidate(_Wire):
    candidate_repair_id: str
    repair_description: str
    system: Optional[str] = None
    match_tier: MatchTier
    similarity_score: float
    success_rate: float
    success_count: int
    attempt_count: int
    average_labor_hours: Optional[float] = None
    last_done_at: Optional[str] = None
    source_work_order_ids: list[str]
    evidence: str


class RepairHistorySearchResponse(_Wire):
    candidates: list[RepairCandidate]
    fallback: bool


# ---------------------------------------------------------------- ingest


class IngestRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    paths: list[str] = Field(min_length=1, max_length=200)
    force_reingest: bool = False


class IngestAccepted(_Wire):
    job_id: str


class IngestionErrorOut(_Wire):
    file: str
    message: str
    page: Optional[int] = None


class IngestionStatus(_Wire):
    job_id: str
    state: IngestState
    files_total: int
    files_done: int
    chunks_created: int
    chunks_embedded: int
    current_file: Optional[str] = None
    errors: list[IngestionErrorOut] = Field(default_factory=list)


# ---------------------------------------------------------------- guided


class GuidedStartRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    equipment_model_id: str = Field(min_length=1, max_length=64)
    symptom_id: Optional[str] = None
    complaint_text: Optional[str] = Field(default=None, max_length=2000)


class GuidedNextRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    session_id: str = Field(min_length=1, max_length=64)
    step_id: Optional[str] = None
    verdict: Optional[Verdict] = None
    reading_value: Optional[float] = None
    reading_unit: Optional[str] = Field(default=None, max_length=16)
    answer: Optional[str] = Field(default=None, max_length=2000)


class GuidedStep(_Wire):
    session_id: str
    available: bool
    tree_id: Optional[str] = None
    step_id: Optional[str] = None
    step_order: int = 0
    instruction: str
    question: Optional[str] = None
    safety_warnings: list[str] = Field(default_factory=list)
    reading_type: Optional[str] = None
    test_point_id: Optional[str] = None
    spec_min: Optional[float] = None
    spec_max: Optional[float] = None
    spec_source: Optional[str] = None
    terminal_result: Optional[str] = None
    next_on_pass: Optional[str] = None
    next_on_fail: Optional[str] = None
