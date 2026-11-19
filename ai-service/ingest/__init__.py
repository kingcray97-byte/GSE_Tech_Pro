"""Manual ingestion: PDF text per page (pypdf), chunking, tagging, DB writes, optional embeddings.

Public entry points:
- ingest_files(paths, *, conn, ...) -> IngestionJob        (synchronous)
- start_ingest_job(paths, *, db_path, ...) -> IngestionJob (background thread, for POST /ingest)
- get_job(job_id) -> IngestionJob                          (for GET /ingest/status)
- embed_missing(conn, embedder, ...) -> int                (re-embed chunks that have no vector yet)
"""

from ingest.job import IngestionJob, get_job, register_job
from ingest.maintenance import soft_delete_chunks, soft_delete_manual
from ingest.pipeline import embed_missing, ingest_files, start_ingest_job

__all__ = [
    "IngestionJob",
    "embed_missing",
    "get_job",
    "ingest_files",
    "register_job",
    "soft_delete_chunks",
    "soft_delete_manual",
    "start_ingest_job",
]
