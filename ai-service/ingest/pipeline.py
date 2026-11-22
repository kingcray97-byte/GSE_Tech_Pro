"""Ingest pipeline: import PDFs (file or folder), dedupe by sha256, chunk, write RAG tables, embed.

Writes only the RAG tables (manuals, manual_pages, manual_chunks, chunk_tags, embeddings). FTS5 is kept in
sync by the triggers in 001_Initial.sql. Original PDFs are only read; a copy is stored under
<database folder>/manuals/<sha256>.pdf and `manuals.file_path` is relative to the database folder.

Scanned (image-only) pages are read by OCR (ingest/ocr.py) and their chunks tagged `source:ocr`. Importing a manual
that is already in the library (same file hash) fills in its scanned pages that have no text yet, without touching
the chunks it already has.
"""

from __future__ import annotations

import hashlib
import os
import re
import sqlite3
import threading
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from ingest.chunker import ChunkDraft, chunk_pages
from ingest.job import IngestionJob, register_job
from ingest.ocr import PageOcr, make_page_ocr
from ingest.pdf_text import PdfDocument, PageText, apply_ocr, read_pdf, strip_repeated_lines
from rag.config import Config
from rag.db import connect, delete_embeddings, is_vec0_available, to_vec_blob
from rag.embedding import Embedder, make_document_embedder
from rag.search import OCR_TAG  # chunk text read by OCR from a page image: numbers must be checked against the page

OCR_PENDING_MESSAGE = "scanned page not searchable: OCR is unavailable on this computer"
OCR_EMPTY_MESSAGE = "scanned page not searchable: OCR found no readable text"
OCR_FAILED_MESSAGE = "scanned page not searchable: OCR could not read it"
OCR_STOPPED_MESSAGE = "scanned page not read: the import was cancelled"
TEXT_EXTRACT_FAILED_MESSAGE = "page text could not be extracted; page skipped"
OFFICE_SUFFIXES = frozenset({".doc", ".docx", ".ppt", ".pptx"})
MANUALS_SUBDIR = "manuals"
EMBED_BATCH = 16

_MODEL_PATTERNS = (re.compile(r"\b(\d{3}-\d{2})\b"), re.compile(r"\b(\d{3})i\b", re.IGNORECASE))


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


# ---------------------------------------------------------------- paths


def normalize_stem(stem: str) -> str:
    """Match a .doc to its PDF twin: drop index digits, 'rev X', 'TUG', punctuation; lower-case."""
    s = stem.lower()
    s = re.sub(r"\brev\.?\s*[a-z]\b", " ", s)
    s = re.sub(r"\btug\b", " ", s)
    s = re.sub(r"^\s*\d+\s+", "", s)
    return re.sub(r"[^a-z0-9]", "", s)


MAX_PDF_BYTES = 200 * 1024 * 1024  # a single manual larger than this is rejected (8 GB tablet budget)
MAX_FILES = 2000  # files collected per request
MAX_DEPTH = 6  # folder levels below the requested folder


def _reject_reason(raw: str | Path) -> str | None:
    """Network shares and drive/filesystem roots are never ingest sources (the app picks local folders)."""
    text = str(raw)
    if text.startswith("\\\\") or text.startswith("//"):
        return "rejected: UNC/network paths are not allowed"
    p = Path(text)
    if p.parent == p:
        return "rejected: drive or filesystem root is not allowed"
    return None


def _walk_files(root: Path, problems: list[tuple[str, str]]) -> list[Path]:
    """Bounded, non-following walk: at most MAX_DEPTH levels and MAX_FILES files."""
    base = len(root.parts)
    out: list[Path] = []
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        if len(Path(dirpath).parts) - base >= MAX_DEPTH:
            dirnames[:] = []  # do not descend further
        for name in sorted(filenames):
            if len(out) >= MAX_FILES:
                problems.append((str(root), f"folder has more than {MAX_FILES} files; import stopped at the limit"))
                return out
            out.append(Path(dirpath, name))
    return out


