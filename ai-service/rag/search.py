"""Hybrid manual search: BM25 (FTS5) + vec0 KNN, merged with Reciprocal Rank Fusion.

Returns a dict shaped like ADR-008 `SearchManualsResponse`:
    {"results": [ {chunk_id, manual_id, manual_title, page_start, page_end, section, text, score, citation} ],
     "fallback": bool}
`fallback` is True when vectors were not used (no sqlite-vec / embedding model unavailable): BM25-only ranking.

Safety rules:
- User text never reaches FTS5 MATCH raw: each token is extracted with \\w+ and double-quoted.
- Soft-deleted chunks and manuals are always excluded.
- vec0 applies `k` before relational filters, so the KNN pool is filtered afterwards and
  stale embeddings rows are removed by the ingest/soft-delete code paths.
"""

from __future__ import annotations

import re
import sqlite3
from typing import Any, Iterable, Sequence

from rag.db import is_vec0_available, to_vec_blob
from rag.embedding import Embedder, make_query_embedder
from rag.rrf import RRF_K, rrf_merge

CHUNK_KINDS = frozenset(
    {"text", "table", "procedure", "specs", "troubleshooting", "wiring", "torque", "diagram_caption"}
)
MAX_TOP_K = 50
MIN_KNN_POOL = 50

# Small English stopword list: keeps "the/how/do" from matching every page. Never used for escaping.
STOPWORDS = frozenset(
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how",
        "i", "if", "in", "is", "it", "its", "my", "of", "on", "or", "should", "the", "this", "to",
        "we", "what", "when", "where", "which", "with", "you", "there", "me",
    }
)

_TOKEN_RE = re.compile(r"\w+")


def build_fts_match(query: str) -> str | None:
    """Turn free text into a safe FTS5 MATCH expression.

    Tokens come from \\w+ (so no quotes, hyphens, parens, or operators survive); each is double-quoted
    and OR-joined. Returns None when there is nothing searchable.
    """
    tokens = [t.lower() for t in _TOKEN_RE.findall(query or "")]
    kept = [t for t in tokens if t not in STOPWORDS] or tokens
    if not kept:
        return None
    unique = dict.fromkeys(kept)
    return " OR ".join(f'"{t}"' for t in unique)


def _validate_kinds(chunk_kinds: Iterable[str] | None) -> list[str] | None:
    if chunk_kinds is None:
        return None
    kinds = list(chunk_kinds)
    bad = [k for k in kinds if k not in CHUNK_KINDS]
    if bad:
        raise ValueError(f"unknown chunk kind(s): {bad}")
    return kinds


def _filter_sql(manual_id: str | None, kinds: list[str] | None) -> tuple[str, list[Any]]:
    clauses = ["c.deleted_at IS NULL", "m.deleted_at IS NULL"]
    params: list[Any] = []
    if manual_id is not None:
        clauses.append("c.manual_id = ?")
        params.append(manual_id)
    if kinds:
        clauses.append(f"c.chunk_kind IN ({', '.join('?' for _ in kinds)})")
        params.extend(kinds)
    return " AND ".join(clauses), params


def _bm25_ids(
    conn: sqlite3.Connection,
    match: str,
    manual_id: str | None,
    kinds: list[str] | None,
    pool: int,
) -> list[int]:
    where, params = _filter_sql(manual_id, kinds)
    sql = f"""
        SELECT c.id AS id
        FROM manual_chunks_fts
        JOIN manual_chunks AS c ON c.id = manual_chunks_fts.rowid
        JOIN manuals AS m ON m.id = c.manual_id
        WHERE manual_chunks_fts MATCH ? AND {where}
        ORDER BY bm25(manual_chunks_fts, 1.0, 2.0, 0.5)
        LIMIT ?
    """
    rows = conn.execute(sql, [match, *params, pool]).fetchall()
    return [int(r["id"]) for r in rows]


def _vector_ids(
    conn: sqlite3.Connection,
    query_vector: list[float],
    manual_id: str | None,
    kinds: list[str] | None,
    pool: int,
) -> list[int]:
    knn = conn.execute(
        "SELECT chunk_id FROM embeddings WHERE embedding MATCH ? AND k = ? ORDER BY distance",
        (to_vec_blob(query_vector), pool),
    ).fetchall()
    candidates = [int(r["chunk_id"]) for r in knn]
    if not candidates:
        return []
    where, params = _filter_sql(manual_id, kinds)
    placeholders = ", ".join("?" for _ in candidates)
    live = {
        int(r["id"])
        for r in conn.execute(
            f"SELECT c.id AS id FROM manual_chunks AS c JOIN manuals AS m ON m.id = c.manual_id "
            f"WHERE c.id IN ({placeholders}) AND {where}",
            [*candidates, *params],
        ).fetchall()
    }
    return [cid for cid in candidates if cid in live]


