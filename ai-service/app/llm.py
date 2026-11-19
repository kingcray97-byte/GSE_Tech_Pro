"""Chat backend: LiteLLM -> Ollama (ADR-008, ARCHITECTURE §4.1).

- Model names come only from config (`models.chat`, `models.embedding`). The only literal here is the
  LiteLLM provider route `ollama_chat/` (a provider, not a model).
- LiteLLM is imported lazily so /health and /config boot fast on the tablet.
- The model is called with stream=True internally so first-token latency can be measured; the HTTP
  response to the App is still one JSON ChatResponse (ADR-008).
- Offline: the LiteLLM model-cost map is read locally (no fetch from the internet), and telemetry is off.
"""

from __future__ import annotations

import logging
import os
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Optional, Protocol

import httpx

from rag.config import Config

log = logging.getLogger("gse.ai.llm")

# Must be set before litellm is imported anywhere: otherwise it fetches a model-price JSON from GitHub,
# and its telemetry may attempt outbound connections. Offline-first: both are switched off here.
os.environ.setdefault("LITELLM_LOCAL_MODEL_COST_MAP", "True")
os.environ.setdefault("LITELLM_TELEMETRY", "False")


def _import_litellm():  # noqa: ANN202 - returns the litellm module
    """Import LiteLLM (heavy, about 6 s) and force telemetry off on the module as well as the environment."""
    import litellm

    litellm.telemetry = False
    return litellm

PROVIDER_ROUTE = "ollama_chat"  # LiteLLM route for Ollama's /api/chat (tool-call aware)
CHAT_TIMEOUT_S = 120.0  # CPU-only 2B model: a long first load can be slow
CHAT_TEMPERATURE = 0.2
CHAT_MAX_TOKENS = 384  # keeps answers short on a CPU tablet


class ChatUnavailable(Exception):
    """Raised when chat cannot run now. `code` is an ADR-008 code: ai_unavailable or ai_starting."""

    def __init__(self, code: str, detail: str) -> None:
        super().__init__(detail)
        self.code = code
        self.detail = detail


@dataclass
class ToolCall:
    id: str
    name: str
    arguments: str  # raw JSON text from the model; validated by the tool registry


@dataclass
class AssistantTurn:
    text: str
    tool_calls: list[ToolCall] = field(default_factory=list)
    first_token_ms: Optional[float] = None
    total_ms: float = 0.0


class ChatBackend(Protocol):
    def complete(self, messages: list[dict[str, Any]], tools: Optional[list[dict[str, Any]]]) -> AssistantTurn:
        ...

    def unload(self) -> None:
        ...


def _first(seq: Any) -> Any:
    try:
        return seq[0] if seq else None
    except (TypeError, IndexError, KeyError):
        return None


def _get(obj: Any, name: str) -> Any:
    """LiteLLM returns objects; some providers return dicts. Read either."""
    if obj is None:
        return None
    if isinstance(obj, dict):
        return obj.get(name)
    return getattr(obj, name, None)


class OllamaChatBackend:
    def __init__(self, config: Config, *, timeout_s: float = CHAT_TIMEOUT_S) -> None:
        self._config = config
        self._timeout = timeout_s
        if config.ollama.no_cloud:
            os.environ["OLLAMA_NO_CLOUD"] = "1"

    @property
    def chat_model(self) -> str:
        return self._config.models.chat

    def preload(self) -> None:
        """Import LiteLLM now (about 6 s on the tablet) so the first chat does not pay for it."""
        _import_litellm()

    def complete(self, messages: list[dict[str, Any]], tools: Optional[list[dict[str, Any]]]) -> AssistantTurn:
        litellm = _import_litellm()  # heavy import: already preloaded at startup when Ollama is reachable

        kwargs: dict[str, Any] = {
            "model": f"{PROVIDER_ROUTE}/{self.chat_model}",
            "messages": messages,
            "api_base": self._config.ollama.base_url,
            "stream": True,
            "timeout": self._timeout,
            "temperature": CHAT_TEMPERATURE,
            "max_tokens": CHAT_MAX_TOKENS,
        }
        if tools:
            kwargs["tools"] = tools
        started = time.perf_counter()
        try:
            stream = litellm.completion(**kwargs)
            return _collect(stream, started)
        except Exception as exc:  # map every provider failure to a typed, honest code
            raise _map_error(exc) from exc

    def unload(self) -> None:
        """Ask Ollama to drop the chat and embedding models from RAM (keep_alive=0). Best effort."""
        for model in {self._config.models.chat, self._config.models.embedding}:
            try:
                httpx.post(
                    f"{self._config.ollama.base_url}/api/generate",
                    json={"model": model, "keep_alive": 0},
                    timeout=5.0,
                )
            except httpx.HTTPError:
                continue  # Ollama absent or already unloaded: nothing to free


