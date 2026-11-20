"""HTTP routes (ADR-008). Every route is behind the X-GSE-Token middleware (app/security.py).

Blocking work (SQLite, llama-server, LiteLLM) runs in FastAPI's thread pool: these handlers are plain `def`.
"""

from __future__ import annotations

import logging
import math
import sqlite3
from pathlib import Path
from typing import Optional

from fastapi import APIRouter, Query, Request

from app import SERVICE_VERSION
from app import troubleshooting as tb
from app import units
from app.chat import run_chat
from app.errors import ApiError
from app.guided import NO_TREE_MESSAGE
from app.llm import ChatUnavailable
from app.routing import CLOUD, CLOUD_SAFE_TOOLS, TABLET, plan_routes
from app.models import (
    RouteStatusOut,
    RoutingIn,
    AiServiceConfigResponse,
    AiServiceHealth,
    ChatRequest,
    ChatResponse,
    GuidedNextRequest,
    GuidedStartRequest,
    GuidedStep,
    IngestAccepted,
    IngestionStatus,
    IngestRequest,
    RepairHistorySearchRequest,
    RepairHistorySearchResponse,
    SearchManualsRequest,
    SearchManualsResponse,
    SearchResultOut,
)
from app.perf import log_event
from app.state import ServiceState
from ingest.job import get_job
from ingest.pipeline import start_ingest_job
from rag.config import VEC0_DIM
from rag.db import connect
from recommender import UnknownWorkOrderError, run_repair_history_search
from tools.manuals import run_manual_search

router = APIRouter()
log = logging.getLogger("gse.ai.guided")
_INVALID_ROLES = frozenset({"system", "tool"})


def _svc(request: Request) -> ServiceState:
    return request.app.state.svc  # type: ignore[no-any-return]


@router.get("/health", response_model=AiServiceHealth)
def health(request: Request) -> AiServiceHealth:
    svc = _svc(request)
    cfg = svc.config
    db_ok = svc.db_ok()
    if not svc.warm.is_set():
        # Booting: report 'starting' without probing, so a slow or hung runtime cannot stall liveness.
        return AiServiceHealth(
            status="starting",
            model_runtime_reachable=False,
            ollama_reachable=False,
            chat_available=False,
            search_available=db_ok,
            chat_model=cfg.models.chat,
            embedding_model=cfg.models.embedding,
            degradation_reason=None,
            version=SERVICE_VERSION,
        )
    avail = svc.availability(db_ok)
    return AiServiceHealth(
        status=avail.status,
        model_runtime_reachable=avail.model_runtime_reachable,
        ollama_reachable=avail.model_runtime_reachable,  # deprecated alias, one release (ADR-012)
        chat_available=avail.chat_available,
        search_available=avail.search_available,
        chat_model=cfg.models.chat,
        embedding_model=cfg.models.embedding,
        degradation_reason=avail.degradation_reason,  # None only when every capability is ready
        version=SERVICE_VERSION,
        routes=[RouteStatusOut(**vars(s)) for s in svc.route_statuses(avail)],
    )


@router.get("/config", response_model=AiServiceConfigResponse)
def config(request: Request) -> AiServiceConfigResponse:
    cfg = _svc(request).config
    return AiServiceConfigResponse(
        base_url=f"http://{cfg.service.host}:{cfg.service.port}",
        chat_model=cfg.models.chat,
        embedding_model=cfg.models.embedding,
        embedding_dimension=VEC0_DIM,
        idle_unload_minutes=cfg.idle_unload_minutes,
    )


