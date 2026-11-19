"""Soft delete for chunks and manuals. Both also remove the matching `embeddings` rows.

Why: vec0 applies `k` before relational filters, so a stale vector still occupies a KNN slot even after
its chunk is soft-deleted. Deleting the vector row keeps the KNN pool honest.
Soft delete keeps the relational row (deleted_at set) for sync tombstones; FTS rows are dropped by the
trigger-driven update and search filters on deleted_at as well.
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timezone
from typing import Iterable

from rag.db import delete_embeddings


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _delete_vectors(conn: sqlite3.Connection, chunk_ids: Iterable[int]) -> None:
    delete_embeddings(conn, chunk_ids)


def _soft_delete_chunk_ids(conn: sqlite3.Connection, chunk_ids: list[int]) -> int:
    if not chunk_ids:
        return 0
    _delete_vectors(conn, chunk_ids)
    now = _now()
    for cid in chunk_ids:
        conn.execute(
            "UPDATE manual_chunks SET deleted_at = ?, updated_at = ? WHERE id = ? AND deleted_at IS NULL",
            (now, now, cid),
        )
    return len(chunk_ids)


def _in_tx(conn: sqlite3.Connection, fn) -> int:
    conn.execute("BEGIN IMMEDIATE")
    try:
        result = fn()
        conn.execute("COMMIT")
        return result
    except BaseException:
        conn.execute("ROLLBACK")
        raise


def soft_delete_chunks(conn: sqlite3.Connection, chunk_ids: Iterable[int]) -> int:
    """Soft-delete chunks by integer id and remove their embeddings. Returns chunks affected."""
    ids = [int(i) for i in chunk_ids]
    return _in_tx(conn, lambda: _soft_delete_chunk_ids(conn, ids))


def soft_delete_manual(conn: sqlite3.Connection, manual_id: str) -> int:
    """Soft-delete a manual, all its live chunks, and their embeddings. Returns chunks affected."""

    def _work() -> int:
        ids = [
            int(r[0])
            for r in conn.execute(
                "SELECT id FROM manual_chunks WHERE manual_id = ? AND deleted_at IS NULL", (manual_id,)
            ).fetchall()
        ]
        affected = _soft_delete_chunk_ids(conn, ids)
        now = _now()
        conn.execute(
            "UPDATE manuals SET deleted_at = ?, updated_at = ? WHERE id = ? AND deleted_at IS NULL",
            (now, now, manual_id),
        )
        return affected

    return _in_tx(conn, _work)
