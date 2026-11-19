"""Test helpers: build a migrated temp database with troubleshooting content (synthetic rows or the real package).

The real package is data/troubleshooting/*.json (owned by gse-domain-expert, read-only here). The C# seeder is the
production writer; this loader only mirrors its columns for tests. Synthetic test values are NOT manual specs.
"""

from __future__ import annotations

import json
import sqlite3
import uuid
from pathlib import Path
from typing import Any, Iterable, Optional

import ai_support  # noqa: F401  (puts ai-service/ on sys.path)
from app.bootstrap import ensure_schema

REPO = ai_support.REPO
PACKAGE = REPO / "data" / "troubleshooting"
DTC_DIR = REPO / "data" / "dtc"
TS = "2026-11-16T00:00:00Z"

_MODEL_COLS = ("id", "make", "model", "category", "engine_make", "engine_model", "transmission", "fuel_type",
               "system_notes", "default_unit_system")
_WARNING_COLS = ("id", "category", "text", "source_citation")
_SYMPTOM_COLS = ("id", "code", "name", "category", "system", "description")
_TP_COLS = ("id", "equipment_model_id", "name", "system", "location", "reading_type", "expected_unit",
            "spec_min", "spec_max", "spec_source", "procedure_ref", "safety_warning_ids")
_TREE_COLS = ("id", "equipment_model_id", "symptom_id", "title", "system", "status", "source")
_STEP_COLS = ("id", "tree_id", "step_order", "instruction", "question", "reading_type", "test_point_id",
              "spec_min", "spec_max", "spec_source", "safety_warning_ids", "next_on_pass", "next_on_fail",
              "terminal_result")
_DTC_COLS = ("protocol", "code", "spn", "fmi", "description", "system", "severity", "likely_causes", "source_citation")


def _insert(conn: sqlite3.Connection, table: str, cols: Iterable[str], row: dict[str, Any], stamps: bool = True) -> None:
    names = list(cols)
    values = [row.get(c) for c in names]
    if stamps:
        names += ["created_at", "updated_at"]
        values += [TS, TS]
    marks = ", ".join("?" for _ in names)
    conn.execute(f"INSERT INTO {table} ({', '.join(names)}) VALUES ({marks})", values)


def _ids(value: Optional[list[str]]) -> Optional[str]:
    return None if value is None else json.dumps(list(value))


def _with_json_ids(row: dict[str, Any]) -> dict[str, Any]:
    out = dict(row)
    if "safety_warning_ids" in out and isinstance(out["safety_warning_ids"], list):
        out["safety_warning_ids"] = _ids(out["safety_warning_ids"])
    return out


def new_migrated_db(path: Path) -> Path:
    ensure_schema(path)
    return path


def connect_rw(path: Path) -> sqlite3.Connection:
    conn = sqlite3.connect(str(path), isolation_level=None)
    conn.row_factory = sqlite3.Row
    return conn


def load_package(conn: sqlite3.Connection, package: Path = PACKAGE) -> None:
    """Insert every row of data/troubleshooting/*.json (flat arrays; trees embed their steps)."""
    def _read(name: str) -> list[dict[str, Any]]:
        return json.loads((package / name).read_text(encoding="utf-8"))

    for row in _read("equipment_models.json"):
        _insert(conn, "equipment_models", _MODEL_COLS, row)
    for row in _read("safety_warnings.json"):
        _insert(conn, "safety_warnings", _WARNING_COLS, row)
    for row in _read("symptoms.json"):
        _insert(conn, "symptoms", _SYMPTOM_COLS, {**row, "is_standard": 1})
    for row in _read("test_points.json"):
        _insert(conn, "test_points", _TP_COLS, _with_json_ids(row))
    for tree in _read("trees.json"):
        _insert(conn, "troubleshooting_trees", _TREE_COLS, tree)
        for step in tree["steps"]:
            _insert(conn, "troubleshooting_steps", _STEP_COLS, {**_with_json_ids(step), "tree_id": tree["id"]})


def load_dtc_package(conn: sqlite3.Connection, package: Path = DTC_DIR) -> int:
    """Insert the three DTC JSON files into dtc_library (only the (protocol, code) key and descriptive fields)."""
    count = 0
    for name in ("j1939.json", "j1587.json", "obd2.json"):
        for row in json.loads((package / name).read_text(encoding="utf-8")):
            conn.execute(
                "INSERT INTO dtc_library (id, protocol, code, spn, fmi, description, system, severity, "
                "likely_causes, source_citation, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
                (str(uuid.uuid4()), row["protocol"], row["code"], row.get("spn"), row.get("fmi"),
                 row.get("description"), row.get("system"), row.get("severity"), row.get("likely_causes"),
                 row.get("source_citation"), TS, TS),
            )
            count += 1
    return count


def insert_synthetic(conn: sqlite3.Connection, *, models=(), warnings=(), symptoms=(), points=(), trees=(), steps=()) -> None:
    """Insert hand-written rows (test values only). Each argument is a list of dicts with the table's columns."""
    for row in models:
        _insert(conn, "equipment_models", _MODEL_COLS, row)
    for row in warnings:
        _insert(conn, "safety_warnings", _WARNING_COLS, row)
    for row in symptoms:
        _insert(conn, "symptoms", _SYMPTOM_COLS, {**row, "is_standard": 1})
    for row in points:
        _insert(conn, "test_points", _TP_COLS, _with_json_ids(row))
    for row in trees:
        _insert(conn, "troubleshooting_trees", _TREE_COLS, row)
    for row in steps:
        _insert(conn, "troubleshooting_steps", _STEP_COLS, _with_json_ids(row))
