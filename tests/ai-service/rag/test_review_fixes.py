"""Regression tests for the final review: config path fallback, models.chat, path guards, size cap, load_vec."""

from __future__ import annotations

from pathlib import Path

import pytest

from conftest import MAINT_600I, require
from ingest import pipeline
from ingest.pipeline import expand_paths, ingest_files
from rag import config as cfg_mod
from rag.config import load_config
from rag.db import connect, is_vec0_available


def test_default_config_path_falls_back_to_app_location(tmp_path, monkeypatch):
    missing_config = tmp_path / "config" / "config.yaml"
    app_config = tmp_path / "app" / "config.yaml"
    app_config.parent.mkdir(parents=True)
    app_config.write_text("models:\n  chat: from-app-location\n", encoding="utf-8")
    monkeypatch.setattr(cfg_mod, "_CONFIG_CANDIDATES", (missing_config, app_config))
    assert cfg_mod.default_config_path() == app_config
    assert load_config().models.chat == "from-app-location"


def test_default_config_path_prefers_phase8_location(tmp_path, monkeypatch):
    first = tmp_path / "config" / "config.yaml"
    second = tmp_path / "app" / "config.yaml"
    first.parent.mkdir(parents=True)
    second.parent.mkdir(parents=True)
    first.write_text("models:\n  chat: phase8\n", encoding="utf-8")
    second.write_text("models:\n  chat: app\n", encoding="utf-8")
    monkeypatch.setattr(cfg_mod, "_CONFIG_CANDIDATES", (first, second))
    assert load_config().models.chat == "phase8"


def test_empty_models_chat_is_rejected():
    with pytest.raises(ValueError, match="models.chat must be set"):
        load_config(overrides={"models.chat": "   "})


def test_unc_and_drive_root_are_rejected(tmp_path):
    root = Path.cwd().anchor  # e.g. "C:\\" or "/"
    pdfs, problems = expand_paths(["\\\\fileserver\\share\\manual.pdf", "//fileserver/share", root])
    assert pdfs == []
    reasons = {reason for _, reason in problems}
    assert any("UNC" in r for r in reasons)
    assert any("root" in r for r in reasons)


def test_depth_limit_stops_deep_folders(tmp_path):
    shallow = tmp_path / "a" / "b"
    shallow.mkdir(parents=True)
    (shallow / "shallow.pdf").write_bytes(b"%PDF-1.4")
    deep = tmp_path
    for i in range(pipeline.MAX_DEPTH + 2):
        deep = deep / f"lvl{i}"
    deep.mkdir(parents=True)
    (deep / "deep.pdf").write_bytes(b"%PDF-1.4")
    pdfs, _ = expand_paths([tmp_path])
    names = {p.name for p in pdfs}
    assert "shallow.pdf" in names
    assert "deep.pdf" not in names


def test_file_count_limit(tmp_path, monkeypatch):
    monkeypatch.setattr(pipeline, "MAX_FILES", 3)
    for i in range(6):
        (tmp_path / f"m{i}.pdf").write_bytes(b"%PDF-1.4")
    pdfs, problems = expand_paths([tmp_path])
    assert len(pdfs) == 3
    assert any("more than 3 files" in msg for _, msg in problems)


def test_oversized_pdf_is_rejected_before_reading(conn, offline_config, monkeypatch):
    src = require(MAINT_600I)
    monkeypatch.setattr(pipeline, "MAX_PDF_BYTES", 1024)
    job = ingest_files([src], conn=conn, config=offline_config)
    assert job.chunks_created == 0
    assert any("file too large" in e.message for e in job.errors)
    assert conn.execute("SELECT count(*) FROM manuals").fetchone()[0] == 0


def test_connect_without_vec_reports_no_vec0(db_path):
    c = connect(db_path, load_vec=False)
    try:
        assert is_vec0_available(c) is False  # table may exist, but this connection cannot query it
    finally:
        c.close()


def test_connect_default_still_loads_vec(db_path):
    c = connect(db_path)
    try:
        assert is_vec0_available(c) is True
    finally:
        c.close()
