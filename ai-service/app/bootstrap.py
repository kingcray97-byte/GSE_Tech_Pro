"""Schema safety net for standalone runs (ADR-008 'DB sharing').

The C# data layer (GseTechPro.Data) is the schema authority and migrates on app open. The AI service never
authors DDL of its own: it applies the same versioned SQL files, but only the ones `schema_migrations` does
not already record. On an app-migrated database this is a no-op.

Each migration runs in its own transaction together with its schema_migrations row. Migration 3's vec0
statement is skipped when sqlite-vec is unavailable, mirroring SqliteMigrator; version 3 is still recorded
and the vector table is created on a later start that has sqlite-vec (see notices).
"""

from __future__ import annotations

import logging
import os
import sqlite3
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

from rag.db import connect

log = logging.getLogger("gse.ai.bootstrap")

MIGRATIONS: tuple[tuple[int, str], ...] = (
    (1, "001_Initial.sql"),
    (2, "002_Recordings.sql"),
    (3, "003_ManualsSearch.sql"),
    (4, "004_GseTroubleshooting.sql"),  # ADR-009: safety_warnings + readings.is_simulated (additive)
    (5, "005_RepairHistory.sql"),  # ADR-010 §8: work_orders.number + idx_wo_closed (additive)
)
VEC0_MIGRATION = 3
MIGRATIONS_ENV = "GSE_TECH_PRO_MIGRATIONS"
DEFAULT_MIGRATIONS_DIR = Path(__file__).resolve().parents[2] / "src" / "GseTechPro.Data" / "Migrations"


def resolve_migrations_dir(explicit: str | Path | None = None) -> Path:
    if explicit:
        return Path(explicit)
    env = os.environ.get(MIGRATIONS_ENV)
    return Path(env) if env else DEFAULT_MIGRATIONS_DIR


@dataclass
class SchemaReport:
    applied: list[int] = field(default_factory=list)
    notices: list[str] = field(default_factory=list)


def _applied_versions(conn: sqlite3.Connection) -> set[int]:
    rows = conn.execute("SELECT version FROM schema_migrations").fetchall()
    return {int(r[0]) for r in rows}


def _strip_vec0(sql: str) -> str:
    """Remove the `CREATE VIRTUAL TABLE ... USING vec0(...)` statement; keep everything else in the file."""
    kept = []
    for statement in sql.split(";"):
        if "using vec0" in statement.lower():
            continue
        kept.append(statement)
    return ";".join(kept)


def ensure_schema(db_path: str | Path, migrations_dir: str | Path | None = None) -> SchemaReport:
    """Create the database file if needed and apply pending migrations. Raises on real failures."""
    directory = resolve_migrations_dir(migrations_dir)
    conn = connect(db_path)
    report = SchemaReport()
    try:
        conn.execute(
            "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)"
        )
        applied = _applied_versions(conn)
        vec_ok = _vec_loadable(conn)
        for version, filename in MIGRATIONS:
            if version in applied:
                continue
            path = directory / filename
            if not path.is_file():
                raise FileNotFoundError(f"migration file missing: {filename} (set {MIGRATIONS_ENV})")
            sql = path.read_text(encoding="utf-8")
            if version == VEC0_MIGRATION and not vec_ok:
                sql = _strip_vec0(sql)
                report.notices.append("sqlite-vec unavailable: vector table not created (BM25-only search)")
            now = datetime.now(timezone.utc).isoformat(timespec="seconds")
            script = (
                "BEGIN IMMEDIATE;\n"
                f"{sql}\n;\n"
                f"INSERT INTO schema_migrations(version, applied_at) VALUES ({int(version)}, '{now}');\n"
                "COMMIT;"
            )
            try:
                conn.executescript(script)
            except sqlite3.Error:
                try:
                    conn.execute("ROLLBACK")
                except sqlite3.Error:
                    pass
                raise
            report.applied.append(version)
            log.info("applied migration %03d", version)
    finally:
        conn.close()
    for notice in report.notices:
        log.warning(notice)
    return report


def _vec_loadable(conn: sqlite3.Connection) -> bool:
    """True when sqlite-vec loads on this connection even if no vec0 table exists yet."""
    try:
        conn.execute("SELECT vec_version()").fetchone()
        return True
    except sqlite3.Error:
        return False
