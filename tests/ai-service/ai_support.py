"""Plain helper module shared by ai-service tests (not a conftest, so it can be imported safely).

Constants and builders only. Fixtures live in conftest.py files.
"""

from __future__ import annotations

import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
AI_SERVICE = REPO / "ai-service"
if str(AI_SERVICE) not in sys.path:
    sys.path.insert(0, str(AI_SERVICE))

MANUALS = REPO / "docs" / "Manuals"
MAINT_600I = MANUALS / "740-774 Delta Airlines" / "740-774 Delta Gas" / "4  740-774 Maintenance manual 600i  (Delta).pdf"
APP_CONFIG = AI_SERVICE / "app" / "config.yaml"
# Closed ports: every real probe fails fast and deterministically. Two ports so fakes can tell chat from embeddings.
OFFLINE_CHAT_PORT = 9
OFFLINE_EMBED_PORT = 10
TEST_TOKEN = "test-token-0123456789abcdef"


AUTH = {"X-GSE-Token": TEST_TOKEN}


def _bare(name: str) -> str:
    return name.split(":")[0]


def fake_probe(models: tuple[str, ...] = (), reachable: bool = True, loading: tuple[str, ...] = ()):
    """A llama-server health probe that never touches the network.

    `models` are the models whose llama-server is up and loaded (chat model -> the chat server on OFFLINE_CHAT_PORT,
    embedding model -> the embedding server on OFFLINE_EMBED_PORT). `loading` are servers up but still loading (503).
    Anything not listed is down (connection refused), which is what a missing llama-server looks like.
    """
    from app.runtime import RuntimeProbe

    ready = {_bare(m) for m in models}
    busy = {_bare(m) for m in loading}

    def _probe(base_url: str) -> RuntimeProbe:
        if not reachable:
            return RuntimeProbe("down", error="connection_failed")
        wanted = "lfm3-omni" if base_url.endswith(f":{OFFLINE_CHAT_PORT}") else "nomic-embed-text"
        if wanted in busy:
            return RuntimeProbe("loading")
        if wanted in ready:
            return RuntimeProbe("ready")
        return RuntimeProbe("down", error="connection_failed")

    return _probe


def blocking_probe(gate):
    """A probe that waits on `gate` (a threading.Event), so tests can observe the 'starting' window."""
    from app.runtime import RuntimeProbe

    def _probe(_base_url: str) -> RuntimeProbe:
        gate.wait(10)
        return RuntimeProbe("down", error="connection_failed")

    return _probe


def offline_config(**overrides):
    """The real app/config.yaml with llama-server pointed at closed ports (attach mode) and idle unload disabled.

    The dev config may have llama_server.binary/model filled in (it does on a machine with the pinned
    runtime), so attach mode is forced here; tests that exercise spawning override them.
    """
    from rag.config import load_config

    values = {
        "llama_server.binary": "",
        "llama_server.model": "",
        "llama_server.embedding.model": "",
        "llama_server.port": OFFLINE_CHAT_PORT,
        "llama_server.embedding.port": OFFLINE_EMBED_PORT,
        "idle_unload_minutes": 0,
    }
    values.update(overrides)
    return load_config(APP_CONFIG, overrides=values)
