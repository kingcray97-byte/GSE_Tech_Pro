"""Chunking: page numbers are preserved, steps are never split, chunk kinds are sensible."""

from __future__ import annotations

from conftest import MAINT_600I, require
from ingest.chunker import MAX_CHARS, STEP_RE, chunk_pages, classify
from ingest.pdf_text import MIN_TEXT_CHARS, PageText, read_pdf, strip_repeated_lines


def _page(num: int, lines: list[str]) -> PageText:
    return PageText(number=num, lines=lines, has_images=False, scanned=False, blank=False)


def test_steps_are_atomic_and_page_numbers_kept():
    long_body = "Inspect the bushing carefully for play and wear marks. " * 14  # ~750 chars
    pages = [
        _page(3, ["1.10 Check the first thing", long_body, "", "1.20 Check the second thing", long_body]),
        _page(4, ["1.30 Check the third thing", "Short note on page four."]),
    ]
    chunks = chunk_pages(pages)
    assert chunks, "expected at least one chunk"
    for ch in chunks:
        assert 1 <= ch.page_start <= ch.page_end
        assert len(ch.text) <= MAX_CHARS + 200  # soft cap; one atomic step may be a little over
    # Every step title appears exactly once, and always at the start of a line inside a chunk.
    for title in ("1.10 Check the first thing", "1.20 Check the second thing", "1.30 Check the third thing"):
        holders = [ch for ch in chunks if title in ch.text.splitlines()]
        assert len(holders) == 1, title
    # Page numbers follow the page each step came from.
    by_title = {}
    for ch in chunks:
        for line in ch.text.splitlines():
            if STEP_RE.match(line.strip()):
                by_title[line.strip()] = ch
    assert by_title["1.10 Check the first thing"].page_start == 3
    assert by_title["1.30 Check the third thing"].page_start == 4
    assert by_title["1.30 Check the third thing"].page_end == 4


def test_chunks_never_span_pages_and_report_range():
    pages = [_page(1, ["Alpha paragraph text here."]), _page(2, ["Beta paragraph text here."])]
    chunks = chunk_pages(pages)
    assert [(c.page_start, c.page_end) for c in chunks] == [(1, 1), (2, 2)]


def test_scanned_and_blank_pages_produce_no_chunks():
    pages = [
        PageText(1, ["x"], has_images=True, scanned=True, blank=False),
        PageText(2, [""], has_images=False, scanned=False, blank=True),
    ]
    assert chunk_pages(pages) == []


def test_chunk_kind_heuristics():
    assert classify("Illustration: Power Stow Drawing No. 14304, 12350")[0] == "diagram_caption"
    assert classify("All 6 mm nuts are tightened to 17 Nm.")[0] == "torque"
    assert classify("The system fault: no hydraulic movement. Possible causes listed below.")[0] == "troubleshooting"
    assert classify("Operating pressure 2500 psi, nominal 24 VDC supply.")[0] == "specs"
    assert classify("Remove the cover. Disconnect the harness connector. Check the fuse.")[0] in {"wiring", "procedure"}
    assert classify("Plain descriptive sentence about the unit.")[0] == "text"


def test_bare_decimal_in_running_text_is_not_a_step():
    assert STEP_RE.match("0.8 Inch gap") is None
    assert STEP_RE.match("2.20 Check for loose bolts") is not None
    assert STEP_RE.match("2.10") is not None


def test_real_600i_pages_round_trip_into_chunks():
    pdf = require(MAINT_600I)
    doc = read_pdf(pdf)
    assert doc.page_count == 29
    assert all(not p.scanned for p in doc.pages), "600i has a full text layer"
    pages = strip_repeated_lines(doc.pages)
    chunks = chunk_pages(pages)
    assert len(chunks) >= 20
    page_text = {p.number: "\n".join(p.lines) for p in pages}
    for ch in chunks:
        assert 1 <= ch.page_start <= ch.page_end <= 29
        # The first meaningful line of each chunk must come from its declared start page.
        first_line = next(line.strip() for line in ch.text.splitlines() if line.strip())
        probe = first_line[:25]
        assert probe in page_text[ch.page_start] or probe in page_text[ch.page_end], (ch.page_start, probe)


def test_header_footer_stripping_keeps_step_numbers():
    pages = []
    for n in range(1, 8):
        pages.append(
            PageText(n, [f"740-774 Maintenance scheme      Page {n} of 7", "2.10", "Lubricate the axle pivots now"],
                     has_images=False, scanned=False, blank=False)
        )
    out = strip_repeated_lines(pages)
    joined = "\n".join("\n".join(p.lines) for p in out)
    assert "Page" not in joined
    assert "2.10" in joined  # bare step numbers are never treated as headers
    assert MIN_TEXT_CHARS > 0
