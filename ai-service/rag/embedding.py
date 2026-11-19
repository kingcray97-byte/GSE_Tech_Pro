"""Embeddings via local Ollama (nomic-embed-text, 768-d, ADR-003).

Never fabricates vectors. When Ollama or the model is absent, callers get `None` and must fall back
(BM25-only search; chunks stored without embedding rows). A dimension mismatch is a hard error.
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


def _names_match(wanted: str, present: str) -> bool:
    if ":" in wanted:
        return present == wanted or present == f"{wanted}:latest"
    return present.split(":")[0] == wanted


def is_embedding_available(config: Config | None = None) -> bool:
    """Probe Ollama for the configured embedding model. Returns False on any failure."""
    cfg = config or load_config()
    try:
        resp = httpx.get(f"{cfg.ollama.base_url}/api/tags", timeout=PROBE_TIMEOUT_S)
        resp.raise_for_status()
        names = [m.get("name", "") for m in resp.json().get("models", [])]
    except (httpx.HTTPError, ValueError):
        return False
    return any(_names_match(cfg.models.embedding, n) for n in names)


def embed_texts(
    texts: Sequence[str],
    task_prefix: str,
    config: Config | None = None,
) -> list[list[float]] | None:
    """Embed texts with task 'search_document' or 'search_query'.

    Returns None when Ollama or the model is unavailable (callers fall back). Raises
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
    }
    try:
        resp = httpx.post(
            f"{cfg.ollama.base_url}/api/embed",
            json=payload,
            timeout=httpx.Timeout(EMBED_TIMEOUT_S, connect=PROBE_TIMEOUT_S),
        )
        if resp.status_code != 200:
            return None
        vectors = resp.json().get("embeddings")
    except (httpx.HTTPError, ValueError):
        return None
    if not isinstance(vectors, list) or len(vectors) != len(texts):
        return None
    for vec in vectors:
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
