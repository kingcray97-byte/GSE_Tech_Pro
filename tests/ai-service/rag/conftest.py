"""Shared fixtures for rag-ingest tests.

- Puts ai-service/ on sys.path so `import rag` / `import ingest` work without installing the package.
- Builds a temp database by running the REAL migrations (001_Initial.sql, then 003_ManualsSearch.sql when
  sqlite-vec is loadable). If vec is unavailable only 001 is applied and vector tests are skipped.
- `hashing_test_embedder` is a TEST DOUBLE that exists only to exercise the vec0 write/KNN code path. It is
  never imported by production code and is never used for the fallback/no-Ollama assertions.
"""

from __future__ import annotations

import hashlib
import math
import re
import sqlite3
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[3]
AI_SERVICE = REPO / "ai-service"
if str(AI_SERVICE) not in sys.path:
    sys.path.insert(0, str(AI_SERVICE))

from rag.config import load_config  # noqa: E402
from rag.db import connect, is_vec0_available  # noqa: E402

MIGRATIONS = REPO / "src" / "GseTechPro.Data" / "Migrations"
MANUALS = REPO / "docs" / "Manuals"
DELTA_GAS = MANUALS / "740-774 Delta Airlines" / "740-774 Delta Gas"
MAINT_600I = DELTA_GAS / "4  740-774 Maintenance manual 600i  (Delta).pdf"
SCHEMATICS_SCANNED = MANUALS / "How to read Schematics.pdf"
TESTSET = Path(__file__).resolve().parent / "retrieval_testset.json"


def require(path: Path) -> Path:
    if not path.exists():
        pytest.skip(f"seed manual not present: {path.name}")
    return path


def _vec_loaded(conn) -> bool:
    """sqlite-vec is loaded on this connection (independent of whether any vec0 table exists yet)."""
    try:
        conn.execute("SELECT vec_version()").fetchone()
        return True
    except sqlite3.Error:
        return False


@pytest.fixture
def db_path(tmp_path: Path) -> Path:
    path = tmp_path / "gse-tech-pro.db"
    conn = connect(path)  # loads sqlite-vec when importable
    try:
        conn.executescript((MIGRATIONS / "001_Initial.sql").read_text(encoding="utf-8"))
        if _vec_loaded(conn):
            conn.executescript((MIGRATIONS / "003_ManualsSearch.sql").read_text(encoding="utf-8"))
    finally:
        conn.close()
    return path


@pytest.fixture
def conn(db_path: Path):
    c = connect(db_path)
    yield c
    c.close()


@pytest.fixture
def vec(conn) -> bool:
    if not is_vec0_available(conn):
        pytest.skip("sqlite-vec not loadable on this interpreter")
    return True


@pytest.fixture
def offline_config():
    """Ollama pointed at a closed port: the embedding probe fails fast and deterministically."""
    return load_config(overrides={"ollama.host": "127.0.0.1:9"})


@pytest.fixture
def unavailable_embedder():
    """Stands in for 'Ollama absent' at the callable boundary: always returns None."""

    def _none(texts):
        return None

    return _none


_TOKEN = re.compile(r"[a-z0-9]+")


def hashing_test_embedder(texts: list[str]) -> list[list[float]]:
    """TEST DOUBLE ONLY: deterministic 768-d bag-of-words hash vectors. Never used in production paths."""
    out = []
    for text in texts:
        vec = [0.0] * 768
        for tok in _TOKEN.findall(text.lower()):
            h = int(hashlib.md5(tok.encode()).hexdigest(), 16)
            vec[h % 768] += 1.0
        norm = math.sqrt(sum(x * x for x in vec)) or 1.0
        out.append([x / norm for x in vec])
    return out
