"""Runtime state shared by the routes. One object, built once in create_app, so /health and /chat always agree."""

from __future__ import annotations

import logging
import sqlite3
import threading
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from app.guided import GuidedStore
from app.llm import ChatBackend, IdleUnloader, remote_backend
from app.routing import CLOUD, SHOP, RouteHealth, RouteStatus, TABLET
from app.runtime import Availability, LlamaRuntime, RuntimeMonitor, availability
from rag.config import Config
from rag.db import connect
from rag.embedding import embed_texts
from tools.manuals import no_vectors
from tools.registry import ToolContext, ToolRegistry

log = logging.getLogger("gse.ai.state")
UNLOAD_POLL_S = 30.0


@dataclass
class ServiceState:
    config: Config
    db_path: Optional[Path]
    monitor: RuntimeMonitor
    runtime: LlamaRuntime
    backend: ChatBackend  # the tablet route
    registry: ToolRegistry
    system_prompt: str
    route_health: RouteHealth
    remote_backends: dict[str, Any] = field(default_factory=dict)  # tests inject; else built from config
    guided: GuidedStore = field(default_factory=GuidedStore)
    unloader: Optional[IdleUnloader] = None
    warm: threading.Event = field(default_factory=threading.Event)
    chat_lock: threading.Lock = field(default_factory=threading.Lock)
    stop: threading.Event = field(default_factory=threading.Event)
    schema_error: Optional[str] = None

    def db_ok(self) -> bool:
        """True when the database file opens read-only and has the manuals table."""
        if self.db_path is None or not Path(self.db_path).is_file():
            return False
        conn: Optional[sqlite3.Connection] = None
        try:
            conn = connect(self.db_path, readonly=True)
            conn.execute("SELECT 1 FROM manuals LIMIT 1").fetchall()
            return True
        except (sqlite3.Error, FileNotFoundError):
            return False
        finally:
            if conn is not None:
                conn.close()

    def availability(self, db_ok: bool) -> Availability:
        chat, embed = self.monitor.probe_all()
        return availability(self.config, chat, embed, db_ok)

    def route_statuses(self, tablet: Availability) -> list[RouteStatus]:
        if tablet.chat_available:
            tablet_msg = "Ready"
        elif tablet.chat_loading:
            tablet_msg = "Loading the model"
        elif not tablet.chat_model_present:
            tablet_msg = "Chat model not installed"
        else:
            tablet_msg = "Local model not running"
        return [
            RouteStatus(TABLET, True, tablet.chat_available, self.config.models.chat, tablet_msg),
            self.route_health.status(SHOP),
            self.route_health.status(CLOUD),
        ]

    def backend_for(self, route: str) -> Any:
        if route == TABLET:
            return self.backend
        if route not in self.remote_backends:
            self.remote_backends[route] = remote_backend(self.config, route, lambda: self.route_health.key_for(route))
        return self.remote_backends[route]

    def ensure_tablet_ready(self) -> Optional[str]:
        """Start the chat llama-server if idle unload stopped it, and wait for the model. None = ready, else a code."""
        if self.monitor.chat.probe().ready:
            return None
        if self.runtime.chat.manages_process:
            if not self.runtime.chat.ensure_started():
                return "chat_model_missing"
            if self.runtime.chat.wait_ready(float(self.config.llama_server.start_timeout_s)):
                return None
            return "model_loading" if self.runtime.chat.running() else "model_runtime_unreachable"
        probe = self.monitor.chat.probe(fresh=True)
        if probe.ready:
            return None
        return "model_loading" if probe.state == "loading" else "model_runtime_unreachable"

    def _embedding_ready(self) -> bool:
        """Embedding llama-server is up. If idle unload stopped it, start it now and use BM25 until it has loaded."""
        if self.monitor.embedding.probe().ready:
            return True
        if self.runtime.embedding.manages_process and not self.runtime.embedding.running():
            self.runtime.embedding.ensure_started()
        return False

    def query_embedder(self) -> Optional[Callable[[list[str]], Optional[list[list[float]]]]]:
        """Search-side embedder, or None when the embedding server is not ready (BM25-only).

        Uses the cached probe, so a search costs no extra probe. embed_texts returns None on any error,
        and the search then falls back to BM25 for that query.
        """
        if not self._embedding_ready():
            return None
        cfg = self.config

        def _embed(texts: list[str]) -> Optional[list[list[float]]]:
            return embed_texts(texts, "search_query", cfg)

        return _embed

    def document_embedder(self) -> Callable[[list[str]], Optional[list[list[float]]]]:
        """Ingest-side embedder. A no-vector stand-in when the embedding server is not ready (chunks stored, no vectors)."""
        if not self._embedding_ready():
            return no_vectors
        cfg = self.config

        def _embed(texts: list[str]) -> Optional[list[list[float]]]:
            return embed_texts(texts, "search_document", cfg)

        return _embed

    def tool_context(self) -> ToolContext:
        return ToolContext(db_path=self.db_path, config=self.config, embedder=self.query_embedder())

    def warm_up(self) -> None:
        """Start the llama-server children and take the first probes (background thread). /health says 'starting' until done."""
        try:
            self.runtime.start_all()
            self.monitor.probe_all(fresh=True)
            preload = getattr(self.backend, "preload", None)
            if callable(preload):
                # Local-only import (cost map and telemetry are off), so it is safe even when the runtime is down.
                preload()
        except Exception:  # a broken probe or import must not wedge startup
            log.warning("initial runtime probe or chat import failed")
        finally:
            self.warm.set()

    def unload_loop(self, poll_s: float = UNLOAD_POLL_S) -> None:
        while not self.stop.wait(poll_s):
            if self.unloader is not None:
                if self.unloader.maybe_unload():
                    log.info("idle unload: llama-server processes stopped")


def idle_unloader_for(state: ServiceState, clock: Optional[Callable[[], float]] = None) -> IdleUnloader:
    if clock is None:
        return IdleUnloader(state.config.idle_unload_minutes, state.backend.unload)
    return IdleUnloader(state.config.idle_unload_minutes, state.backend.unload, clock=clock)
