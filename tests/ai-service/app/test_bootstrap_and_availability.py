"""Schema safety net (app/bootstrap.py) and the availability/degradation rules (app/runtime.py)."""

from __future__ import annotations

import sqlite3

import pytest

from app.bootstrap import MIGRATIONS, _strip_vec0, ensure_schema
from app.runtime import Availability, CachedProbe, RuntimeProbe
from app.perf import rss_mb
from app.security import token_matches
from rag.db import connect, is_vec0_available


def _versions(path) -> list[int]:
    conn = sqlite3.connect(str(path))
    try:
        return [r[0] for r in conn.execute("SELECT version FROM schema_migrations ORDER BY version")]
    finally:
        conn.close()


def test_fresh_database_gets_every_migration_recorded(tmp_path):
    db = tmp_path / "fresh.db"
    report = ensure_schema(db)
    assert report.applied == [v for v, _ in MIGRATIONS]
    assert _versions(db) == [1, 2, 3, 4, 5]  # 004: ADR-009 safety_warnings + readings.is_simulated
    conn = connect(db, readonly=True)
    try:
        tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        assert {"manuals", "manual_chunks", "manual_pages", "chunk_tags", "work_orders"} <= tables
    finally:
        conn.close()


def test_second_run_is_a_no_op(tmp_path):
    db = tmp_path / "again.db"
    ensure_schema(db)
    report = ensure_schema(db)
    assert report.applied == []
    assert _versions(db) == [1, 2, 3, 4, 5]


def test_missing_migration_folder_is_a_clear_error(tmp_path):
    with pytest.raises(FileNotFoundError, match="migration file missing"):
        ensure_schema(tmp_path / "x.db", tmp_path / "no-such-folder")


def test_vec0_statement_is_stripped_when_vectors_are_unavailable():
    sql = (
        "-- header\nCREATE INDEX IF NOT EXISTS i ON manuals(file_hash);\n"
        "CREATE VIRTUAL TABLE IF NOT EXISTS embeddings USING vec0(\n  chunk_id INTEGER PRIMARY KEY,\n"
        "  embedding float[768]\n);\n"
    )
    stripped = _strip_vec0(sql)
    assert "vec0" not in stripped.lower()
    assert "CREATE INDEX" in stripped


def test_vec0_table_exists_only_when_sqlite_vec_loads(tmp_path):
    db = tmp_path / "vec.db"
    ensure_schema(db)
    conn = connect(db, readonly=True)
    try:
        has_embeddings = conn.execute(
            "SELECT 1 FROM sqlite_master WHERE name = 'embeddings'"
        ).fetchone() is not None
        assert has_embeddings == is_vec0_available(conn)
    finally:
        conn.close()


def _avail(reachable=True, chat=True, loading=False, embed=True, db=True):
    return Availability(model_runtime_reachable=reachable, chat_model_present=chat, chat_loading=loading,
                        embedding_model_present=embed, db_ok=db)


def test_availability_reason_priority():
    down = _avail(reachable=False, embed=False)
    assert down.degradation_reason == "model_runtime_unreachable"
    assert down.status == "degraded" and down.chat_available is False

    no_db = _avail(reachable=False, chat=False, embed=False, db=False)
    assert no_db.degradation_reason == "db_unavailable"
    assert no_db.search_available is False

    no_chat = _avail(chat=False)
    assert no_chat.degradation_reason == "chat_model_missing"
    assert no_chat.chat_available is False

    loading = _avail(loading=True)
    assert loading.degradation_reason == "model_loading"
    assert loading.status == "starting" and loading.chat_available is False

    no_embed = _avail(embed=False)
    assert no_embed.degradation_reason == "embedding_model_missing"
    assert no_embed.chat_available is True

    ok = _avail()
    assert ok.degradation_reason is None and ok.status == "ok"


def test_probe_caches_within_ttl_and_refreshes_after():
    calls = []
    now = {"t": 0.0}

    def probe(_base):
        calls.append(1)
        return RuntimeProbe("ready")

    monitor = CachedProbe("http://x", probe_fn=probe, ttl_s=5.0, clock=lambda: now["t"])
    monitor.probe()
    monitor.probe()
    assert len(calls) == 1
    now["t"] = 6.0
    monitor.probe()
    assert len(calls) == 2
    monitor.probe(fresh=True)
    assert len(calls) == 3


def test_rss_is_a_number_or_none():
    value = rss_mb()
    assert value is None or value > 0


def test_token_comparison_helper_rejects_blank_expected():
    assert token_matches("x", "") is False
