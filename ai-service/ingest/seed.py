"""Seed the manual library from `seed_manuals.json` (Phase 10C.5).

The seed list names GSE component manuals by path relative to a library root (on the dev machine
`Reference/GSE diagnostic apps`; at a shop, wherever the PDFs were copied). Files that are not present are
reported and skipped: nothing is downloaded and nothing outside the root is read.

    python -m ingest.seed --root "D:/Manuals/GSE diagnostic apps" [--db path] [--skip-slow]
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence

from ingest.job import IngestionJob
from ingest.pipeline import ingest_files
from rag.config import Config

SEED_FILE = Path(__file__).with_name("seed_manuals.json")
_REQUIRED = ("id", "path", "title", "manufacturer", "model_family", "questions")


@dataclass(frozen=True)
class SeedManual:
    id: str
    path: str
    title: str
    manufacturer: str
    model_family: str
    questions: tuple[dict[str, Any], ...]
    slow: bool = False

    def override(self) -> dict[str, str]:
        return {"title": self.title, "manufacturer": self.manufacturer, "model_family": self.model_family}


def load_seed(path: Path = SEED_FILE) -> tuple[dict[str, Any], list[SeedManual]]:
    """Loads and validates the seed list; raises ValueError on a malformed entry."""
    spec = json.loads(Path(path).read_text(encoding="utf-8"))
    manuals: list[SeedManual] = []
    seen: set[str] = set()
    for raw in spec.get("manuals", []):
        missing = [k for k in _REQUIRED if not raw.get(k)]
        if missing:
            raise ValueError(f"seed entry {raw.get('id')!r} is missing {missing}")
        rel = Path(raw["path"])
        if rel.is_absolute() or ".." in rel.parts:
            raise ValueError(f"seed entry {raw['id']!r}: path must be relative to the library root")
        if raw["id"] in seen:
            raise ValueError(f"duplicate seed id {raw['id']!r}")
        seen.add(raw["id"])
        for q in raw["questions"]:
            pages = q.get("expected_pages")
            if not q.get("question") or not pages or not all(isinstance(p, int) and p >= 1 for p in pages):
                raise ValueError(f"seed entry {raw['id']!r} has a malformed question: {q!r}")
        manuals.append(SeedManual(
            id=raw["id"], path=raw["path"], title=raw["title"], manufacturer=raw["manufacturer"],
            model_family=raw["model_family"], questions=tuple(raw["questions"]), slow=bool(raw.get("slow")),
        ))
    return spec, manuals


def resolve(manuals: Sequence[SeedManual], root: Path) -> tuple[list[tuple[SeedManual, Path]], list[SeedManual]]:
    """Splits the seed list into (present, missing) under `root`."""
    present, missing = [], []
    for m in manuals:
        p = Path(root) / m.path
        (present.append((m, p)) if p.is_file() else missing.append(m))
    return present, missing


def seed_library(
    root: Path,
    *,
    conn: sqlite3.Connection,
    config: Config | None = None,
    embedder=None,
    include_slow: bool = True,
    manuals: Sequence[SeedManual] | None = None,
) -> tuple[IngestionJob, list[tuple[SeedManual, Path]], list[SeedManual]]:
    """Ingests every present seed manual with its title/manufacturer/model overrides."""
    if manuals is None:
        _, manuals = load_seed()
    chosen = [m for m in manuals if include_slow or not m.slow]
    present, missing = resolve(chosen, root)
    overrides = {str(p): m.override() for m, p in present}
    job = ingest_files([p for _, p in present], conn=conn, manual_overrides=overrides, embedder=embedder, config=config)
    return job, present, missing


def main(argv: Sequence[str] | None = None) -> int:
    from rag.db import connect, default_db_path

    ap = argparse.ArgumentParser(prog="python -m ingest.seed", description=__doc__.splitlines()[0])
    ap.add_argument("--root", required=True, help="library root folder the seed paths are relative to")
    ap.add_argument("--db", default=None, help="SQLite database (default: GSE_TECH_PRO_DB or the app's database)")
    ap.add_argument("--skip-slow", action="store_true", help="skip manuals marked slow (very large PDFs)")
    args = ap.parse_args(argv)

    conn = connect(args.db or default_db_path())
    try:
        job, present, missing = seed_library(Path(args.root), conn=conn, include_slow=not args.skip_slow)
    finally:
        conn.close()
    print(f"seeded {len(present)} manual(s), {job.chunks_created} chunks; state={job.state}")
    for m in missing:
        print(f"  missing: {m.id} ({m.path})")
    for err in job.errors:
        print(f"  {err.file}: {err.message}" + (f" (page {err.page})" if getattr(err, "page", None) else ""))
    return 0 if job.state == "succeeded" else 1


if __name__ == "__main__":
    sys.exit(main())