def expand_paths(paths: Iterable[str | Path]) -> tuple[list[Path], list[tuple[str, str]]]:
    """Expand files and folders into PDFs to ingest. Returns (pdfs, skipped_or_error (file, message))."""
    pdfs: list[Path] = []
    problems: list[tuple[str, str]] = []
    for raw in paths:
        reason = _reject_reason(raw)
        if reason is not None:
            problems.append((str(raw), reason))
            continue
        p = Path(raw)
        if p.is_dir():
            files = _walk_files(p, problems)
            folder_pdfs = sorted(f for f in files if f.suffix.lower() == ".pdf")
            pdfs.extend(folder_pdfs)
            pdf_stems = {normalize_stem(f.stem) for f in folder_pdfs}
            for f in sorted(files):
                if f.suffix.lower() not in OFFICE_SUFFIXES:
                    continue
                if normalize_stem(f.stem) in pdf_stems:
                    continue  # a PDF of the same document exists (ADR-005): skip silently
                problems.append(
                    (f.name, "skipped: office format with no PDF version (LibreOffice conversion not available)")
                )
        elif p.is_file():
            if p.suffix.lower() == ".pdf":
                pdfs.append(p)
            elif p.suffix.lower() in OFFICE_SUFFIXES:
                problems.append((p.name, "unsupported: office format needs a PDF version (no LibreOffice)"))
            else:
                problems.append((p.name, "unsupported file type"))
        else:
            problems.append((str(p), "not found"))
    unique: dict[str, Path] = {}
    for pdf in pdfs:
        unique.setdefault(str(pdf.resolve()).lower(), pdf)
    return list(unique.values()), problems


def _data_dir(conn: sqlite3.Connection) -> Path:
    for row in conn.execute("PRAGMA database_list").fetchall():
        if row[1] == "main" and row[2]:
            return Path(row[2]).parent
    raise ValueError("ingest needs a file-backed database; manuals are stored next to it")


def _store_original(data: bytes, file_hash: str, data_dir: Path) -> str:
    rel = f"{MANUALS_SUBDIR}/{file_hash}.pdf"
    target = data_dir / rel
    if not target.exists():
        target.parent.mkdir(parents=True, exist_ok=True)
        tmp = target.with_suffix(".pdf.tmp")
        tmp.write_bytes(data)
        os.replace(tmp, target)
    return rel


# ---------------------------------------------------------------- metadata


def infer_models(*texts: str) -> list[str]:
    found: list[str] = []
    for text in texts:
        for pattern in _MODEL_PATTERNS:
            for match in pattern.findall(text):
                value = f"{match}i" if pattern.pattern.endswith(r"i\b") else match
                if value not in found:
                    found.append(value)
    return found


def _title_from_filename(path: Path) -> str:
    stem = re.sub(r"^\s*\d+\s+", "", path.stem)
    return re.sub(r"\s+", " ", stem).strip() or path.name


def build_meta(doc: PdfDocument, path: Path, override: Mapping[str, str]) -> dict[str, Any]:
    first_text = "\n".join(
        line for page in doc.pages[:3] if not (page.scanned or page.blank) for line in page.lines
    )
    models = infer_models(path.name, first_text)
    manufacturer = override.get("manufacturer") or (
        "TUG" if re.search(r"\bTUG\b", first_text + " " + path.name) else None
    )
    rev = re.search(r"\brev\.?\s*([A-Z])\b", path.name, re.IGNORECASE)
    title = override.get("title") or (doc.title if doc.title and len(doc.title) > 3 else None) or _title_from_filename(path)
    model_family = override.get("model_family") or (" & ".join(models) if models else None)
    tags: list[str] = []
    if manufacturer:
        tags.append(f"manufacturer:{manufacturer}")
    tags.extend(f"model:{m}" for m in models)
    if rev:
        tags.append(f"revision:{rev.group(1).upper()}")
    return {
        "title": title,
        "manufacturer": manufacturer,
        "model_family": model_family,
        "tags": tags,
    }


# ---------------------------------------------------------------- DB writes


@contextmanager
def _tx(conn: sqlite3.Connection):
    conn.execute("BEGIN IMMEDIATE")
    try:
        yield
        conn.execute("COMMIT")
    except BaseException:
        conn.execute("ROLLBACK")
        raise