@router.post("/chat", response_model=ChatResponse)
def chat(body: ChatRequest, request: Request) -> ChatResponse:
    svc = _svc(request)
    if any(m.role in _INVALID_ROLES for m in body.messages):
        raise ApiError(400, "invalid_request", "Only user and assistant messages can be sent to /chat.")
    offline_message = svc.config.degradation.offline_chat_message
    if not svc.warm.is_set():
        raise ApiError(503, "ai_starting", "AI is starting.", detail="warm-up probe in progress")
    routing = body.routing or RoutingIn()
    routes_cfg = svc.config.routes
    plan = plan_routes(routing.mode, routing.cloud_allowed, routes_cfg.shop_server.configured, routes_cfg.cloud.configured)
    tool_ctx = svc.tool_context()
    if svc.unloader is not None:
        svc.unloader.touch()  # before a possible cold start, so the idle timer cannot stop the server mid-answer
    with svc.chat_lock:  # one generation at a time on a CPU-only tablet
        answered: Optional[tuple[str, object, str]] = None
        for route in plan:
            if route == TABLET:
                break
            ok, _ = svc.route_health.check(route)  # cached; a route that failed its last check is skipped, no wait
            if not ok:
                continue
            backend = svc.backend_for(route)
            strip = route == CLOUD and not routing.share_job_details_with_cloud
            try:
                run = run_chat(
                    body,
                    backend=backend,
                    registry=svc.registry,
                    tool_ctx=tool_ctx,
                    system_prompt=svc.system_prompt,
                    include_job_context=not strip,
                    allowed_tools=CLOUD_SAFE_TOOLS if strip else None,
                )
            except ChatUnavailable as exc:
                svc.route_health.mark_down(route)
                log.warning("chat route %s failed (%s); falling back", route, exc.code)
                continue
            answered = (route, run, getattr(backend, "label", None) or "")
            break
        if answered is None:
            reason = svc.ensure_tablet_ready()
            if reason == "model_loading":
                raise ApiError(503, "ai_starting", "AI is starting.", detail="model_loading")
            if reason == "chat_model_missing":
                raise ApiError(503, "ai_unavailable", offline_message,
                               detail=f"chat_model_missing: '{svc.config.models.chat}' is not installed on this tablet")
            if reason is not None:
                raise ApiError(503, "ai_unavailable", offline_message, detail=reason)
            try:
                run = run_chat(
                    body,
                    backend=svc.backend,
                    registry=svc.registry,
                    tool_ctx=tool_ctx,
                    system_prompt=svc.system_prompt,
                )
            except ChatUnavailable as exc:
                raise ApiError(503, exc.code, offline_message if exc.code == "ai_unavailable" else "AI is starting.",
                               detail=exc.detail) from exc
            answered = (TABLET, run, svc.config.models.chat)
    route, run, model_label = answered
    if svc.unloader is not None:
        svc.unloader.touch()
    log_event(
        "chat",
        route=route,
        model_turns=run.model_turns,
        tool_calls=run.tool_calls,
        sources=run.sources_used,
        first_token_ms=run.first_token_ms,
        total_ms=run.total_ms,
        unverified_note=run.unverified_note_added,
    )
    return run.response.model_copy(update={"route": route, "model": model_label or None})


@router.post("/search/manuals", response_model=SearchManualsResponse)
def search_manuals(body: SearchManualsRequest, request: Request) -> SearchManualsResponse:
    svc = _svc(request)
    if not svc.db_ok():
        raise ApiError(503, "ai_unavailable", "Manual search is unavailable: the database could not be opened.",
                       detail="db_unavailable")
    try:
        out = run_manual_search(
            svc.db_path,  # type: ignore[arg-type]  # db_ok() guarantees a path
            body.query,
            body.top_k,
            query_embedder=svc.query_embedder(),
            manual_id=body.manual_id,
            chunk_kinds=body.chunk_kinds,
            include_text=body.include_text,
        )
    except ValueError as exc:
        raise ApiError(422, "validation_error", str(exc)) from exc
    if svc.unloader is not None:
        svc.unloader.touch()
    log_event("search", top_k=body.top_k, results=len(out["results"]), fallback=out["fallback"])
    return SearchManualsResponse(
        results=[SearchResultOut(**r) for r in out["results"]],
        fallback=bool(out["fallback"]),
    )


@router.post("/search/repair-history", response_model=RepairHistorySearchResponse)
def search_repair_history(body: RepairHistorySearchRequest, request: Request) -> RepairHistorySearchResponse:
    """Similar past repairs for a work order (ADR-010 §7). Deterministic ranking; vectors only when available."""
    svc = _svc(request)
    if not svc.db_ok():
        raise ApiError(503, "ai_unavailable", "Repair history search is unavailable: the database could not be opened.",
                       detail="db_unavailable")
    try:
        out = run_repair_history_search(
            svc.db_path,  # type: ignore[arg-type]  # db_ok() guarantees a path
            body.work_order_id,
            body.query_text,
            body.top_k,
            query_embedder=svc.query_embedder(),
        )
    except UnknownWorkOrderError as exc:
        raise ApiError(404, "not_found", "No work order matches this id.") from exc
    except ValueError as exc:
        raise ApiError(422, "validation_error", str(exc)) from exc
    except FileNotFoundError as exc:
        raise ApiError(503, "ai_unavailable", "Repair history search is unavailable: the database could not be opened.",
                       detail="db_unavailable") from exc
    except sqlite3.Error as exc:
        log.warning("repair history search: database read failed")
        raise ApiError(500, "internal", "Repair history search failed. Try again.") from exc
    if svc.unloader is not None:
        svc.unloader.touch()
    log_event(
        "search_repair_history",
        top_k=body.top_k,
        candidates=len(out["candidates"]),
        fallback=out["fallback"],
    )
    return RepairHistorySearchResponse(candidates=out["candidates"], fallback=bool(out["fallback"]))


def _is_unc(raw: str) -> bool:
    """UNC / network paths (\\\\server\\share, //server/share, \\\\?\\UNC\\...). Opening them can start SMB/NTLM traffic."""
    return raw.startswith(("\\\\", "//"))


