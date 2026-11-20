"""Chat backends: LiteLLM -> llama-server on the tablet, or a remote route (ADR-008, ADR-012, ADR-013).

- Model names come only from config (`models.chat`, `routes.*.model`). The only literal here is the LiteLLM
  provider route `openai/` used to reach llama-server's OpenAI-compatible API (a provider, not a model).
- Remote keys are passed to LiteLLM per call from Windows Credential Manager and are never logged.
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
    litellm.suppress_debug_info = True  # no "Give Feedback" banners on stdout
    return litellm

PROVIDER_ROUTE = "openai"  # LiteLLM route for llama-server's OpenAI-compatible /v1/chat/completions
LOCAL_NO_KEY = "sk-local-no-key"  # llama-server runs without --api-key; LiteLLM's openai route still wants a value
CHAT_TIMEOUT_S = 120.0  # CPU-only 2B model: a long first load can be slow
CHAT_TEMPERATURE = 0.0  # default only; the value used comes from models.chat_temperature
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


class LiteLLMChatBackend:
    """One LiteLLM route. `api_key_fn` is called per request so a key rotated in Credential Manager is picked up."""

    def __init__(
        self,
        *,
        model: str,
        label: str,
        api_base: Optional[str] = None,
        api_key_fn: Optional[Callable[[], Optional[str]]] = None,
        timeout_s: float = CHAT_TIMEOUT_S,
        unreachable_detail: str = "The model route is not reachable.",
        temperature: float = CHAT_TEMPERATURE,
    ) -> None:
        self._model = model
        self._temperature = temperature
        self.label = label
        self._api_base = api_base
        self._api_key_fn = api_key_fn
        self._timeout = timeout_s
        self._unreachable_detail = unreachable_detail

    @property
    def chat_model(self) -> str:
        return self.label

    def preload(self) -> None:
        """Import LiteLLM now (about 6 s on the tablet) so the first chat does not pay for it."""
        _import_litellm()

    def complete(self, messages: list[dict[str, Any]], tools: Optional[list[dict[str, Any]]]) -> AssistantTurn:
        litellm = _import_litellm()  # heavy import: already preloaded at startup

        kwargs: dict[str, Any] = {
            "model": self._model,
            "messages": messages,
            "stream": True,
            "timeout": self._timeout,
            "temperature": self._temperature,
            "max_tokens": CHAT_MAX_TOKENS,
            "max_retries": 0,  # fall back to the next route at once instead of retrying a dead one
        }
        if self._api_base:
            kwargs["api_base"] = self._api_base
        if self._api_key_fn is not None:
            key = self._api_key_fn()
            if not key:
                raise ChatUnavailable("ai_unavailable", "No key for this route in Credential Manager.")
            kwargs["api_key"] = key
        if tools:
            kwargs["tools"] = tools
        started = time.perf_counter()
        try:
            stream = litellm.completion(**kwargs)
            return _collect(stream, started)
        except ChatUnavailable:
            raise
        except Exception as exc:  # map every provider failure to a typed, honest code
            raise _map_error(exc, self._unreachable_detail) from exc

    def unload(self) -> None:
        """Remote routes hold no tablet RAM."""


class LlamaCppChatBackend(LiteLLMChatBackend):
    """The tablet route: llama-server on 127.0.0.1 (ADR-012). `unload` stops the supervised processes."""

    def __init__(self, config: Config, runtime: Any = None, *, timeout_s: float = CHAT_TIMEOUT_S) -> None:
        super().__init__(
            model=f"{PROVIDER_ROUTE}/{config.models.chat}",
            label=config.models.chat,
            api_base=f"{config.llama_server.base_url}/v1",
            api_key_fn=lambda: LOCAL_NO_KEY,
            timeout_s=timeout_s,
            unreachable_detail="The local model runtime (llama-server) is not reachable.",
            temperature=config.models.chat_temperature,
        )
        self._runtime = runtime

    def unload(self) -> None:
        """Stop both llama-server children to give the RAM back (idle unload). Best effort."""
        if self._runtime is not None:
            self._runtime.stop_all()


def remote_backend(config: Config, route: str, key_fn: Callable[[], Optional[str]]) -> LiteLLMChatBackend:
    """Backend for the shop-server or cloud route (ADR-013). Model, address and key name come from config."""
    rc = config.routes.shop_server if route == "shop-server" else config.routes.cloud
    return LiteLLMChatBackend(
        model=rc.model.strip(),
        label=rc.model.strip(),
        api_base=rc.api_base.strip() or None,
        api_key_fn=key_fn if rc.key_name.strip() else None,
        timeout_s=CHAT_TIMEOUT_S,
        unreachable_detail=f"The {route} route is not reachable.",
        temperature=config.models.chat_temperature,
    )


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


def _cause_chain(exc: BaseException, limit: int = 8) -> list[BaseException]:
    """The exception and what it wraps. LiteLLM reports a refused connection as InternalServerError around a
    ConnectError, so the outer type alone cannot be trusted."""
    chain: list[BaseException] = []
    cur: Optional[BaseException] = exc
    while cur is not None and len(chain) < limit and cur not in chain:
        chain.append(cur)
        cur = cur.__cause__ or cur.__context__
    return chain


def _map_error(exc: BaseException, unreachable_detail: str = "The model route is not reachable.") -> ChatUnavailable:
    name = type(exc).__name__
    chain = _cause_chain(exc)
    if any("Timeout" in type(e).__name__ for e in chain):
        # Either still loading into RAM or too slow: the App shows "starting" and retries.
        return ChatUnavailable("ai_starting", "The chat model did not respond in time (still loading?).")
    if any("Connect" in type(e).__name__ or isinstance(e, (httpx.HTTPError, OSError)) for e in chain):
        return ChatUnavailable("ai_unavailable", unreachable_detail)
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
