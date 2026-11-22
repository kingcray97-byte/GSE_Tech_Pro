"""Per-page text extraction with pypdf (BSD-3, ADR-005). Never modifies the source file.

A page with almost no text layer is `scanned` when it contains images (read by OCR in ingest/ocr.py, see
`apply_ocr`) and `blank` otherwise (no error recorded). A page whose text cannot be extracted at all (for example a drawing
whose content stream exceeds pypdf's parsing limits) keeps `extract_error` and no lines, so one bad page never
fails the whole manual.

Encrypted PDFs that open with the empty user password (common "no copy/print" owner protection) are read;
AES-encrypted ones need the `cryptography` package (declared in pyproject.toml).
"""

from __future__ import annotations

import math
import re
from collections import Counter
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any

from pypdf import PdfReader

MIN_TEXT_CHARS = 20
_MIN_PAGES_FOR_HEADER_STRIP = 4


@dataclass
class PageText:
    number: int  # 1-based PDF page index (== the "page" used in citations)
    lines: list[str]
    has_images: bool
    scanned: bool
    blank: bool
    extract_error: str | None = None  # text extraction failed on this page (reason), lines are empty
    ocr: bool = False  # lines were read by OCR from the page image (scanned page), not from a text layer


@dataclass
class PdfDocument:
    path: Path
    page_count: int
    title: str | None
    pages: list[PageText]


_DEGREE_LIKE_RE = re.compile(r"(?<=\d)[º˚](?=[CcFf]\b)")  # "25º C", "25˚F" -> "25°C", "25°F"


def normalize_line(line: str) -> str:
    """Deterministic glyph cleanup for pypdf output. Never rewrites numeric values.

    - drops U+FFFD replacement characters (extraction could not decode the glyph)
    - non-breaking spaces -> spaces; curly quotes -> ASCII quotes; en/em dashes -> '-'
    - degree-like masculine-ordinal / ring-above glyphs directly before C/F after a digit -> '°'
    Private-use symbol glyphs are left untouched: their meaning cannot be recovered without guessing.
    """
    s = line.replace("\ufffd", "")
    s = s.replace("\u00a0", " ")
    s = s.replace("\u201c", '"').replace("\u201d", '"').replace("\u2018", "'").replace("\u2019", "'")
    s = s.replace("\u2013", "-").replace("\u2014", "-")
    return _DEGREE_LIKE_RE.sub("°", s)


def _resolve(obj: Any) -> Any:
    return obj.get_object() if hasattr(obj, "get_object") else obj


def _has_images(page: Any) -> bool:
    try:
        resources = _resolve(page.get("/Resources"))
        if not resources:
            return False
        xobjects = _resolve(resources.get("/XObject"))
        if not xobjects:
            return False
        return any(_resolve(ref).get("/Subtype") == "/Image" for ref in xobjects.values())
    except Exception:  # malformed resources: treat as no images rather than failing the import
        return False


def read_pdf(path: str | Path) -> PdfDocument:
    path = Path(path)
    reader = PdfReader(str(path))
    if reader.is_encrypted and not reader.decrypt(""):
        raise ValueError("encrypted PDF (password required)")
    title: str | None = None
    try:
        raw_title = reader.metadata.title if reader.metadata else None
        title = raw_title.strip() if raw_title and raw_title.strip() else None
    except Exception:
        title = None

    pages: list[PageText] = []
    for number, page in enumerate(reader.pages, start=1):
        try:
            text = page.extract_text() or ""
        except Exception as exc:  # noqa: BLE001 - pypdf raises many types on malformed/oversized pages
            pages.append(
                PageText(
                    number=number,
                    lines=[],
                    has_images=False,
                    scanned=False,
                    blank=False,
                    extract_error=f"{type(exc).__name__}: {exc}"[:200],
                )
            )
            continue
        short = len(text.strip()) < MIN_TEXT_CHARS
        has_images = _has_images(page)
        pages.append(
            PageText(
                number=number,
                lines=[normalize_line(line).rstrip() for line in text.splitlines()],
                has_images=has_images,
                scanned=short and has_images,
                blank=short and not has_images,
            )
        )
    return PdfDocument(path=path, page_count=len(pages), title=title, pages=pages)


def apply_ocr(page: PageText, ocr_lines: list[str]) -> PageText | None:
    """A scanned page with its OCR lines (glyph-normalized like extracted text), or None when OCR found too little
    text to index (the page stays scanned)."""
    lines = [normalize_line(line).rstrip() for line in ocr_lines]
    if len("".join(lines).strip()) < MIN_TEXT_CHARS:
        return None
    return replace(page, lines=lines, scanned=False, blank=False, ocr=True)


def _repeat_key(line: str) -> str:
    """Normalized key for header/footer detection. Lines without letters are never keys (so step
    numbers such as '2.10' or hour values are never stripped)."""
    s = line.strip()
    if sum(ch.isalpha() for ch in s) < 3:
        return ""
    return re.sub(r"\s+", " ", re.sub(r"\d+", "#", s.lower()))


def strip_repeated_lines(pages: list[PageText]) -> list[PageText]:
    """Drop running headers/footers (e.g. '740-774 Maintenance scheme   Page 2 of 29') that repeat on at
    least half of the text pages."""
    text_pages = [p for p in pages if not (p.scanned or p.blank)]
    if len(text_pages) < _MIN_PAGES_FOR_HEADER_STRIP:
        return pages
    counts: Counter[str] = Counter()
    for page in text_pages:
        keys = {k for k in (_repeat_key(line) for line in page.lines) if k}
        counts.update(keys)
    threshold = max(3, math.ceil(0.5 * len(text_pages)))
    repeated = {k for k, n in counts.items() if n >= threshold}
    if not repeated:
        return pages
    out: list[PageText] = []
    for page in pages:
        if page.scanned or page.blank:
            out.append(page)
        else:
            out.append(replace(page, lines=[ln for ln in page.lines if _repeat_key(ln) not in repeated]))
    return out
