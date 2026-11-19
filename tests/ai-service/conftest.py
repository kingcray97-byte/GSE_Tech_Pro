"""Shared fixtures for ai-service tests (ai-engineer).

- `seeded_db`: a temp database with the REAL migrations applied and the real 600i seed manual ingested
  with NO embeddings (embedder is a stub that returns None). Session-scoped: built once.
- `empty_db`: a migrated database with no manuals.
Helpers and constants live in ai_support.py (importable by name, unlike conftest).
"""

from __future__ import annotations

import pytest

from ai_support import MAINT_600I, offline_config  # noqa: E402  (puts ai-service/ on sys.path)


def require_seed():
    if not MAINT_600I.exists():
        pytest.skip(f"seed manual not present: {MAINT_600I.name}")
    return MAINT_600I


def _no_vectors(_texts):
    return None


@pytest.fixture(scope="session")
def seeded_db(tmp_path_factory):
    """A migrated database containing the 600i seed manual (text + FTS, no vectors)."""
    from app.bootstrap import ensure_schema
    from ingest.pipeline import ingest_files
    from rag.db import connect

    src = require_seed()
    db = tmp_path_factory.mktemp("seed") / "gse-tech-pro.db"
    ensure_schema(db)
    conn = connect(db)
    try:
        job = ingest_files([src], conn=conn, embedder=_no_vectors, config=offline_config())
        assert job.state == "succeeded", job.errors
    finally:
        conn.close()
    return db


@pytest.fixture
def empty_db(tmp_path):
    """A migrated database with no manuals."""
    from app.bootstrap import ensure_schema

    db = tmp_path / "empty.db"
    ensure_schema(db)
    return db