def _purge_derived(conn: sqlite3.Connection, manual_id: str) -> None:
    """Remove derived index rows of one manual before a forced re-import (not user data).

    Embedding rows go first (rule: no stale vec0 rows). FTS rows are removed by the delete trigger.
    """
    chunk_ids = [int(r[0]) for r in conn.execute(
        "SELECT id FROM manual_chunks WHERE manual_id = ?", (manual_id,)
    ).fetchall()]
    delete_embeddings(conn, chunk_ids)
    conn.execute(
        "DELETE FROM chunk_tags WHERE chunk_id IN (SELECT id FROM manual_chunks WHERE manual_id = ?)",
        (manual_id,),
    )
    conn.execute("DELETE FROM manual_chunks WHERE manual_id = ?", (manual_id,))
    conn.execute("DELETE FROM manual_pages WHERE manual_id = ?", (manual_id,))


def _insert_manual(conn: sqlite3.Connection, manual_id: str, meta: dict, rel_path: str,
                   file_hash: str, page_count: int) -> None:
    now = _now()
    conn.execute(
        """INSERT INTO manuals (id, title, manufacturer, model_family, file_path, file_hash, page_count,
               format, status, imported_at, created_at, updated_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, 'pdf', 'imported', ?, ?, ?)""",
        (manual_id, meta["title"], meta["manufacturer"], meta["model_family"], rel_path, file_hash,
         page_count, now, now, now),
    )


def _write_content(conn: sqlite3.Connection, manual_id: str, pages: list[PageText],
                   chunks: list[ChunkDraft]) -> None:
    now = _now()
    page_section: dict[int, str | None] = {}
    for ch in chunks:
        page_section.setdefault(ch.page_start, ch.section)
    for page in pages:
        conn.execute(
            """INSERT INTO manual_pages (guid, manual_id, page_number, section, has_image, thumbnail_path, created_at)
               VALUES (?, ?, ?, ?, ?, NULL, ?)""",
            (str(uuid.uuid4()), manual_id, page.number, page_section.get(page.number),
             1 if page.has_images else 0, now),
        )


def _write_chunks(conn: sqlite3.Connection, manual_id: str, chunks: list[ChunkDraft],
                  base_tags: list[str], ocr_pages: set[int] | frozenset[int] = frozenset()) -> int:
    now = _now()
    for ch in chunks:
        cur = conn.execute(
            """INSERT INTO manual_chunks (guid, manual_id, page_start, page_end, section, chunk_kind, heading,
                   chunk_text, created_at, updated_at, deleted_at)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL)""",
            (str(uuid.uuid4()), manual_id, ch.page_start, ch.page_end, ch.section, ch.chunk_kind,
             ch.heading, ch.text, now, now),
        )
        chunk_id = cur.lastrowid
        tags = set(base_tags) | {f"kind:{k}" for k in ch.kinds if k != "text"}
        if ocr_pages.intersection(ch.pages or range(ch.page_start, ch.page_end + 1)):
            tags.add(OCR_TAG)
        for tag in sorted(tags):
            conn.execute("INSERT OR IGNORE INTO chunk_tags (chunk_id, tag) VALUES (?, ?)", (chunk_id, tag))
    return len(chunks)


def _update_manual(conn: sqlite3.Connection, manual_id: str, meta: dict, rel_path: str,
                   page_count: int) -> None:
    conn.execute(
        """UPDATE manuals SET title = ?, manufacturer = ?, model_family = ?, file_path = ?, page_count = ?,
               status = 'imported', imported_at = ?, updated_at = ?
           WHERE id = ?""",
        (meta["title"], meta["manufacturer"], meta["model_family"], rel_path, page_count, _now(), _now(), manual_id),
    )


# ---------------------------------------------------------------- embedding


def _embed_text(row: sqlite3.Row) -> str:
    return f"{row['heading']}\n{row['chunk_text']}" if row["heading"] else row["chunk_text"]


