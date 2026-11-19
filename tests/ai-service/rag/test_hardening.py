"""Regression tests for reviewer pass #2: non-finite vectors, all-invalid input, glyph cleanup, batching, registry cap."""

from __future__ import annotations

import math

import pytest

from conftest import hashing_test_embedder, require, MAINT_600I
from ingest import job as job_mod
from ingest.job import IngestionJob, get_job, register_job
from ingest.pdf_text import normalize_line, read_pdf
from ingest.pipeline import ingest_files
from rag.db import delete_embeddings, to_vec_blob


@pytest.mark.parametrize("bad", [math.nan, math.inf, -math.inf])
def test_to_vec_blob_rejects_non_finite(bad):
    vec = [0.0] * 768
    vec[5] = bad
    with pytest.raises(ValueError, match="non-finite"):
        to_vec_blob(vec)


def test_to_vec_blob_accepts_finite_and_packs_768_floats():
    blob = to_vec_blob([0.5] * 768)
    assert len(blob) == 768 * 4


def test_embed_texts_rejects_non_finite_from_model(monkeypatch):
    import rag.embedding as emb

    class _Resp:
        status_code = 200

        def json(self):
            return {"embeddings": [[math.nan] + [0.0] * 767]}

    monkeypatch.setattr(emb.httpx, "post", lambda *a, **k: _Resp())
    from rag.config import load_config

    with pytest.raises(ValueError, match="non-finite"):
        emb.embed_texts(["x"], "search_document", load_config(overrides={"ollama.host": "127.0.0.1:9"}))


def test_all_invalid_paths_yield_failed_job(conn, offline_config, tmp_path):
    missing = tmp_path / "does-not-exist.pdf"
    office = tmp_path / "manual.docx"
    office.write_bytes(b"not really a docx")
    job = ingest_files([missing, office], conn=conn, config=offline_config)
    assert job.state == "failed"
    assert job.files_total == 0
    assert len(job.errors) == 2


def test_empty_request_is_not_a_failure(conn, offline_config):
    job = ingest_files([], conn=conn, config=offline_config)
    assert job.state == "succeeded"


def test_normalize_line_cleans_glyphs_without_touching_numbers():
    assert normalize_line("Set to 25\u00baC") == "Set to 25\u00b0C"  # adjacent only; spaced forms are left alone
    assert normalize_line("range 20\u201325 mm") == "range 20-25 mm"
    assert normalize_line("\u201cService\u201d mode\ufffd") == '"Service" mode'
    assert normalize_line("Adjust gap 0.8 Inch") == "Adjust gap 0.8 Inch"  # numbers unchanged
    assert "\ufffd" not in normalize_line("bad\ufffdglyph")


def test_real_600i_has_no_replacement_chars_after_read():
    doc = read_pdf(require(MAINT_600I))
    assert all("\ufffd" not in line for p in doc.pages for line in p.lines)


def test_delete_embeddings_batches_and_is_idempotent(conn, vec):
    # Two chunk rows are not needed: vec0 accepts ids that do not exist; we only check the call shape.
    vec_rows = [0, 1, 2]
    for cid in vec_rows:
        conn.execute("INSERT INTO embeddings (chunk_id, embedding) VALUES (?, ?)", (cid, to_vec_blob([0.1] * 768)))
    assert delete_embeddings(conn, vec_rows + list(range(3, 1203))) == 1203
    assert conn.execute("SELECT count(*) FROM embeddings").fetchone()[0] == 0
    assert delete_embeddings(conn, []) == 0


def test_job_registry_is_capped(monkeypatch):
    monkeypatch.setattr(job_mod, "MAX_REGISTRY", 5)
    jobs = [IngestionJob() for _ in range(8)]
    for j in jobs:
        register_job(j)
    assert len(job_mod._REGISTRY) == 5
    assert get_job(jobs[-1].job_id) is jobs[-1]
    with pytest.raises(KeyError):
        get_job(jobs[0].job_id)
