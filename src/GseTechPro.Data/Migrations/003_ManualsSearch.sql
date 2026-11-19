-- GSE Tech Pro manual search, ARCHITECTURE.md sections 2.2-2.4.
-- Additive migration 3. The connection factory attempts to load sqlite-vec before this runs.
-- If vec0 is unavailable, SqliteMigrator records that condition, skips only the vec0 DDL,
-- and still records version 3. DatabaseInitializer retries the CREATE on later startups.

CREATE INDEX IF NOT EXISTS idx_manuals_file_hash ON manuals(file_hash);

CREATE VIRTUAL TABLE IF NOT EXISTS embeddings USING vec0(
  chunk_id INTEGER PRIMARY KEY,
  embedding float[768]
);
