"""Embeddings via the tablet's embedding llama-server (nomic-embed-text GGUF, 768-d; ADR-003 amended by ADR-012).

The server is `llama-server --embedding --pooling mean` on 127.0.0.1 (`llama_server.embedding.port`). Requests use its
OpenAI-compatible `POST /v1/embeddings`. Embeddings never leave the tablet, whatever the chat route (ADR-013 rule 8).

Never fabricates vectors. When the server is down or still loading, callers get `None` and must fall back (BM25-only
search; chunks stored without embedding rows). A dimension mismatch is a hard error.
"""

from __future__ import annotations

import math
from typing import Callable, Sequence

import httpx

from rag.config import Config, load_config

# ADR-003: nomic-embed-text task prefixes (the trailing space is part of the prefix).
TASK_PREFIXES = {
    "search_document": "search_document: ",
    "search_query": "search_query: ",
}

PROBE_TIMEOUT_S = 2.0
EMBED_TIMEOUT_S = 120.0

Embedder = Callable[[list[str]], "list[list[float]] | None"]


class EmbeddingDimensionError(ValueError):
    """The model returned vectors whose length differs from config (dimensions must never mix)."""


def is_embedding_available(config: Config | None = None) -> bool:
    """True when the embedding llama-server answers /health with 200 (model loaded). False on anything else."""
    cfg = config or load_config()
    try:
        resp = httpx.get(f"{cfg.llama_server.embedding.base_url}/health", timeout=PROBE_TIMEOUT_S)
    except httpx.HTTPError:
        return False
    return resp.status_code == 200


def _vectors_from(body: object, count: int) -> list | None:
    """OpenAI shape: {"data": [{"index": i, "embedding": [...]}, ...]}. Ordered by index."""
    if not isinstance(body, dict):
        return None
    data = body.get("data")
    if not isinstance(data, list) or len(data) != count:
        return None
    try:
        rows = sorted(data, key=lambda d: int(d.get("index", 0)))
        return [row["embedding"] for row in rows]
    except (AttributeError, KeyError, TypeError, ValueError):
        return None


def embed_texts(
    texts: Sequence[str],
    task_prefix: str,
    config: Config | None = None,
) -> list[list[float]] | None:
    """Embed texts with task 'search_document' or 'search_query'.

    Returns None when the embedding server is unavailable (callers fall back). Raises
    EmbeddingDimensionError when the model returns vectors of the wrong size.
    """
    if task_prefix not in TASK_PREFIXES:
        raise ValueError(f"task_prefix must be one of {sorted(TASK_PREFIXES)}")
    cfg = config or load_config()
    if not texts:
        return []
    payload = {
        "model": cfg.models.embedding,
        "input": [TASK_PREFIXES[task_prefix] + t for t in texts],
        "encoding_format": "float",
    }
    try:
        resp = httpx.post(
            f"{cfg.llama_server.embedding.base_url}/v1/embeddings",
            json=payload,
            timeout=httpx.Timeout(EMBED_TIMEOUT_S, connect=PROBE_TIMEOUT_S),
        )
        if resp.status_code != 200:
            return None
        vectors = _vectors_from(resp.json(), len(texts))
    except (httpx.HTTPError, ValueError):
        return None
    if vectors is None:
        return None
    for vec in vectors:
        if not isinstance(vec, list):
            return None
        if len(vec) != cfg.models.embedding_dim:
            raise EmbeddingDimensionError(
                f"embedding model returned {len(vec)}-d vectors; config expects {cfg.models.embedding_dim}"
            )
    out = [[float(x) for x in vec] for vec in vectors]
    if not all(math.isfinite(x) for vec in out for x in vec):
        raise ValueError("embedding model returned non-finite values (NaN or inf)")
    return out


def _make_embedder(task: str, config: Config | None) -> Embedder | None:
    cfg = config or load_config()
    if not is_embedding_available(cfg):  # one probe per job, not per batch
        return None

    def _embed(texts: list[str]) -> list[list[float]] | None:
        return embed_texts(texts, task, cfg)

    return _embed


def make_document_embedder(config: Config | None = None) -> Embedder | None:
    """Document-side embedder (search_document prefix), or None when the model is unavailable."""
    return _make_embedder("search_document", config)


def make_query_embedder(config: Config | None = None) -> Embedder | None:
    """Query-side embedder (search_query prefix), or None when the model is unavailable."""
    return _make_embedder("search_query", config)
