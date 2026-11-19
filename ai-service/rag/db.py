"""SQLite connection helpers shared by the AI service (ADR-008 DB sharing rules).

- One file `gse-tech-pro.db`, opened in WAL mode by both runtimes.
- Python never authors DDL (C# GseTechPro.Data owns migrations). Python is the sole writer of the RAG tables.
- Connections use autocommit (isolation_level=None); writers manage transactions explicitly.
"""

from __future__ import annotations

import math
import os
import sqlite3
import struct
from pathlib import Path
from typing import Iterable

from rag.config import VEC0_DIM


def default_db_path() -> Path:
    """Same location as the app (DatabaseOptions.DefaultDatabasePath):
    %LOCALAPPDATA%\\GseTechPro\\gse-tech-pro.db. `GSE_TECH_PRO_DB` overrides it (tests, standalone runs).
    """
    override = os.environ.get("GSE_TECH_PRO_DB")
    if override:
        return Path(override)
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "GseTechPro" / "gse-tech-pro.db"


# Bundled sqlite-vec DLL shipped with the .NET data layer (same binary both runtimes use, ADR §2.4).
# Used only when the `sqlite_vec` Python wheel is unavailable (e.g. no prebuilt wheel for a new Python).
BUNDLED_VEC0 = Path(__file__).resolve().parents[2] / "src" / "GseTechPro.Data" / "Native" / "win-x64" / "vec0.dll"
_IN_BATCH = 500


def _vec0_candidates() -> list[Path]:
    env = os.environ.get("GSE_VEC0_PATH")
    out = [Path(env)] if env else []
    out.append(BUNDLED_VEC0)
    return out


def _load_vec(conn: sqlite3.Connection) -> None:
    try:
        conn.enable_load_extension(True)
    except (AttributeError, sqlite3.OperationalError):
        return  # extension loading disabled: vec0 is unusable on this connection
    try:
        try:
            import sqlite_vec  # type: ignore

            sqlite_vec.load(conn)
            return
        except ImportError:
            pass
        except sqlite3.OperationalError:
            pass
        for candidate in _vec0_candidates():
            if candidate.exists():
                try:
                    conn.load_extension(str(candidate))
                    return
                except sqlite3.OperationalError:
                    continue
    finally:
        try:
            conn.enable_load_extension(False)
        except (AttributeError, sqlite3.Error):
            pass


def connect(db_path: str | Path, readonly: bool = False, *, load_vec: bool = True) -> sqlite3.Connection:
    """Open the shared database.

    rw: sets WAL, busy_timeout=5000, foreign_keys=ON. ro: `mode=ro` URI and no writing pragmas.
    sqlite-vec is loaded when importable (`load_vec=True`, the default); pass `load_vec=False` for
    connections that never touch vectors (e.g. a `/health` DB probe). Check with `is_vec0_available`.
    """
    path = Path(db_path)
    if readonly:
        if not path.exists():
            raise FileNotFoundError(f"database not found: {path}")
        conn = sqlite3.connect(f"{path.as_uri()}?mode=ro", uri=True, isolation_level=None, timeout=5.0)
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        conn = sqlite3.connect(str(path), isolation_level=None, timeout=5.0)
    conn.row_factory = sqlite3.Row
    if load_vec:
        _load_vec(conn)
    conn.execute("PRAGMA busy_timeout = 5000")
    if not readonly:
        conn.execute("PRAGMA foreign_keys = ON")
        conn.execute("PRAGMA journal_mode = WAL").fetchone()
    return conn


def is_vec0_available(conn: sqlite3.Connection) -> bool:
    """True when sqlite-vec is loaded on this connection AND the `embeddings` vec0 table exists."""
    try:
        row = conn.execute(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'embeddings'"
        ).fetchone()
        if row is None or row[0] is None or "vec0" not in row[0].lower():
            return False
        conn.execute("SELECT vec_version()").fetchone()
        return True
    except sqlite3.Error:
        return False


def to_vec_blob(vector: list[float]) -> bytes:
    """Serialize a float32 vector for vec0 (same bytes as sqlite_vec.serialize_float32).

    Rejects NaN and +/-inf: a non-finite component silently poisons KNN ranking (matches the C#
    EmbeddingRepository.ValidateVector rule).
    """
    if len(vector) != VEC0_DIM:
        raise ValueError(f"vector length {len(vector)} != vec0 dimension {VEC0_DIM}")
    values = [float(x) for x in vector]
    if not all(math.isfinite(x) for x in values):
        raise ValueError("embedding contains non-finite values (NaN or inf)")
    return struct.pack(f"{VEC0_DIM}f", *values)


def delete_embeddings(conn: sqlite3.Connection, chunk_ids: Iterable[int]) -> int:
    """Delete vec0 rows for the given chunk ids, in batches of IN (...). Returns ids requested.

    No-op when vec0 is unavailable. Callers use this before deleting or soft-deleting chunks so stale
    vectors never occupy KNN slots (vec0 applies k before relational filters).
    """
    ids = [int(i) for i in chunk_ids]
    if not ids or not is_vec0_available(conn):
        return 0
    for start in range(0, len(ids), _IN_BATCH):
        batch = ids[start:start + _IN_BATCH]
        marks = ", ".join("?" for _ in batch)
        conn.execute(f"DELETE FROM embeddings WHERE chunk_id IN ({marks})", batch)
    return len(ids)
