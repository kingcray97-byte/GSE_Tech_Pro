"""Ollama reachability and model presence (ADR-008 degradation rules).

Probes are short-timeout HTTP GETs against the configured Ollama host (127.0.0.1:11434 by default).
Results are cached for a few seconds so /health polling does not hammer Ollama. Nothing here
fabricates availability: an unreachable Ollama is reported as unreachable.
"""

from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass
from typing import Callable, Optional

import httpx

from rag.config import Config

log = logging.getLogger("gse.ai.ollama")

PROBE_TIMEOUT_S = 2.0
CACHE_TTL_S = 5.0


@dataclass(frozen=True)
class OllamaProbe:
    reachable: bool
    model_names: tuple[str, ...] = ()
    error: Optional[str] = None  # short reason; never includes response bodies

    def has_model(self, wanted: str) -> bool:
        return any(names_match(wanted, name) for name in self.model_names)


@dataclass(frozen=True)
class Availability:
    """What the service can do right now. Pure data so the degradation rules are unit-testable."""

    ollama_reachable: bool
    chat_model_present: bool
    embedding_model_present: bool
    db_ok: bool

    @property
    def chat_available(self) -> bool:
        return self.ollama_reachable and self.chat_model_present

    @property
    def search_available(self) -> bool:
        return self.db_ok

    @property
    def degradation_reason(self) -> Optional[str]:
        # Priority: the most fundamental missing piece is reported first.
        if not self.db_ok:
            return "db_unavailable"
        if not self.ollama_reachable:
            return "ollama_unreachable"
        if not self.chat_model_present:
            return "chat_model_missing"
        if not self.embedding_model_present:
            return "embedding_model_missing"
        return None

    @property
    def status(self) -> str:
        return "ok" if self.degradation_reason is None else "degraded"


def names_match(wanted: str, present: str) -> bool:
    """`nomic-embed-text` matches `nomic-embed-text:latest`; a tagged name must match exactly."""
    if ":" in wanted:
        return present == wanted or present == f"{wanted}:latest"
    return present.split(":")[0] == wanted


def probe_now(base_url: str) -> OllamaProbe:
    try:
        with httpx.Client(timeout=PROBE_TIMEOUT_S) as client:
            version = client.get(f"{base_url}/api/version")
            version.raise_for_status()
            tags = client.get(f"{base_url}/api/tags")
            tags.raise_for_status()
            names = tuple(
                str(m.get("name", "")) for m in tags.json().get("models", []) if isinstance(m, dict)
            )
    except httpx.TimeoutException:
        return OllamaProbe(False, error="timeout")
    except httpx.HTTPError:
        return OllamaProbe(False, error="connection_failed")
    except (ValueError, AttributeError):
        return OllamaProbe(False, error="bad_response")
    return OllamaProbe(True, model_names=names)


class OllamaMonitor:
    """Cached probe of one Ollama endpoint. Thread-safe; `probe_fn` is injectable for tests."""

    def __init__(
        self,
        base_url: str,
        *,
        probe_fn: Callable[[str], OllamaProbe] = probe_now,
        ttl_s: float = CACHE_TTL_S,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._base_url = base_url
        self._probe_fn = probe_fn
        self._ttl = ttl_s
        self._clock = clock
        self._lock = threading.Lock()
        self._cached: Optional[tuple[float, OllamaProbe]] = None

    @property
    def base_url(self) -> str:
        return self._base_url

    def probe(self, *, fresh: bool = False) -> OllamaProbe:
        with self._lock:
            now = self._clock()
            if not fresh and self._cached is not None and now - self._cached[0] < self._ttl:
                return self._cached[1]
        result = self._probe_fn(self._base_url)  # network I/O outside the lock
        with self._lock:
            self._cached = (self._clock(), result)
        return result

    def invalidate(self) -> None:
        with self._lock:
            self._cached = None


def model_names_available(config: Config, probe: OllamaProbe) -> tuple[bool, bool]:
    """(chat model present, embedding model present) for the configured names."""
    if not probe.reachable:
        return False, False
    return probe.has_model(config.models.chat), probe.has_model(config.models.embedding)
