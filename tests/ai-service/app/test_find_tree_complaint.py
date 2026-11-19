"""find_tree without a symptom_id: never serve an arbitrary tree (ADR-009 §3).

Synthetic content only (test values, not manual data). Uses a temporary SQLite database.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from ai_support import REPO  # noqa: F401  (puts ai-service/ on sys.path)
from app import troubleshooting as tb
from rag.db import connect
from troubleshooting_support import connect_rw, insert_synthetic, new_migrated_db


@pytest.fixture
def two_tree_db(tmp_path: Path) -> Path:
    path = new_migrated_db(tmp_path / "gse.db")
    conn = connect_rw(path)
    try:
        insert_synthetic(
            conn,
            models=[{"id": "eqm-b", "make": "Acme", "model": "B2"}, {"id": "eqm-c", "make": "Acme", "model": "C3"}],
            symptoms=[
                {"id": "sym-brk", "code": "BRK", "name": "Brakes grinding", "category": "brakes", "system": "Brakes",
                 "description": "Grinding noise when braking"},
                {"id": "sym-nc", "code": "NC", "name": "No crank", "category": "engine", "system": "Engine",
                 "description": "Starter does not turn the engine"},
            ],
            trees=[
                {"id": "tree-brk", "equipment_model_id": "eqm-b", "symptom_id": "sym-brk", "title": "Brake pad check",
                 "system": "Brakes", "status": "draft", "source": "handwritten"},
                {"id": "tree-nc", "equipment_model_id": "eqm-b", "symptom_id": "sym-nc", "title": "No-crank diagnosis",
                 "system": "Electrical", "status": "approved", "source": "handwritten"},
                {"id": "tree-solo", "equipment_model_id": "eqm-c", "symptom_id": None, "title": "Only tree",
                 "system": "Hydraulics", "status": "draft", "source": "handwritten"},
            ],
            steps=[
                {"id": "st-brk", "tree_id": "tree-brk", "step_order": 1, "instruction": "Inspect pads",
                 "safety_warning_ids": [], "next_on_pass": None, "next_on_fail": None, "terminal_result": "ok"},
                {"id": "st-nc", "tree_id": "tree-nc", "step_order": 1, "instruction": "Check battery",
                 "safety_warning_ids": [], "next_on_pass": None, "next_on_fail": None, "terminal_result": "ok"},
                {"id": "st-solo", "tree_id": "tree-solo", "step_order": 1, "instruction": "Check pump",
                 "safety_warning_ids": [], "next_on_pass": None, "next_on_fail": None, "terminal_result": "ok"},
            ],
        )
    finally:
        conn.close()
    return path


def _ro(path: Path):
    return connect(path, readonly=True, load_vec=False)


def test_unrelated_complaint_with_several_trees_and_no_symptom_returns_none(two_tree_db):
    conn = _ro(two_tree_db)
    try:
        assert tb.find_tree(conn, "eqm-b", None, "brakes grinding") is not None  # sanity: this one matches
        assert tb.find_tree(conn, "eqm-b", None, "hydraulic leak at the boom") is None
        assert tb.start_tree(conn, "eqm-b", None, "hydraulic leak at the boom") is None
    finally:
        conn.close()


def test_matching_complaint_returns_the_matching_tree(two_tree_db):
    conn = _ro(two_tree_db)
    try:
        assert tb.find_tree(conn, "eqm-b", None, "brakes grinding")["id"] == "tree-brk"
        assert tb.find_tree(conn, "eqm-b", None, "engine will not crank")["id"] == "tree-nc"
        tree, step = tb.start_tree(conn, "eqm-b", None, "BRAKES are grinding")
        assert tree["id"] == "tree-brk" and step["id"] == "st-brk"
    finally:
        conn.close()


def test_no_symptom_and_no_complaint_with_several_trees_returns_none(two_tree_db):
    conn = _ro(two_tree_db)
    try:
        assert tb.find_tree(conn, "eqm-b", None) is None
        assert tb.find_tree(conn, "eqm-b", None, "   ") is None
    finally:
        conn.close()


def test_no_symptom_single_tree_and_no_complaint_returns_that_tree(two_tree_db):
    conn = _ro(two_tree_db)
    try:
        assert tb.find_tree(conn, "eqm-c", None)["id"] == "tree-solo"
        assert tb.find_tree(conn, "eqm-c", None, None)["id"] == "tree-solo"
    finally:
        conn.close()


def test_single_tree_with_unrelated_complaint_returns_none(two_tree_db):
    conn = _ro(two_tree_db)
    try:
        assert tb.find_tree(conn, "eqm-c", None, "brakes grinding") is None
    finally:
        conn.close()


def test_explicit_symptom_is_unchanged_by_complaint_text(two_tree_db):
    conn = _ro(two_tree_db)
    try:
        assert tb.find_tree(conn, "eqm-b", "sym-nc", "brakes grinding")["id"] == "tree-nc"
    finally:
        conn.close()
