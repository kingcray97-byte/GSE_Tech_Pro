# ADR-003 — Embedding model: nomic-embed-text

- **Status:** Accepted; amended by ADR-012 (2026-11-19): same model and dimension, now a GGUF file served by `llama-server --embedding` instead of Ollama
- **Date:** 2026-11-15
- **Owner:** architect

## Context

Local hybrid search needs an embedding model that runs on CPU, in an 8 GB tablet, alongside the chat model, via Ollama. The choice fixes the vector dimension stored in `embeddings` (a `vec0` table), so changing it later means re-embedding.

## Decision

Use **`nomic-embed-text`** (v1.5): 137M params, **768-d**, ~274 MB, Apache-2.0, 8,192-token context. Use its `search_document:` / `search_query:` task prefixes.

## Alternatives considered

- **bge-m3** (567M, 1024-d, MIT): multilingual + long context, but ~1.2 GB and slower on CPU — overkill for English manuals.
- **mxbai-embed-large** (335M, 1024-d, Apache-2.0): good English quality but 512-token context forces small chunks.
- **all-minilm** (23M, 384-d, Apache-2.0): tiny/fast but weaker recall on technical prose.

## Consequences

- `embeddings.embedding` is `float[768]`; the vector dimension is fixed to 768.
- Search uses the two prefixes; missing them silently degrades quality.
- Changing models later requires a re-embed of `manual_chunks` (never mix dimensions).
- ~300 MB transient RAM during indexing/query — negligible on the budget.
