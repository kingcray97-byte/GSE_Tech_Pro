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
OFFLINE_OLLAMA = "127.0.0.1:9"  # closed port: every probe fails fast and deterministically
TEST_TOKEN = "test-token-0123456789abcdef"


AUTH = {"X-GSE-Token": TEST_TOKEN}


def fake_probe(models: tuple[str, ...] = (), reachable: bool = True):
    """A probe that never touches the network. `models` are the names Ollama would list."""
    from app.ollama import OllamaProbe

    def _probe(_base_url: str) -> OllamaProbe:
        if not reachable:
            return OllamaProbe(False, error="connection_failed")
        return OllamaProbe(True, model_names=tuple(models))

    return _probe


def blocking_probe(gate):
    """A probe that waits on `gate` (a threading.Event), so tests can observe the 'starting' window."""
    from app.ollama import OllamaProbe

    def _probe(_base_url: str) -> OllamaProbe:
        gate.wait(10)
        return OllamaProbe(False, error="connection_failed")

    return _probe


def offline_config(**overrides):
    """The real app/config.yaml with Ollama pointed at a closed port and idle unload disabled."""
    from rag.config import load_config

    values = {"ollama.host": OFFLINE_OLLAMA, "idle_unload_minutes": 0}
    values.update(overrides)
    return load_config(APP_CONFIG, overrides=values)
