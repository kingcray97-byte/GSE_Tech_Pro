"""llama.cpp runtime: health probes and the llama-server process supervisor (ADR-012).

- `llama-server` answers `GET /health` with 200 when the model is loaded and 503 while it is loading. Anything else
  (connection refused, timeout) means it is down. Probes are short and cached so /health polling costs nothing.
- `LlamaServerProcess` starts one `llama-server` child (chat or embeddings), always with `--host 127.0.0.1`, and stops
  it on idle unload or shutdown. With no `binary` configured it never spawns anything (attach mode: dev and tests).
- Nothing here fabricates availability: a server that does not answer is reported as down.
"""

from __future__ import annotations

import logging
import subprocess
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Literal, Optional

import httpx

from rag.config import Config, EmbeddingServerConfig, LlamaServerConfig

log = logging.getLogger("gse.ai.runtime")

PROBE_TIMEOUT_S = 2.0
CACHE_TTL_S = 5.0
RuntimeState = Literal["ready", "loading", "down"]


@dataclass(frozen=True)
class RuntimeProbe:
    state: RuntimeState
    error: Optional[str] = None  # short reason; never a response body

    @property
    def reachable(self) -> bool:
        return self.state != "down"

    @property
    def ready(self) -> bool:
        return self.state == "ready"


def probe_now(base_url: str) -> RuntimeProbe:
    """One llama-server health check: 200 ready, 503 loading, anything else down."""
    try:
        with httpx.Client(timeout=PROBE_TIMEOUT_S) as client:
            resp = client.get(f"{base_url}/health")
    except httpx.TimeoutException:
        return RuntimeProbe("down", error="timeout")
    except httpx.HTTPError:
        return RuntimeProbe("down", error="connection_failed")
    if resp.status_code == 200:
        return RuntimeProbe("ready")
    if resp.status_code == 503:
        return RuntimeProbe("loading")
    return RuntimeProbe("down", error=f"http_{resp.status_code}")


class CachedProbe:
    """Cached probe of one endpoint. Thread-safe; `probe_fn` is injectable for tests."""

    def __init__(
        self,
        base_url: str,
        *,
        probe_fn: Callable[[str], RuntimeProbe] = probe_now,
        ttl_s: float = CACHE_TTL_S,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._base_url = base_url
        self._probe_fn = probe_fn
        self._ttl = ttl_s
        self._clock = clock
        self._lock = threading.Lock()
        self._cached: Optional[tuple[float, RuntimeProbe]] = None

    @property
    def base_url(self) -> str:
        return self._base_url

    def probe(self, *, fresh: bool = False) -> RuntimeProbe:
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


class RuntimeMonitor:
    """Probes for the chat and the embedding llama-server."""

    def __init__(self, config: Config, *, probe_fn: Callable[[str], RuntimeProbe] = probe_now,
                 clock: Callable[[], float] = time.monotonic) -> None:
        self.chat = CachedProbe(config.llama_server.base_url, probe_fn=probe_fn, clock=clock)
        self.embedding = CachedProbe(config.llama_server.embedding.base_url, probe_fn=probe_fn, clock=clock)

    def probe_all(self, *, fresh: bool = False) -> tuple[RuntimeProbe, RuntimeProbe]:
        return self.chat.probe(fresh=fresh), self.embedding.probe(fresh=fresh)

    def invalidate(self) -> None:
        self.chat.invalidate()
        self.embedding.invalidate()


@dataclass(frozen=True)
class Availability:
    """What the tablet route can do right now. Pure data so the degradation rules are unit-testable."""

    model_runtime_reachable: bool
    chat_model_present: bool
    chat_loading: bool
    embedding_model_present: bool
    db_ok: bool

    @property
    def chat_available(self) -> bool:
        return self.model_runtime_reachable and self.chat_model_present and not self.chat_loading

    @property
    def search_available(self) -> bool:
        return self.db_ok

    @property
    def degradation_reason(self) -> Optional[str]:
        # Priority: the most fundamental missing piece is reported first.
        if not self.db_ok:
            return "db_unavailable"
        if not self.chat_model_present:
            return "chat_model_missing"
        if self.chat_loading:
            return "model_loading"
        if not self.model_runtime_reachable:
            return "model_runtime_unreachable"
        if not self.embedding_model_present:
            return "embedding_model_missing"
        return None

    @property
    def status(self) -> str:
        if self.degradation_reason is None:
            return "ok"
        return "starting" if self.degradation_reason == "model_loading" else "degraded"


def chat_model_configured(ls: LlamaServerConfig) -> bool:
    """In spawn mode the chat GGUF must exist on disk. In attach mode whatever answers on the port is the model."""
    if not ls.spawns:
        return True
    return bool(ls.model.strip()) and Path(ls.model).is_file()


def embedding_model_configured(ls: LlamaServerConfig) -> bool:
    if not ls.spawns:
        return True
    return bool(ls.embedding.model.strip()) and Path(ls.embedding.model).is_file()


def availability(config: Config, chat: RuntimeProbe, embed: RuntimeProbe, db_ok: bool) -> Availability:
    ls = config.llama_server
    chat_configured = chat_model_configured(ls)
    return Availability(
        model_runtime_reachable=chat.reachable,
        chat_model_present=chat_configured,
        chat_loading=chat_configured and chat.state == "loading",
        embedding_model_present=embedding_model_configured(ls) and embed.ready,
        db_ok=db_ok,
    )


# ------------------------------------------------------------------ process supervisor


def _launcher(ls: LlamaServerConfig) -> list[str]:
    """`llama-server.exe`, or `llama.exe serve` for builds that ship one binary with subcommands."""
    return [ls.binary] + ([ls.subcommand.strip()] if ls.subcommand.strip() else [])


def chat_server_args(config: Config) -> list[str]:
    ls = config.llama_server
    args = _launcher(ls) + ["--model", ls.model, "--host", "127.0.0.1", "--port", str(ls.port),
            "--ctx-size", str(ls.ctx), "--alias", config.models.chat]
    if ls.mmproj.strip():
        args += ["--mmproj", ls.mmproj]
    if ls.threads > 0:
        args += ["--threads", str(ls.threads)]
    return args + [str(a) for a in ls.extra_args]


def embedding_server_args(config: Config) -> list[str]:
    ls = config.llama_server
    emb: EmbeddingServerConfig = ls.embedding
    args = _launcher(ls) + ["--model", emb.model, "--host", "127.0.0.1", "--port", str(emb.port),
            "--ctx-size", str(emb.ctx), "--alias", config.models.embedding, "--embedding", "--pooling", emb.pooling]
    if ls.threads > 0:
        args += ["--threads", str(ls.threads)]
    return args + [str(a) for a in emb.extra_args]


Spawner = Callable[[list[str]], "subprocess.Popen[bytes]"]


def _default_spawn(args: list[str]) -> "subprocess.Popen[bytes]":
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)  # no console window on the tablet
    return subprocess.Popen(  # noqa: S603 - args come from validated config, never from a request
        args,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=flags,
        close_fds=True,
    )


