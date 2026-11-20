"""Pure unit tests: RRF merge, FTS query escaping, config loader, DB helpers."""

from __future__ import annotations

import sqlite3

import pytest

from rag.config import Config, load_config
from rag.db import connect, default_db_path, is_vec0_available
from rag.rrf import rrf_merge
from rag.search import build_fts_match


# ------------------------------------------------------------------ RRF (pure)


def test_rrf_single_list_keeps_order():
    merged = rrf_merge([["a", "b", "c"]])
    assert [item for item, _ in merged] == ["a", "b", "c"]


def test_rrf_item_in_both_lists_beats_single_list_item():
    # 'x' is rank 2 in both lists; 'y' is rank 1 in one list only.
    merged = dict(rrf_merge([["y", "x"], ["z", "x"]]))
    assert merged["x"] > merged["y"]
    assert merged["x"] == pytest.approx(1 / 62 + 1 / 62)
    assert merged["y"] == pytest.approx(1 / 61)


def test_rrf_exact_scores_and_k():
    merged = dict(rrf_merge([["a", "b"], ["b", "a"]], k=60))
    assert merged["a"] == pytest.approx(1 / 61 + 1 / 62)
    assert merged["b"] == pytest.approx(1 / 62 + 1 / 61)
    smaller_k = dict(rrf_merge([["a"]], k=1))
    assert smaller_k["a"] == pytest.approx(1 / 2)


def test_rrf_duplicates_within_a_list_count_once_and_ties_are_stable():
    merged = rrf_merge([["a", "a", "b"]])
    assert merged[0][0] == "a" and merged[0][1] == pytest.approx(1 / 61)
    tie = rrf_merge([["p"], ["q"]])
    assert [item for item, _ in tie] == ["p", "q"]  # equal scores keep first-seen order


def test_rrf_rejects_non_positive_k():
    with pytest.raises(ValueError):
        rrf_merge([["a"]], k=0)


# ------------------------------------------------------------------ FTS5 escaping


@pytest.mark.parametrize(
    "query",
    [
        "660-28 torque?",
        "What's the (17 Nm) torque on the 6 mm nuts?",
        'unbalanced "quote',
        "AND OR NOT NEAR(a b) *",
        "col:value ^boost -exclude",
    ],
)
def test_fts_match_contains_only_quoted_tokens(query):
    match = build_fts_match(query)
    assert match is not None
    # Every term is a double-quoted bare word; no operators or punctuation leak through.
    for piece in match.split(" OR "):
        assert piece.startswith('"') and piece.endswith('"'), piece
        inner = piece[1:-1]
        assert inner and all(ch.isalnum() or ch == "_" for ch in inner), piece


def test_fts_match_none_for_punctuation_only():
    assert build_fts_match("?!.. --") is None


def test_real_fts5_accepts_escaped_hostile_queries(conn):
    # Run the escaped expression through real FTS5 on an empty table: must not raise a syntax error.
    for query in ['660-28 "unbalanced', "NEAR(a b) AND", "what?", "a:b (c"]:
        match = build_fts_match(query)
        conn.execute("SELECT count(*) FROM manual_chunks_fts WHERE manual_chunks_fts MATCH ?", (match,)).fetchone()


# ------------------------------------------------------------------ config


def test_defaults_match_the_contract(tmp_path):
    # Assert the built-in dataclass defaults, not a loaded file: ai-service/app/config.yaml (owned by
    # ai-engineer) may legitimately override them at runtime.
    from rag.config import Config as _Config

    cfg = _Config()
    assert cfg.service.host == "127.0.0.1"
    assert cfg.service.port == 8765
    assert cfg.llama_server.binary == ""  # attach mode unless the installer fills it in
    assert cfg.llama_server.base_url == "http://127.0.0.1:8081"
    assert cfg.llama_server.embedding.base_url == "http://127.0.0.1:8082"
    assert cfg.llama_server.embedding.pooling == "mean"
    assert cfg.routes.shop_server.enabled is False
    assert cfg.routes.cloud.enabled is False  # cloud is off by default (ADR-013)
    assert cfg.models.chat == "lfm3-omni"
    assert cfg.models.embedding == "nomic-embed-text"
    assert cfg.models.embedding_dim == 768
    assert cfg.idle_unload_minutes == 10
    assert cfg.degradation.fallback_search == "bm25"
    assert "Manual search remains available" in cfg.degradation.offline_chat_message


def test_overrides_port_and_token_and_token_is_not_printed(tmp_path):
    cfg = load_config(overrides={"port": 9001, "token": "s3cret-token", "host": None})
    assert cfg.service.port == 9001
    assert cfg.service.token == "s3cret-token"
    assert "s3cret-token" not in repr(cfg)


def test_explicit_yaml_file_is_merged(tmp_path):
    path = tmp_path / "config.yaml"
    path.write_text(
        "service:\n  port: 8800\nmodels:\n  chat: test-chat\nidle_unload_minutes: 3\n", encoding="utf-8"
    )
    cfg = load_config(path=path, overrides={"port": 8801})
    assert cfg.service.port == 8801  # override beats file
    assert cfg.models.chat == "test-chat"
    assert cfg.idle_unload_minutes == 3


def test_explicit_missing_config_is_an_error(tmp_path):
    with pytest.raises(FileNotFoundError):
        load_config(path=tmp_path / "nope.yaml")


@pytest.mark.parametrize(
    "overrides",
    [
        {"host": "0.0.0.0"},
        {"port": 0},
        {"port": 70000},
        {"models.embedding_dim": 1024},
        {"nonexistent.key": 1},
    ],
)
def test_invalid_config_is_rejected(overrides):
    with pytest.raises(ValueError):
        load_config(overrides=overrides)


def test_unknown_yaml_section_is_rejected(tmp_path):
    path = tmp_path / "c.yaml"
    path.write_text("bogus:\n  a: 1\n", encoding="utf-8")
    with pytest.raises(ValueError):
        load_config(path=path)


# ------------------------------------------------------------------ db


def test_default_db_path_matches_app_location(monkeypatch):
    monkeypatch.delenv("GSE_TECH_PRO_DB", raising=False)
    monkeypatch.setenv("LOCALAPPDATA", r"C:\Users\tech\AppData\Local")
    assert str(default_db_path()).replace("/", "\\") == r"C:\Users\tech\AppData\Local\GseTechPro\gse-tech-pro.db"


def test_rw_connection_is_wal(db_path):
    c = connect(db_path)
    try:
        assert c.execute("PRAGMA journal_mode").fetchone()[0].lower() == "wal"
        assert c.execute("PRAGMA foreign_keys").fetchone()[0] == 1
    finally:
        c.close()


def test_readonly_connection_cannot_write(db_path):
    c = connect(db_path, readonly=True)
    try:
        with pytest.raises(sqlite3.OperationalError):
            c.execute("INSERT INTO app_settings (key, value_json, updated_at) VALUES ('x','1','t')")
    finally:
        c.close()


def test_vec0_probe_matches_migrations(conn):
    # Our fixture applies 003 only when vec loads; the probe must agree with what the schema has.
    has_table = conn.execute(
        "SELECT count(*) FROM sqlite_master WHERE name = 'embeddings'"
    ).fetchone()[0]
    assert is_vec0_available(conn) == bool(has_table)


def test_token_default_is_hidden_from_repr():
    assert Config().service.token == ""
    assert "token=" not in repr(Config().service)
