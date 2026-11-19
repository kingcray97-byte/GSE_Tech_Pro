"""Runtime state shared by the routes. One object, built once in create_app, so /health and /chat always agree."""

from __future__ import annotations

import logging
import sqlite3
import threading
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

from app.guided import GuidedStore
from app.llm import ChatBackend, IdleUnloader
from app.ollama import Availability, OllamaMonitor, OllamaProbe, model_names_available
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
    monitor: OllamaMonitor
    backend: ChatBackend
    registry: ToolRegistry
    system_prompt: str
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

    def availability(self, probe: OllamaProbe, db_ok: bool) -> Availability:
        chat_present, embed_present = model_names_available(self.config, probe)
        return Availability(
            ollama_reachable=probe.reachable,
            chat_model_present=chat_present,
            embedding_model_present=embed_present,
            db_ok=db_ok,
        )

    def query_embedder(self) -> Optional[Callable[[list[str]], Optional[list[list[float]]]]]:
        """Search-side embedder, or None when the embedding model is not present (BM25-only).

        Uses the cached Ollama probe, so a search costs no extra probe. embed_texts returns None on any error,
        and the search then falls back to BM25 for that query.
        """
        _, embed_present = model_names_available(self.config, self.monitor.probe())
        if not embed_present:
            return None
        cfg = self.config

        def _embed(texts: list[str]) -> Optional[list[list[float]]]:
            return embed_texts(texts, "search_query", cfg)

        return _embed

    def document_embedder(self) -> Callable[[list[str]], Optional[list[list[float]]]]:
        """Ingest-side embedder. Returns a no-vector stand-in when the model is absent (chunks stored, no vectors)."""
        _, embed_present = model_names_available(self.config, self.monitor.probe())
        if not embed_present:
            return no_vectors
        cfg = self.config

        def _embed(texts: list[str]) -> Optional[list[list[float]]]:
            return embed_texts(texts, "search_document", cfg)

        return _embed

    def tool_context(self) -> ToolContext:
        return ToolContext(db_path=self.db_path, config=self.config, embedder=self.query_embedder())

    def warm_up(self) -> None:
        """First Ollama probe (runs on a background thread). Until it finishes, /health reports 'starting'."""
        try:
            self.monitor.probe(fresh=True)
            preload = getattr(self.backend, "preload", None)
            if callable(preload):
                # Local-only import (cost map and telemetry are off), so it is safe even when Ollama is down.
                # Paid here, not on the first technician question, including when Ollama starts later.
                preload()
        except Exception:  # a broken probe or import must not wedge startup
            log.warning("initial Ollama probe or chat import failed")
        finally:
            self.warm.set()

    def unload_loop(self, poll_s: float = UNLOAD_POLL_S) -> None:
        while not self.stop.wait(poll_s):
            if self.unloader is not None:
                if self.unloader.maybe_unload():
                    log.info("idle unload requested")


def idle_unloader_for(state: ServiceState, clock: Optional[Callable[[], float]] = None) -> IdleUnloader:
    if clock is None:
        return IdleUnloader(state.config.idle_unload_minutes, state.backend.unload)
    return IdleUnloader(state.config.idle_unload_minutes, state.backend.unload, clock=clock)