class LlamaServerProcess:
    """One supervised llama-server child. No-op when the config has no binary (attach mode) or no model file."""

    def __init__(
        self,
        name: str,
        args_fn: Callable[[], list[str]],
        model_path: str,
        probe: CachedProbe,
        *,
        enabled: bool,
        spawn: Spawner = _default_spawn,
    ) -> None:
        self.name = name
        self._args_fn = args_fn
        self._model_path = model_path
        self._probe = probe
        self._enabled = enabled
        self._spawn = spawn
        self._lock = threading.Lock()
        self._proc: Optional[subprocess.Popen[bytes]] = None

    @property
    def manages_process(self) -> bool:
        return self._enabled

    def running(self) -> bool:
        with self._lock:
            return self._proc is not None and self._proc.poll() is None

    def ensure_started(self) -> bool:
        """Start the process if it is not running. Returns True when a process is (now) running or not managed."""
        if not self._enabled:
            return True
        if not self._model_path.strip() or not Path(self._model_path).is_file():
            return False
        with self._lock:
            if self._proc is not None and self._proc.poll() is None:
                return True
            if self._proc is not None:
                log.warning("%s llama-server exited (code %s); restarting", self.name, self._proc.returncode)
            try:
                self._proc = self._spawn(self._args_fn())
            except OSError as exc:
                log.warning("%s llama-server could not start (%s)", self.name, type(exc).__name__)
                self._proc = None
                return False
        self._probe.invalidate()
        log.info("%s llama-server started", self.name)
        return True

    def wait_ready(self, timeout_s: float, *, poll_s: float = 0.5,
                   sleep: Callable[[float], None] = time.sleep,
                   clock: Callable[[], float] = time.monotonic) -> bool:
        """Wait until /health says ready (a cold model load can take a while on CPU)."""
        deadline = clock() + timeout_s
        while True:
            if self._probe.probe(fresh=True).ready:
                return True
            if self._enabled and not self.running():
                return False  # crashed while loading
            if clock() >= deadline:
                return False
            sleep(poll_s)

    def stop(self, timeout_s: float = 5.0) -> None:
        with self._lock:
            proc, self._proc = self._proc, None
        if proc is None or proc.poll() is not None:
            return
        proc.terminate()
        try:
            proc.wait(timeout=timeout_s)
        except subprocess.TimeoutExpired:
            proc.kill()
        self._probe.invalidate()
        log.info("%s llama-server stopped", self.name)


class LlamaRuntime:
    """Both llama-server children plus their probes. Built once in create_app."""

    def __init__(self, config: Config, monitor: RuntimeMonitor, *, spawn: Spawner = _default_spawn) -> None:
        ls = config.llama_server
        self.config = config
        self.monitor = monitor
        self.chat = LlamaServerProcess(
            "chat", lambda: chat_server_args(config), ls.model, monitor.chat, enabled=ls.spawns, spawn=spawn)
        self.embedding = LlamaServerProcess(
            "embedding", lambda: embedding_server_args(config), ls.embedding.model, monitor.embedding,
            enabled=ls.spawns and bool(ls.embedding.model.strip()), spawn=spawn)

    def start_all(self) -> None:
        self.chat.ensure_started()
        self.embedding.ensure_started()

    def stop_all(self) -> None:
        self.chat.stop()
        self.embedding.stop()