def embed_missing(
    conn: sqlite3.Connection,
    embedder: Embedder,
    *,
    job: IngestionJob | None = None,
    manual_id: str | None = None,
    batch_size: int = EMBED_BATCH,
) -> int:
    """Embed live chunks that have no `embeddings` row. Stops quietly when the embedder returns None.

    Returns the number of vectors written. Never writes a placeholder vector.
    """
    if not is_vec0_available(conn):
        if job is not None:
            job.add_note("sqlite-vec unavailable: chunks stored without vectors (BM25-only search)")
        return 0
    sql = (
        "SELECT c.id, c.heading, c.chunk_text FROM manual_chunks AS c "
        "WHERE c.deleted_at IS NULL AND NOT EXISTS (SELECT 1 FROM embeddings AS e WHERE e.chunk_id = c.id)"
    )
    params: list[Any] = []
    if manual_id is not None:
        sql += " AND c.manual_id = ?"
        params.append(manual_id)
    rows = conn.execute(sql + " ORDER BY c.id", params).fetchall()
    written = 0
    for start in range(0, len(rows), batch_size):
        if job is not None and not job.wait_if_paused():
            break
        batch = rows[start:start + batch_size]
        vectors = embedder([_embed_text(r) for r in batch])
        if vectors is None:  # model went away: keep what is written, leave the rest unembedded
            break
        if len(vectors) != len(batch):
            raise ValueError("embedder returned a different number of vectors than texts")
        with _tx(conn):
            for row, vec in zip(batch, vectors):
                conn.execute(
                    "INSERT INTO embeddings (chunk_id, embedding) VALUES (?, ?)",
                    (int(row["id"]), to_vec_blob(vec)),
                )
        written += len(batch)
        if job is not None:
            with job._lock:
                job.chunks_embedded += len(batch)
    return written


# ---------------------------------------------------------------- OCR

_AUTO_OCR = object()  # default for ingest_files(page_ocr=...): Windows OCR when it can run here


class _OcrSource:
    """Builds the OCR engine once per ingest run, and only when a scanned page needs it."""

    def __init__(self, page_ocr: PageOcr | None | object) -> None:
        self._given = page_ocr
        self._ocr: PageOcr | None = None
        self._reason: str | None = None
        self._ready = False

    def get(self) -> tuple[PageOcr | None, str | None]:
        if not self._ready:
            if self._given is _AUTO_OCR:
                self._ocr, self._reason = make_page_ocr()
            elif self._given is None:
                self._ocr, self._reason = None, "OCR is turned off"
            else:
                self._ocr = self._given  # type: ignore[assignment]
            self._ready = True
        return self._ocr, self._reason


def _ocr_pages(
    path: Path, pages: list[PageText], wanted: list[int], ocr_source: _OcrSource, job: IngestionJob
) -> tuple[list[PageText], list[int]]:
    """OCR the `wanted` scanned pages. Returns (pages with OCR text applied, page numbers read by OCR). Every wanted
    page that is still not searchable gets one job error saying why."""
    if not wanted:
        return pages, []
    ocr, reason = ocr_source.get()
    if ocr is None:
        for number in wanted:
            job.add_error(path.name, f"{OCR_PENDING_MESSAGE} ({reason})", page=number)
        return pages, []
    by_number = {p.number: p for p in pages}
    read: dict[int, PageText] = {}
    failed: dict[int, str] = {}
    for result in ocr.read_pages(path, wanted, keep_going=job.wait_if_paused):
        if result.number not in by_number:
            continue
        if result.error:
            failed[result.number] = result.error
            continue
        page = apply_ocr(by_number[result.number], result.lines)
        if page is not None:
            read[result.number] = page
        else:
            failed[result.number] = ""
    for number in wanted:
        if number in read:
            continue
        if number not in failed:
            job.add_error(path.name, OCR_STOPPED_MESSAGE, page=number)
        elif failed[number]:
            job.add_error(path.name, f"{OCR_FAILED_MESSAGE} ({failed[number]})", page=number)
        else:
            job.add_error(path.name, OCR_EMPTY_MESSAGE, page=number)
    if read:
        job.add_note(f"{path.name}: {len(read)} scanned page(s) read by {ocr.engine}")
    return [read.get(p.number, p) for p in pages], sorted(read)


