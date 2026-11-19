"""Ingest + hybrid search end to end, against the real 600i maintenance PDF. No Ollama required."""

from __future__ import annotations

import hashlib
import shutil

from conftest import MAINT_600I, SCHEMATICS_SCANNED, hashing_test_embedder, require
from ingest.job import IngestionJob
from ingest.maintenance import soft_delete_chunks, soft_delete_manual
from ingest.pipeline import OCR_PENDING_MESSAGE, embed_missing, ingest_files
from rag.search import get_manual_page, hybrid_search


def _sha(path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _count(conn, sql, params=()):
    return conn.execute(sql, params).fetchone()[0]


def test_ingest_600i_without_ollama_writes_rows_and_no_vectors(conn, tmp_path, offline_config):
    src = require(MAINT_600I)
    before = _sha(src)
    job = ingest_files([src], conn=conn, config=offline_config)

    assert job.state == "succeeded"
    assert job.errors == []
    assert job.files_total == 1 and job.files_done == 1
    assert job.chunks_created > 0
    assert job.chunks_embedded == 0  # no Ollama: nothing written to embeddings
    assert any("embedding model unavailable" in n for n in job.notes)

    status = job.to_status()
    assert set(status) == {
        "job_id", "state", "files_total", "files_done", "chunks_created",
        "chunks_embedded", "current_file", "errors",
    }

    assert _count(conn, "SELECT count(*) FROM manuals") == 1
    assert _count(conn, "SELECT count(*) FROM manual_pages") == 29
    chunks = _count(conn, "SELECT count(*) FROM manual_chunks")
    assert chunks == job.chunks_created
    assert _count(conn, "SELECT count(*) FROM manual_chunks_fts") == chunks  # triggers kept FTS in sync
    assert _count(conn, "SELECT count(*) FROM chunk_tags") > 0
    assert _count(conn, "SELECT count(*) FROM manuals WHERE status = 'imported' AND page_count = 29") == 1

    # The original is never altered; a copy lives next to the database.
    assert _sha(src) == before
    manual = conn.execute("SELECT file_path, file_hash FROM manuals").fetchone()
    assert manual["file_path"] == f"manuals/{manual['file_hash']}.pdf"
    assert (tmp_path / manual["file_path"]).exists()
    assert manual["file_hash"] == before


def test_duplicate_file_hash_is_skipped(conn, tmp_path, offline_config):
    src = require(MAINT_600I)
    ingest_files([src], conn=conn, config=offline_config)
    copy = tmp_path / "renamed-copy.pdf"
    shutil.copyfile(src, copy)
    job = ingest_files([copy], conn=conn, config=offline_config)
    assert job.chunks_created == 0
    assert any("duplicate" in n for n in job.notes)
    assert _count(conn, "SELECT count(*) FROM manuals") == 1


def test_force_reingest_replaces_in_place_without_duplicates(conn, offline_config):
    src = require(MAINT_600I)
    first = ingest_files([src], conn=conn, config=offline_config)
    n_chunks = _count(conn, "SELECT count(*) FROM manual_chunks")
    job = ingest_files([src], conn=conn, config=offline_config, force_reingest=True)
    assert job.state == "succeeded"
    assert _count(conn, "SELECT count(*) FROM manuals") == 1
    assert _count(conn, "SELECT count(*) FROM manual_chunks") == n_chunks == first.chunks_created
    assert _count(conn, "SELECT count(*) FROM manual_pages") == 29
    assert _count(conn, "SELECT count(*) FROM manual_chunks_fts") == n_chunks


def test_scanned_page_is_reported_not_skipped(conn, offline_config):
    scanned = require(SCHEMATICS_SCANNED)
    job = ingest_files([scanned], conn=conn, config=offline_config)
    ocr_errors = [e for e in job.errors if e.message == OCR_PENDING_MESSAGE]
    assert len(ocr_errors) == 7
    assert sorted(e.page for e in ocr_errors) == [1, 2, 3, 4, 5, 6, 7]
    assert job.chunks_created == 0
    assert job.state == "succeeded"  # the file was processed; OCR is a separate, pending step


def test_bm25_retrieves_the_flexramp_lubrication_page(conn, offline_config, unavailable_embedder):
    src = require(MAINT_600I)
    ingest_files([src], conn=conn, config=offline_config)
    resp = hybrid_search(
        "How often do I lubricate the flexramp axle?", top_k=5, conn=conn, query_embedder=unavailable_embedder
    )
    assert resp["fallback"] is True
    assert resp["results"], "expected at least one hit"
    pages = {p for r in resp["results"] for p in range(r["page_start"], r["page_end"] + 1)}
    assert pages & {2, 10}, f"expected page 10 (1.40 flexramp lubrication) in top results, got {pages}"
    top = resp["results"][0]
    assert top["citation"].endswith(("page 10", "page 2")) or "pages" in top["citation"]
    assert top["manual_title"] in top["citation"]
    assert set(top) == {
        "chunk_id", "manual_id", "manual_title", "page_start", "page_end", "section", "text", "score", "citation",
    }


def test_soft_deleted_chunks_are_excluded(conn, offline_config, unavailable_embedder):
    src = require(MAINT_600I)
    ingest_files([src], conn=conn, config=offline_config)
    query = "How often do I lubricate the flexramp axle?"
    before = hybrid_search(query, top_k=5, conn=conn, query_embedder=unavailable_embedder)["results"]
    doomed = [r["chunk_id"] for r in before]
    ids = [r[0] for r in conn.execute(
        f"SELECT id FROM manual_chunks WHERE guid IN ({','.join('?' for _ in doomed)})", doomed
    ).fetchall()]
    assert soft_delete_chunks(conn, ids) == len(ids)
    after = hybrid_search(query, top_k=5, conn=conn, query_embedder=unavailable_embedder)["results"]
    assert not ({r["chunk_id"] for r in after} & set(doomed))


def test_soft_deleted_manual_disappears_from_search(conn, offline_config, unavailable_embedder):
    src = require(MAINT_600I)
    job = ingest_files([src], conn=conn, config=offline_config)
    manual_id = conn.execute("SELECT id FROM manuals").fetchone()[0]
    assert soft_delete_manual(conn, manual_id) == job.chunks_created
    assert hybrid_search("flexramp axle", conn=conn, query_embedder=unavailable_embedder)["results"] == []
    assert get_manual_page(conn, manual_id, 10) is None


def test_manual_and_kind_filters(conn, offline_config, unavailable_embedder):
    src = require(MAINT_600I)
    ingest_files([src], conn=conn, config=offline_config)
    manual_id = conn.execute("SELECT id FROM manuals").fetchone()[0]
    scoped = hybrid_search("nuts tightened Nm", conn=conn, manual_id=manual_id, query_embedder=unavailable_embedder)
    assert scoped["results"] and all(r["manual_id"] == manual_id for r in scoped["results"])
    torque = hybrid_search(
        "nuts tightened Nm", conn=conn, chunk_kinds=["torque"], query_embedder=unavailable_embedder
    )
    kinds = {
        r[0] for r in conn.execute(
            "SELECT c.chunk_kind FROM manual_chunks c WHERE c.guid IN (%s)"
            % ",".join("?" for _ in torque["results"]),
            [r["chunk_id"] for r in torque["results"]],
        ).fetchall()
    }
    assert torque["results"] and kinds == {"torque"}
    no_text = hybrid_search("flexramp", conn=conn, include_text=False, query_embedder=unavailable_embedder)
    assert no_text["results"] and all(r["text"] is None for r in no_text["results"])


def test_hostile_user_query_does_not_crash_search(conn, offline_config, unavailable_embedder):
    src = require(MAINT_600I)
    ingest_files([src], conn=conn, config=offline_config)
    for q in ['660-28 "unbalanced', "what's the torque? (17 Nm)", "NEAR(a b) AND OR NOT", "*:^"]:
        resp = hybrid_search(q, conn=conn, query_embedder=unavailable_embedder)
        assert isinstance(resp["results"], list)


def test_unknown_chunk_kind_is_rejected(conn):
    import pytest

    with pytest.raises(ValueError):
        hybrid_search("x", chunk_kinds=["nonsense"], conn=conn, query_embedder=lambda t: None)


def test_vector_path_writes_and_reuses_embeddings(conn, vec, offline_config):
    """Exercises vec0 writes, KNN, and the cleanup rules. Uses the TEST DOUBLE embedder."""
    src = require(MAINT_600I)
    job = ingest_files([src], conn=conn, config=offline_config, embedder=hashing_test_embedder)
    chunks = _count(conn, "SELECT count(*) FROM manual_chunks")
    assert job.chunks_embedded == chunks
    assert _count(conn, "SELECT count(*) FROM embeddings") == chunks

    resp = hybrid_search(
        "How often do I lubricate the flexramp axle?", conn=conn, query_embedder=hashing_test_embedder
    )
    assert resp["fallback"] is False
    assert resp["results"]
    assert resp["results"][0]["score"] <= 1.0


def test_reingest_and_soft_delete_leave_no_stale_embeddings(conn, vec, offline_config):
    """Rule: re-ingest and soft-delete remove the old embeddings rows (vec0 KNN has no relational filter)."""
    src = require(MAINT_600I)
    ingest_files([src], conn=conn, config=offline_config, embedder=hashing_test_embedder)
    chunks = _count(conn, "SELECT count(*) FROM manual_chunks")
    assert _count(conn, "SELECT count(*) FROM embeddings") == chunks

    ingest_files([src], conn=conn, config=offline_config, force_reingest=True, embedder=hashing_test_embedder)
    assert _count(conn, "SELECT count(*) FROM manual_chunks") == chunks
    assert _count(conn, "SELECT count(*) FROM embeddings") == chunks  # old vectors gone, new ones written

    manual_id = conn.execute("SELECT id FROM manuals").fetchone()[0]
    soft_delete_manual(conn, manual_id)
    assert _count(conn, "SELECT count(*) FROM embeddings") == 0
    assert _count(conn, "SELECT count(*) FROM manual_chunks WHERE deleted_at IS NULL") == 0


def test_embed_missing_fills_in_later_without_duplicates(conn, vec, offline_config):
    src = require(MAINT_600I)
    job = IngestionJob()
    ingest_files([src], conn=conn, config=offline_config, job=job)  # no vectors yet
    assert _count(conn, "SELECT count(*) FROM embeddings") == 0
    written = embed_missing(conn, hashing_test_embedder, job=job)
    assert written == job.chunks_created
    assert embed_missing(conn, hashing_test_embedder) == 0  # idempotent
    assert _count(conn, "SELECT count(*) FROM embeddings") == job.chunks_created