def _collect(stream: Any, started: float) -> AssistantTurn:
    text_parts: list[str] = []
    calls: dict[int, dict[str, str]] = {}
    first_ms: Optional[float] = None
    for chunk in stream:
        choice = _first(_get(chunk, "choices"))
        delta = _get(choice, "delta")
        content = _get(delta, "content")
        if content:
            if first_ms is None:
                first_ms = (time.perf_counter() - started) * 1000.0
            text_parts.append(str(content))
        for tc in _get(delta, "tool_calls") or []:
            idx = _get(tc, "index")
            idx = int(idx) if isinstance(idx, int) else 0
            slot = calls.setdefault(idx, {"id": "", "name": "", "arguments": ""})
            if _get(tc, "id"):
                slot["id"] = str(_get(tc, "id"))
            fn = _get(tc, "function")
            if _get(fn, "name") and not slot["name"]:
                slot["name"] = str(_get(fn, "name"))
            args = _get(fn, "arguments")
            if args:
                slot["arguments"] += args if isinstance(args, str) else _json_text(args)
    tool_calls = [
        ToolCall(id=slot["id"] or f"call_{idx}", name=slot["name"], arguments=slot["arguments"] or "{}")
        for idx, slot in sorted(calls.items())
        if slot["name"]
    ]
    return AssistantTurn(
        text="".join(text_parts).strip(),
        tool_calls=tool_calls,
        first_token_ms=first_ms,
        total_ms=(time.perf_counter() - started) * 1000.0,
    )


def _json_text(value: Any) -> str:
    import json

    try:
        return json.dumps(value)
    except (TypeError, ValueError):
        return "{}"


def _map_error(exc: BaseException) -> ChatUnavailable:
    name = type(exc).__name__
    if "Timeout" in name:
        # Either still loading into RAM or too slow: the App shows "starting" and retries.
        return ChatUnavailable("ai_starting", "The chat model did not respond in time (still loading?).")
    if "Connection" in name or isinstance(exc, (httpx.HTTPError, OSError)):
        return ChatUnavailable("ai_unavailable", "Ollama is not reachable.")
    log.warning("chat backend error: %s", name)  # never log message text: it can echo prompt content
    return ChatUnavailable("ai_unavailable", f"Chat backend error ({name}).")


class IdleUnloader:
    """Asks the backend to unload models after `idle_minutes` without chat/search traffic.

    `touch()` on every chat or search. `maybe_unload()` is pure logic (clock injected) and fires once per idle
    period; the next touch re-arms it.
    """

    def __init__(
        self,
        idle_minutes: int,
        unload_fn: Callable[[], None],
        *,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._idle_s = max(0, idle_minutes) * 60.0
        self._unload = unload_fn
        self._clock = clock
        self._lock = threading.Lock()
        self._last = clock()
        self._armed = True

    def touch(self) -> None:
        with self._lock:
            self._last = self._clock()
            self._armed = True

    def maybe_unload(self) -> bool:
        with self._lock:
            if self._idle_s <= 0 or not self._armed:
                return False
            if self._clock() - self._last < self._idle_s:
                return False
            self._armed = False
        try:
            self._unload()
        except Exception:  # unloading is best effort; never crash the timer thread
            log.warning("idle unload failed (best effort)")
        return True
