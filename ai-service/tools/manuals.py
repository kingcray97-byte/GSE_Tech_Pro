"""Manual search: the one place that runs rag.search for both POST /search/manuals and the search_manuals tool.

Embedding is probed through the configured embedding llama-server (never a default host). When vectors are unavailable
the search runs BM25-only and reports fallback=True (ADR-008 degradation rule 3).
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Literal, Optional, Sequence

from pydantic import BaseModel, Field

from rag.db import connect, is_vec0_available
from rag.embedding import Embedder
from rag.search import hybrid_search
from tools.registry import ToolContext, ToolResult, ToolSource, ToolSpec

ChunkKindCode = Literal[
    "text", "table", "procedure", "specs", "troubleshooting", "wiring", "torque", "diagram_caption"
]


OCR_PASSAGE_NOTE = (
    "(scanned page, text read by OCR: it may misread characters; if you quote a number from it, tell the "
    "technician to check it against the page)"
)


def no_vectors(_texts: list[str]) -> None:
    """Embedder stand-in: 'no vectors right now'. hybrid_search then ranks BM25-only and reports fallback=True."""
    return None


def run_manual_search(
    db_path: Path,
    query: str,
    top_k: int = 5,
    *,
    query_embedder: Optional[Embedder] = None,
    manual_id: Optional[str] = None,
    chunk_kinds: Optional[Sequence[str]] = None,
    include_text: bool = True,
) -> dict[str, Any]:
    """Returns {"results": [...], "fallback": bool}. Raises ValueError for bad input, sqlite3.Error for DB trouble.

    `query_embedder` comes from the service (cached llama-server probe, see ServiceState.query_embedder). It is never
    probed here: when None, the search is BM25-only.
    """
    conn = connect(db_path, readonly=True)
    try:
        embedder = (query_embedder or no_vectors) if is_vec0_available(conn) else no_vectors
        return hybrid_search(
            query,
            top_k,
            manual_id=manual_id,
            chunk_kinds=list(chunk_kinds) if chunk_kinds else None,
            include_text=include_text,
            conn=conn,
            query_embedder=embedder,
        )
    finally:
        conn.close()


class SearchManualsArgs(BaseModel):
    query: str = Field(min_length=1, max_length=300, description="What the technician is looking for.")
    top_k: int = Field(default=5, ge=1, le=10, description="How many manual passages to return (1-10).")
    manual_id: Optional[str] = Field(default=None, description="Limit to one manual id, or omit.")
    chunk_kinds: Optional[list[ChunkKindCode]] = Field(
        default=None, description="Limit to kinds such as specs, torque, procedure, troubleshooting."
    )


def _search_manuals(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, SearchManualsArgs)
    if ctx.db_path is None:
        return ToolResult(False, "Manual search is unavailable: no database is configured.")
    out = run_manual_search(
        ctx.db_path,
        args.query,
        args.top_k,
        query_embedder=ctx.embedder,
        manual_id=args.manual_id,
        chunk_kinds=args.chunk_kinds,
        include_text=True,
    )
    results = out["results"]
    if not results:
        return ToolResult(True, "No matching manual passages. Not found in the manuals.")
    lines = []
    sources = []
    for i, r in enumerate(results, start=1):
        snippet = " ".join(str(r.get("text") or "").split())[:400]
        ocr = f" {OCR_PASSAGE_NOTE}" if r.get("ocr") else ""
        lines.append(f"[{i}] {r['citation']}{ocr}: {snippet}")
        sources.append(
            ToolSource(
                kind="manual-page",
                display_text=r["citation"],
                manual_id=r["manual_id"],
                page_start=r["page_start"],
            )
        )
    note = " (text-only ranking: no vectors)" if out["fallback"] else ""
    return ToolResult(True, f"Manual passages{note}:\n" + "\n".join(lines), tuple(sources))


SEARCH_MANUALS = ToolSpec(
    name="search_manuals",
    description=(
        "Search the imported service manuals. Use for any spec, torque, pressure, procedure, or warning. "
        "Quote the citation shown with each passage."
    ),
    args_model=SearchManualsArgs,
    handler=_search_manuals,
)