def _citation(title: str, page_start: int, page_end: int) -> str:
    if page_start == page_end:
        return f"{title}, page {page_start}"
    return f"{title}, pages {page_start}-{page_end}"


def hybrid_search(
    query: str,
    top_k: int = 5,
    *,
    manual_id: str | None = None,
    chunk_kinds: Sequence[str] | None = None,
    include_text: bool = True,
    conn: sqlite3.Connection,
    query_embedder: Embedder | None = None,
) -> dict[str, Any]:
    """Hybrid search over live manual chunks.

    `query_embedder` (search_query prefix) is optional. When omitted, the default query embedder is
    probed once; if Ollama/the model is absent the search runs BM25-only and reports fallback=True.
    """
    if not isinstance(query, str) or not query.strip():
        raise ValueError("query must be a non-empty string")
    if not (1 <= top_k <= MAX_TOP_K):
        raise ValueError(f"top_k must be between 1 and {MAX_TOP_K}")
    kinds = _validate_kinds(chunk_kinds)
    pool = max(MIN_KNN_POOL, top_k * 10)

    match = build_fts_match(query)
    lexical = _bm25_ids(conn, match, manual_id, kinds, pool) if match else []

    vector_ranked: list[int] = []
    fallback = True
    if is_vec0_available(conn):
        embedder = query_embedder if query_embedder is not None else make_query_embedder()
        if embedder is not None:
            vectors = embedder([query])
            if vectors:
                vector_ranked = _vector_ids(conn, vectors[0], manual_id, kinds, pool)
                fallback = False

    rankings = [r for r in (lexical, vector_ranked) if r]
    if not rankings:
        return {"results": [], "fallback": fallback}

    merged = rrf_merge(rankings, k=RRF_K)[:top_k]
    # Normalize so the best possible fused score (rank 1 in every active list) is 1.0.
    max_score = (2 if not fallback else 1) / (RRF_K + 1)
    scores = {cid: min(1.0, score / max_score) for cid, score in merged}

    ids = [cid for cid, _ in merged]
    placeholders = ", ".join("?" for _ in ids)
    rows = conn.execute(
        f"""
        SELECT c.id, c.guid, c.manual_id, m.title AS manual_title, c.page_start, c.page_end,
               c.section, c.chunk_text
        FROM manual_chunks AS c JOIN manuals AS m ON m.id = c.manual_id
        WHERE c.id IN ({placeholders})
        """,
        ids,
    ).fetchall()
    by_id = {int(r["id"]): r for r in rows}

    results: list[dict[str, Any]] = []
    for cid in ids:
        r = by_id.get(cid)
        if r is None:  # soft-deleted between ranking and load; cannot happen inside one connection snapshot
            continue
        results.append(
            {
                "chunk_id": r["guid"],
                "manual_id": r["manual_id"],
                "manual_title": r["manual_title"],
                "page_start": r["page_start"],
                "page_end": r["page_end"],
                "section": r["section"],
                "text": r["chunk_text"] if include_text else None,
                "score": round(scores[cid], 4),
                "citation": _citation(r["manual_title"], r["page_start"], r["page_end"]),
            }
        )
    return {"results": results, "fallback": fallback}


def get_manual_page(conn: sqlite3.Connection, manual_id: str, page_number: int) -> dict[str, Any] | None:
    """Open-to-page helper for the viewer/`get_manual_page` tool. Live manuals only."""
    manual = conn.execute(
        "SELECT id, title, file_path, page_count FROM manuals WHERE id = ? AND deleted_at IS NULL",
        (manual_id,),
    ).fetchone()
    if manual is None:
        return None
    rows = conn.execute(
        """
        SELECT c.guid, c.page_start, c.page_end, c.section, c.chunk_kind, c.heading, c.chunk_text
        FROM manual_chunks AS c
        WHERE c.manual_id = ? AND c.deleted_at IS NULL AND c.page_start <= ? AND c.page_end >= ?
        ORDER BY c.page_start, c.id
        """,
        (manual_id, page_number, page_number),
    ).fetchall()
    return {
        "manual_id": manual["id"],
        "manual_title": manual["title"],
        "file_path": manual["file_path"],  # relative to the database folder
        "page_number": page_number,
        "page_count": manual["page_count"],
        "chunks": [
            {
                "chunk_id": r["guid"],
                "page_start": r["page_start"],
                "page_end": r["page_end"],
                "section": r["section"],
                "chunk_kind": r["chunk_kind"],
                "heading": r["heading"],
                "text": r["chunk_text"],
            }
            for r in rows
        ],
    }
