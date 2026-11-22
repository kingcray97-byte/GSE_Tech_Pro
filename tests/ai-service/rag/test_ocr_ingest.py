"""Phase 7.6: scanned (image-only) manual pages are read by OCR at import and tagged `source:ocr`.

Most tests use a hand-made PDF (one text page, two image-only pages) and a fake OCR engine, so they run anywhere.
`test_real_windows_ocr_reads_660_28_scanned_pages` uses Windows.Media.Ocr on pages cut from the real 660-28 manual and
skips when the engine or the manual is not on this machine.
"""

from __future__ import annotations

import threading
from dataclasses import dataclass, field
from pathlib import Path

import pytest

from conftest import MANUALS, require
from ingest.ocr import MAX_PIXELS, MAX_SIDE_PX, OcrPage, make_page_ocr, render_scale
from ingest.pipeline import (
    OCR_EMPTY_MESSAGE,
    OCR_FAILED_MESSAGE,
    OCR_PENDING_MESSAGE,
    OCR_STOPPED_MESSAGE,
    ingest_files,
)
from rag.search import OCR_TAG, get_manual_page, hybrid_search
from tools.manuals import OCR_PASSAGE_NOTE, SearchManualsArgs, _search_manuals
from tools.registry import ToolContext

MANUAL_660_28 = MANUALS / "660-28_Maintenance&Parts_052019C.pdf"

TEXT_PAGE = "Hydraulic pump inspection. Check the hose fittings for leaks before every shift."
OCR_PAGE_2 = [
    "AXLE SHAFT REMOVAL",
    "Remove the axle shaft retaining bolts and pull the shaft",
    "from the housing. Torque bolts to 75 ft-lb on assembly.",
]


def _pdf_bytes() -> bytes:
    """Three pages: page 1 has a text layer, pages 2 and 3 only an image (what a scan looks like to pypdf)."""
    text_stream = f"BT /F1 12 Tf 72 720 Td ({TEXT_PAGE}) Tj ET".encode()
    image_stream = b"q 612 0 0 792 0 0 cm /Im1 Do Q"
    pixel = b"\x80"
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R"
        b" /Resources << /Font << /F1 8 0 R >> >> >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R"
        b" /Resources << /XObject << /Im1 9 0 R >> >> >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R"
        b" /Resources << /XObject << /Im1 9 0 R >> >> >>",
        b"<< /Length %d >>\nstream\n" % len(text_stream) + text_stream + b"\nendstream",
        b"<< /Length %d >>\nstream\n" % len(image_stream) + image_stream + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        b"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8"
        b" /Length 1 >>\nstream\n" + pixel + b"\nendstream",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for number, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for offset in offsets:
        out += b"%010d 00000 n \n" % offset
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, xref)
    return bytes(out)


@pytest.fixture
def scanned_pdf(tmp_path: Path) -> Path:
    path = tmp_path / "Axle service bulletin.pdf"
    path.write_bytes(_pdf_bytes())
    return path


@dataclass
class FakeOcr:
    """Page number -> lines, or an Exception to report that page as unreadable."""

    pages: dict
    engine: str = "Fake OCR"
    stop_after: int | None = None
    calls: list = field(default_factory=list)

    def read_pages(self, pdf_path, page_numbers, keep_going=lambda: True):
        self.calls.append((Path(pdf_path).name, list(page_numbers)))
        for done, number in enumerate(page_numbers):
            if not keep_going() or (self.stop_after is not None and done >= self.stop_after):
                return
            value = self.pages.get(number, [])
            if isinstance(value, Exception):
                yield OcrPage(number=number, lines=[], error=f"{type(value).__name__}: {value}")
            else:
                yield OcrPage(number=number, lines=list(value))


def _chunks(conn):
    return conn.execute(
        "SELECT c.id, c.page_start, c.chunk_text, "
        "EXISTS (SELECT 1 FROM chunk_tags t WHERE t.chunk_id = c.id AND t.tag = ?) AS ocr "
        "FROM manual_chunks c ORDER BY c.page_start, c.id",
        (OCR_TAG,),
    ).fetchall()


