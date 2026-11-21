"""Phase 10C.5: GSE component manual seed list, its acceptance question sets, and the two ingestion fixes the
seeding found (AES-encrypted manuals; one page whose text extraction throws must not fail the manual).

The acceptance run needs the owner's library under `Reference/GSE diagnostic apps` (gitignored, never
bundled) and is skipped when it is absent. Manuals marked `slow` only run with GSE_RAG_SLOW=1.
The question sets are self-authored from the same PDF text layer (in-sample regression check).
"""

from __future__ import annotations

import os
from pathlib import Path

import pytest

from conftest import REPO
from ingest import pdf_text
from ingest.pipeline import TEXT_EXTRACT_FAILED_MESSAGE, ingest_files
from ingest.seed import SEED_FILE, load_seed, resolve, seed_library
from rag.search import hybrid_search

LIBRARY = REPO / "Reference" / "GSE diagnostic apps"


def test_seed_list_is_valid_and_covers_the_10c_documents():
    spec, manuals = load_seed()
    assert spec["top_k"] == 5 and 0 < spec["min_hit_rate"] <= 1
    ids = {m.id for m in manuals}
    # PCS transmission, IES2000 shifter, park brake, Power Stow/TUG and the Ford 2.5 engine (PLAN 10C.5).
    for required in ("pcs-tech", "pcs-qrg", "ies2000", "mico-burnish", "pcs-brk2050", "ps-maint", "ps-user", "ford-gcp"):
        assert required in ids
    assert all(m.questions for m in manuals)
    assert all(not Path(m.path).is_absolute() for m in manuals)


def test_seed_rejects_paths_outside_the_library_root(tmp_path):
    bad = tmp_path / "seed.json"
    bad.write_text('{"manuals": [{"id": "x", "path": "../secret.pdf", "title": "t", "manufacturer": "m", '
                   '"model_family": "f", "questions": [{"question": "q", "expected_pages": [1]}]}]}', encoding="utf-8")
    with pytest.raises(ValueError, match="relative"):
        load_seed(bad)


def test_resolve_reports_missing_files(tmp_path):
    _, manuals = load_seed()
    present, missing = resolve(manuals, tmp_path)
    assert present == [] and len(missing) == len(manuals)


def test_aes_encrypted_pdf_with_empty_user_password_is_read(tmp_path):
    pypdf = pytest.importorskip("pypdf")
    pytest.importorskip("cryptography")
    writer = pypdf.PdfWriter()
    writer.add_blank_page(width=200, height=200)
    writer.encrypt(user_password="", owner_password="owner-only", algorithm="AES-256")
    path = tmp_path / "aes.pdf"
    with path.open("wb") as fh:
        writer.write(fh)

    doc = pdf_text.read_pdf(path)
    assert doc.page_count == 1 and doc.pages[0].blank


class _FakePage:
    def __init__(self, text: str | None):
        self._text = text

    def extract_text(self):
        if self._text is None:
            raise RuntimeError("Reached maximum number of tokens per stream.")
        return self._text


class _FakeReader:
    is_encrypted = False
    metadata = None

    def __init__(self, _path):
        self.pages = [_FakePage("Change the hydraulic pressure filter every 500 hours."), _FakePage(None),
                      _FakePage("Test all emergency stops before use each shift.")]


def test_one_unreadable_page_does_not_fail_the_manual(monkeypatch, tmp_path, conn, offline_config, unavailable_embedder):
    monkeypatch.setattr(pdf_text, "PdfReader", _FakeReader)
    monkeypatch.setattr(pdf_text, "_has_images", lambda page: False)
    doc = pdf_text.read_pdf(tmp_path / "x.pdf")
    assert [p.extract_error is not None for p in doc.pages] == [False, True, False]
    assert doc.pages[1].lines == [] and not doc.pages[1].scanned

    pdf = tmp_path / "manual.pdf"
    pdf.write_bytes(b"%PDF-1.4 placeholder")  # bytes are hashed/stored; text comes from the fake reader
    import ingest.pipeline as pipeline
    monkeypatch.setattr(pipeline, "read_pdf", pdf_text.read_pdf)
    job = ingest_files([pdf], conn=conn, config=offline_config, embedder=unavailable_embedder)
    assert job.state == "succeeded" and job.chunks_created > 0
    assert any(e.page == 2 and e.message.startswith(TEXT_EXTRACT_FAILED_MESSAGE) for e in job.errors)


def test_seed_acceptance_library_wide(conn, offline_config, unavailable_embedder):
    if not LIBRARY.is_dir():
        pytest.skip("owner manual library (Reference/GSE diagnostic apps) not present")
    spec, manuals = load_seed()
    include_slow = os.environ.get("GSE_RAG_SLOW") == "1"
    job, present, missing = seed_library(LIBRARY, conn=conn, config=offline_config,
                                         embedder=unavailable_embedder, include_slow=include_slow)
    assert present, "no seed manuals found under the library root"
    assert not [e for e in job.errors if "Error" in e.message and e.page is None], job.errors

    titles = {m.id: m.title for m, _ in present}
    total = hits = 0
    per_manual: dict[str, tuple[int, int]] = {}
    for m, _ in present:
        mh = 0
        for case in m.questions:
            resp = hybrid_search(case["question"], top_k=spec["top_k"], conn=conn, query_embedder=unavailable_embedder)
            expected = set(case["expected_pages"])
            hit = any(r["manual_title"] == titles[m.id] and r["page_start"] in expected for r in resp["results"])
            cited = [(r["manual_title"][:24], r["page_start"]) for r in resp["results"]]
            print(f"{'HIT ' if hit else 'MISS'} [{m.id}] {case['question']!r} expected={sorted(expected)} cited={cited}")
            mh += hit
        per_manual[m.id] = (mh, len(m.questions))
        hits += mh
        total += len(m.questions)
    print(f"\nSEED ACCEPTANCE HIT@{spec['top_k']} (library-wide, right manual + page): {hits}/{total} = {hits / total:.0%}")
    for mid, (h, n) in per_manual.items():
        print(f"  {mid:14} {h}/{n}")
    print(f"missing (skipped): {[m.id for m in missing]}")
    assert hits / total >= spec["min_hit_rate"]
    # No manual may be unfindable: each one answers at least one of its own questions.
    assert all(h >= 1 for h, _ in per_manual.values()), per_manual