def _validate_ingest_path(raw: str) -> None:
    """Offline-first guard for /ingest: local absolute paths only, never a network share or a whole drive.

    UNC is rejected on the raw string before any filesystem call, so nothing is touched on the network.
    Drive roots (C:\\) are rejected because an ingest would walk the entire disk.
    """
    if _is_unc(raw):
        raise ApiError(400, "invalid_request", "Network (UNC) paths are not allowed. Copy the manual to a local folder.")
    path = Path(raw)
    if not path.is_absolute():
        raise ApiError(400, "invalid_request", "Ingest paths must be absolute file or folder paths.")
    resolved = path.resolve()
    if _is_unc(str(resolved)) or _is_unc(resolved.drive):
        raise ApiError(400, "invalid_request", "Network (UNC) paths are not allowed. Copy the manual to a local folder.")
    if resolved == Path(resolved.anchor):
        raise ApiError(400, "invalid_request", "A drive or filesystem root cannot be ingested. Pick a manuals folder.")


@router.post("/ingest", status_code=202, response_model=IngestAccepted)
def ingest(body: IngestRequest, request: Request) -> IngestAccepted:
    svc = _svc(request)
    if svc.db_path is None:
        raise ApiError(500, "ingest_failed", "No database is configured for ingestion.")
    for raw in body.paths:
        _validate_ingest_path(raw)
    job = start_ingest_job(
        body.paths,
        db_path=svc.db_path,
        force_reingest=body.force_reingest,
        config=svc.config,
        embedder=svc.document_embedder(),
    )
    log_event("ingest_start", paths=len(body.paths), force=body.force_reingest)
    return IngestAccepted(job_id=job.job_id)


@router.get("/ingest/status", response_model=IngestionStatus)
def ingest_status(job_id: str = Query(min_length=1, max_length=64)) -> IngestionStatus:
    try:
        job = get_job(job_id)
    except KeyError as exc:
        raise ApiError(404, "not_found", "Unknown ingestion job.") from exc
    return IngestionStatus(**job.to_status())


def _no_tree_step(session_id: str) -> GuidedStep:
    return GuidedStep(session_id=session_id, available=False, step_order=0, instruction=NO_TREE_MESSAGE)


def _open_content_db(svc: ServiceState) -> Optional[sqlite3.Connection]:
    """Read-only connection to the shared content tables. None when there is no usable database."""
    if svc.db_path is None:
        return None
    try:
        return connect(svc.db_path, readonly=True, load_vec=False)
    except (sqlite3.Error, FileNotFoundError):
        log.warning("guided: database could not be opened")
        return None


@router.post("/guided/start", response_model=GuidedStep)
def guided_start(body: GuidedStartRequest, request: Request) -> GuidedStep:
    svc = _svc(request)
    session = svc.guided.start(body.equipment_model_id, body.symptom_id, body.complaint_text)
    conn = _open_content_db(svc)
    if conn is None:
        return _no_tree_step(session.session_id)
    try:
        found = tb.start_tree(conn, body.equipment_model_id, body.symptom_id, body.complaint_text)
        if found is None:
            return _no_tree_step(session.session_id)
        tree, step = found
        svc.guided.attach(session.session_id, tree["id"], step["id"])
        return tb.resolve_step(conn, session.session_id, step)
    except sqlite3.Error:
        log.warning("guided start: database read failed")
        return _no_tree_step(session.session_id)
    finally:
        conn.close()


@router.post("/guided/next", response_model=GuidedStep)
def guided_next(body: GuidedNextRequest, request: Request) -> GuidedStep:
    svc = _svc(request)
    session = svc.guided.get(body.session_id)
    if session is None:
        raise ApiError(404, "not_found", "Unknown guided session.")
    if body.reading_value is not None:
        # The service canonicalizes and cross-checks only. The app persists the reading (ADR-009 §3).
        if body.reading_unit is None:
            raise ApiError(422, "validation_error", "reading_unit is required with reading_value.")
        if not math.isfinite(body.reading_value) or not units.is_known_unit(body.reading_unit):
            raise ApiError(422, "validation_error", "Reading value or unit is invalid.")
    if session.tree_id is None:
        return _no_tree_step(session.session_id)
    if body.step_id is not None and body.step_id != session.current_step_id:
        raise ApiError(409, "conflict", "step_id is not the current step of this guided session.")
    conn = _open_content_db(svc)
    if conn is None:
        return _no_tree_step(session.session_id)
    try:
        current = tb.load_step(conn, session.tree_id, session.current_step_id or "")
        if current is None:
            raise ApiError(409, "conflict", "The current step is no longer in the tree. Start the test again.")
        note = None
        if body.reading_value is not None and body.reading_unit is not None:
            note = tb.reading_note(conn, current, body.reading_value, body.reading_unit, body.verdict)
        nxt = tb.advance(conn, session, current["id"], body.verdict)
        svc.guided.attach(session.session_id, nxt["tree_id"], nxt["id"])
        return tb.resolve_step(conn, session.session_id, nxt, note=note)
    except tb.TreeIntegrityError as exc:
        log.warning("guided next: troubleshooting content is inconsistent")
        raise ApiError(500, "internal", "The troubleshooting content is inconsistent. Check the content package.") from exc
    except sqlite3.Error:
        log.warning("guided next: database read failed")
        return _no_tree_step(session.session_id)
    finally:
        conn.close()