def _pages_without_text(conn: sqlite3.Connection, manual_id: str) -> list[int]:
    """Pages of an imported manual that have an image and never had a chunk (live or deleted): the scanned pages an
    earlier import could not read. Pages whose chunks were deleted on purpose are left alone."""
    rows = conn.execute(
        """SELECT p.page_number FROM manual_pages AS p
           WHERE p.manual_id = ? AND p.has_image = 1
             AND NOT EXISTS (SELECT 1 FROM manual_chunks AS c
                             WHERE c.manual_id = p.manual_id AND c.page_start <= p.page_number
                               AND c.page_end >= p.page_number)
           ORDER BY p.page_number""",
        (manual_id,),
    ).fetchall()
    return [int(r[0]) for r in rows]


def _manual_base_tags(conn: sqlite3.Connection, manual_id: str) -> list[str]:
    rows = conn.execute(
        """SELECT DISTINCT t.tag FROM chunk_tags AS t JOIN manual_chunks AS c ON c.id = t.chunk_id
           WHERE c.manual_id = ? AND t.tag NOT LIKE 'kind:%' AND t.tag <> ?
           ORDER BY t.tag""",
        (manual_id, OCR_TAG),
    ).fetchall()
    return [str(r[0]) for r in rows]


def _backfill_ocr(
    path: Path, manual_id: str, *, conn: sqlite3.Connection, job: IngestionJob, embedder: Embedder | None,
    ocr_source: _OcrSource,
) -> int:
    """Read the scanned pages an earlier import left without text and add their chunks. Returns chunks created."""
    wanted = _pages_without_text(conn, manual_id)
    if not wanted:
        return 0
    ocr, _reason = ocr_source.get()
    if ocr is None:
        return 0  # nothing new can be read here; the earlier import already reported these pages
    blanks = [PageText(number=n, lines=[], has_images=True, scanned=True, blank=False) for n in wanted]
    pages, read = _ocr_pages(path, blanks, wanted, ocr_source, job)
    if not read:
        return 0
    chunks = chunk_pages([p for p in pages if p.ocr])
    base_tags = _manual_base_tags(conn, manual_id)
    with _tx(conn):
        created = _write_chunks(conn, manual_id, chunks, base_tags, set(read))
        first_section: dict[int, str | None] = {}
        for ch in chunks:
            first_section.setdefault(ch.page_start, ch.section)
        for number, section in first_section.items():
            if section:
                conn.execute(
                    "UPDATE manual_pages SET section = ? WHERE manual_id = ? AND page_number = ? AND section IS NULL",
                    (section, manual_id, number),
                )
        conn.execute("UPDATE manuals SET updated_at = ? WHERE id = ?", (_now(), manual_id))
    with job._lock:
        job.chunks_created += created
    if embedder is not None:
        embed_missing(conn, embedder, job=job, manual_id=manual_id)
    return created


# ---------------------------------------------------------------- orchestration


def _ingest_one(
    path: Path,
    *,
    conn: sqlite3.Connection,
    job: IngestionJob,
    embedder: Embedder | None,
    overrides: Mapping[str, str],
    force_reingest: bool,
    ocr_source: _OcrSource,
) -> str:
    size = path.stat().st_size
    if size > MAX_PDF_BYTES:
        raise ValueError(
            f"file too large ({size // (1024 * 1024)} MB; limit {MAX_PDF_BYTES // (1024 * 1024)} MB)"
        )
    data = path.read_bytes()
    file_hash = hashlib.sha256(data).hexdigest()
    existing = conn.execute(
        "SELECT id FROM manuals WHERE file_hash = ? AND deleted_at IS NULL ORDER BY imported_at LIMIT 1",
        (file_hash,),
    ).fetchone()
    if existing is not None and not force_reingest:
        added = _backfill_ocr(path, existing["id"], conn=conn, job=job, embedder=embedder, ocr_source=ocr_source)
        if added:
            job.add_note(f"{path.name}: already imported; {added} chunk(s) added from its scanned pages")
            return "ocr-added"
        job.add_note(f"{path.name}: duplicate of an existing manual (same file hash), skipped")
        return "duplicate"

    doc = read_pdf(path)
    for page in doc.pages:
        if page.extract_error:
            job.add_error(path.name, f"{TEXT_EXTRACT_FAILED_MESSAGE} ({page.extract_error})", page=page.number)
    read_pages, ocr_read = _ocr_pages(path, doc.pages, [p.number for p in doc.pages if p.scanned], ocr_source, job)
    pages = strip_repeated_lines(read_pages)
    chunks = chunk_pages(pages)
    meta = build_meta(doc, path, overrides)
    data_dir = _data_dir(conn)
    rel_path = _store_original(data, file_hash, data_dir)

    with _tx(conn):
        if existing is not None:
            manual_id = existing["id"]
            _purge_derived(conn, manual_id)
            _update_manual(conn, manual_id, meta, rel_path, doc.page_count)
            outcome = "reimported"
        else:
            manual_id = str(uuid.uuid4())
            _insert_manual(conn, manual_id, meta, rel_path, file_hash, doc.page_count)
            outcome = "imported"
        _write_content(conn, manual_id, pages, chunks)
        created = _write_chunks(conn, manual_id, chunks, meta["tags"], set(ocr_read))

    with job._lock:
        job.chunks_created += created
    if embedder is not None:
        embed_missing(conn, embedder, job=job, manual_id=manual_id)
    return outcome