def test_scanned_pages_are_read_by_ocr_and_tagged(conn, scanned_pdf, offline_config, unavailable_embedder):
    ocr = FakeOcr({2: OCR_PAGE_2, 3: []})
    job = ingest_files([scanned_pdf], conn=conn, config=offline_config, page_ocr=ocr)

    assert job.state == "succeeded"
    assert ocr.calls == [(scanned_pdf.name, [2, 3])]  # only the image-only pages, never the text page
    assert [(e.page, e.message) for e in job.errors] == [(3, OCR_EMPTY_MESSAGE)]
    assert any("1 scanned page(s) read by Fake OCR" in n for n in job.notes)

    rows = _chunks(conn)
    assert {r["page_start"]: bool(r["ocr"]) for r in rows} == {1: False, 2: True}
    assert "Torque bolts to 75 ft-lb" in next(r["chunk_text"] for r in rows if r["page_start"] == 2)

    hits = hybrid_search("axle shaft retaining bolts", top_k=3, conn=conn, query_embedder=unavailable_embedder)
    top = hits["results"][0]
    assert top["page_start"] == 2 and top["ocr"] is True
    assert top["citation"].endswith("page 2")  # citation text is unchanged; the flag travels beside it
    other = hybrid_search("hose fittings leaks", top_k=3, conn=conn, query_embedder=unavailable_embedder)
    assert other["results"][0]["ocr"] is False

    manual_id = top["manual_id"]
    page = get_manual_page(conn, manual_id, 2)
    assert page is not None and [c["ocr"] for c in page["chunks"]] == [True]


def test_search_tool_tells_the_model_to_check_ocr_numbers(conn, db_path, scanned_pdf, offline_config):
    ingest_files([scanned_pdf], conn=conn, config=offline_config, page_ocr=FakeOcr({2: OCR_PAGE_2}))
    ctx = ToolContext(db_path=db_path, config=offline_config, embedder=None)

    scanned = _search_manuals(ctx, SearchManualsArgs(query="axle shaft retaining bolts torque"))
    first = scanned.content.splitlines()[1]
    assert first.startswith("[1] Axle service bulletin, page 2 " + OCR_PASSAGE_NOTE)
    assert scanned.sources[0].display_text == "Axle service bulletin, page 2"

    typed = _search_manuals(ctx, SearchManualsArgs(query="hose fittings leaks"))
    assert OCR_PASSAGE_NOTE not in typed.content.splitlines()[1]


def test_unreadable_page_and_ocr_off_are_reported(conn, tmp_path, scanned_pdf, offline_config):
    job = ingest_files([scanned_pdf], conn=conn, config=offline_config,
                       page_ocr=FakeOcr({2: RuntimeError("render failed"), 3: ["x"]}))
    assert [(e.page, e.message) for e in job.errors] == [
        (2, f"{OCR_FAILED_MESSAGE} (RuntimeError: render failed)"),
        (3, OCR_EMPTY_MESSAGE),  # one character is not worth indexing
    ]

    other = tmp_path / "copy-with-different-bytes.pdf"
    other.write_bytes(_pdf_bytes() + b"\n% another file\n")
    off = ingest_files([other], conn=conn, config=offline_config, page_ocr=None)
    assert [(e.page, e.message) for e in off.errors] == [
        (2, f"{OCR_PENDING_MESSAGE} (OCR is turned off)"),
        (3, f"{OCR_PENDING_MESSAGE} (OCR is turned off)"),
    ]
    assert off.state == "succeeded"


def test_cancel_during_ocr_reports_the_pages_not_read(conn, scanned_pdf, offline_config):
    job = ingest_files([scanned_pdf], conn=conn, config=offline_config,
                       page_ocr=FakeOcr({2: OCR_PAGE_2, 3: OCR_PAGE_2}, stop_after=1))
    assert [(e.page, e.message) for e in job.errors] == [(3, OCR_STOPPED_MESSAGE)]


