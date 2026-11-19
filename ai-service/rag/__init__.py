"""RAG package: config loader, SQLite helpers, embeddings (Ollama), hybrid manual search.

Owned by rag-ingest. Python is the sole writer of the RAG tables (manuals, manual_pages,
manual_chunks, chunk_tags, embeddings). Python never authors DDL.
"""

__version__ = "0.3.0"