def ingest_files(
    paths: Iterable[str | Path],
    *,
    conn: sqlite3.Connection,
    manual_overrides: Mapping[str, Mapping[str, str]] | None = None,
    embedder: Embedder | None = None,
    job: IngestionJob | None = None,
    force_reingest: bool = False,
    config: Config | None = None,
    page_ocr: PageOcr | None | object = _AUTO_OCR,
) -> IngestionJob:
    """Ingest PDFs synchronously and return the job (state succeeded/failed/cancelled).

    `page_ocr`: reads scanned pages. Default: Windows OCR when it can run here; None turns OCR off.

    `embedder`: document-side embedder. When None, one embedding-server probe decides; if the model is absent, chunks
    are stored with no vectors and `chunks_embedded` stays 0.
    `manual_overrides`: keyed by file name or full path -> {title, manufacturer, model_family}.
    """
    job = job or IngestionJob()
    register_job(job)
    job.state = "running"
    files, problems = expand_paths(paths)
    for name, message in problems:
        job.add_error(name, message)
    job.files_total = len(files)

    if embedder is None:
        embedder = make_document_embedder(config)
        if embedder is None:
            job.add_note("embedding model unavailable: chunks stored without vectors (BM25-only search)")

    overrides_all = manual_overrides or {}
    ocr_source = _OcrSource(page_ocr)
    succeeded = 0
    for path in files:
        if not job.wait_if_paused():
            break
        job.current_file = path.name
        overrides = overrides_all.get(path.name) or overrides_all.get(str(path)) or {}
        try:
            outcome = _ingest_one(
                path, conn=conn, job=job, embedder=embedder, overrides=overrides,
                force_reingest=force_reingest, ocr_source=ocr_source,
            )
            if outcome != "duplicate":
                succeeded += 1
        except Exception as exc:  # one bad file must not stop the batch
            job.add_error(path.name, f"{type(exc).__name__}: {exc}")
        with job._lock:
            job.files_done += 1
    with job._lock:
        job.current_file = None
    if job.cancel_requested:
        job.state = "cancelled"
    elif not files and problems:
        job.state = "failed"  # every requested path was invalid, unsupported, or not found
    elif files and succeeded == 0 and job.files_done == len(files):
        job.state = "failed"
    else:
        job.state = "succeeded"
    return job


def start_ingest_job(
    paths: Iterable[str | Path],
    *,
    db_path: str | Path,
    manual_overrides: Mapping[str, Mapping[str, str]] | None = None,
    force_reingest: bool = False,
    config: Config | None = None,
    embedder: Embedder | None = None,
) -> IngestionJob:
    """Run an ingest on a worker thread with its own connection. Poll with get_job(job.job_id)."""
    job = IngestionJob()
    register_job(job)
    path_list = [str(p) for p in paths]

    def _run() -> None:
        conn = connect(db_path)
        try:
            ingest_files(
                path_list, conn=conn, manual_overrides=manual_overrides, embedder=embedder,
                job=job, force_reingest=force_reingest, config=config,
            )
        except Exception as exc:
            job.add_error("", f"{type(exc).__name__}: {exc}")
            job.state = "failed"
        finally:
            conn.close()

    threading.Thread(target=_run, name=f"ingest-{job.job_id[:8]}", daemon=True).start()
    return job