def test_reimport_fills_in_scanned_pages_without_touching_existing_chunks(conn, scanned_pdf, offline_config):
    first = ingest_files([scanned_pdf], conn=conn, config=offline_config, page_ocr=None)
    assert [e.page for e in first.errors] == [2, 3]
    before = [(r["id"], r["chunk_text"]) for r in _chunks(conn)]
    assert len(before) == 1  # only the text page

    ocr = FakeOcr({2: OCR_PAGE_2, 3: ["OIL CHANGE", "Drain the oil and replace the filter every 250 hours."]})
    again = ingest_files([scanned_pdf], conn=conn, config=offline_config, page_ocr=ocr)
    assert again.state == "succeeded"
    assert again.errors == []
    assert ocr.calls == [(scanned_pdf.name, [2, 3])]
    assert any("already imported; 2 chunk(s) added from its scanned pages" in n for n in again.notes)
    assert again.chunks_created == 2

    rows = _chunks(conn)
    assert [(r["id"], r["chunk_text"]) for r in rows if not r["ocr"]] == before  # same rows, same ids
    assert sorted(r["page_start"] for r in rows if r["ocr"]) == [2, 3]
    assert conn.execute("SELECT count(*) FROM manuals").fetchone()[0] == 1
    # Base tags carried over from the manual's existing chunks.
    tags = {r[0] for r in conn.execute(
        "SELECT t.tag FROM chunk_tags t JOIN manual_chunks c ON c.id = t.chunk_id WHERE c.page_start = 2")}
    assert OCR_TAG in tags

    # Nothing left to read: a third import is an ordinary duplicate and OCR is not asked again.
    ocr3 = FakeOcr({})
    third = ingest_files([scanned_pdf], conn=conn, config=offline_config, page_ocr=ocr3)
    assert ocr3.calls == []
    assert any("duplicate" in n for n in third.notes)


def test_pages_whose_chunks_were_deleted_are_not_read_again(conn, scanned_pdf, offline_config):
    from ingest.maintenance import soft_delete_chunks

    ingest_files([scanned_pdf], conn=conn, config=offline_config, page_ocr=FakeOcr({2: OCR_PAGE_2}))
    ocr_chunk = [r["id"] for r in _chunks(conn) if r["ocr"]]
    soft_delete_chunks(conn, ocr_chunk)
    ocr = FakeOcr({})
    ingest_files([scanned_pdf], conn=conn, config=offline_config, page_ocr=ocr)
    assert ocr.calls == [(scanned_pdf.name, [3])]  # page 3 never had text; page 2 was deleted on purpose


def test_render_scale_caps_large_sheets():
    letter = render_scale(612, 792)
    assert letter == pytest.approx(300 / 72)
    e_size = render_scale(44 * 72, 34 * 72)  # 44 x 34 in drawing
    assert 44 * 72 * e_size <= MAX_SIDE_PX + 1
    assert (44 * 72 * e_size) * (34 * 72 * e_size) <= MAX_PIXELS + 1


def _cut_pages(source: Path, numbers: list[int], target: Path) -> Path:
    from pypdf import PdfReader, PdfWriter

    reader = PdfReader(str(source))
    writer = PdfWriter()
    for n in numbers:
        writer.add_page(reader.pages[n - 1])
    with target.open("wb") as fh:
        writer.write(fh)
    return target


def test_real_windows_ocr_reads_660_28_scanned_pages(conn, db_path, tmp_path, offline_config, unavailable_embedder):
    source = require(MANUAL_660_28)
    ocr, reason = make_page_ocr()
    if ocr is None:
        pytest.skip(f"Windows OCR not available here: {reason}")
    # 1639 = the axle section index, 1700 = TRAC-LOK assembly steps: both image-only in the 660-28 PDF.
    sample = _cut_pages(source, [1639, 1700], tmp_path / "660-28 scanned sample.pdf")

    result: dict = {}

    def run() -> None:  # the service runs ingest on a worker thread: WinRT must work there too
        from rag.db import connect

        own = connect(db_path)
        try:
            result["job"] = ingest_files([sample], conn=own, config=offline_config)
        finally:
            own.close()

    worker = threading.Thread(target=run)
    worker.start()
    worker.join(timeout=120)
    job = result["job"]

    assert job.state == "succeeded", job.errors
    assert job.errors == []
    assert any("2 scanned page(s) read by Windows.Media.Ocr" in n for n in job.notes)
    texts = " ".join(r["chunk_text"] for r in _chunks(conn) if r["ocr"])
    assert "REAR AXLES" in texts
    assert "TRAC-LOK" in texts
    hits = hybrid_search("clutch pack side gear splines", top_k=3, conn=conn, query_embedder=unavailable_embedder)
    assert hits["results"] and hits["results"][0]["page_start"] == 2 and hits["results"][0]["ocr"] is True
