"""Integrity of the REAL troubleshooting package (data/troubleshooting/*.json) against the ADR-009 invariants.

Read-only: the package is owned by gse-domain-expert. These tests fail loudly when authored content breaks a rule
(dangling reference, unit that does not match its reading type, step spec without a test point, uncited spec).
They do not pin wording, so editing a sentence never breaks them.
"""

from __future__ import annotations

import sqlite3
from pathlib import Path

import pytest

from ai_support import REPO  # noqa: F401  (puts ai-service/ on sys.path)
from app import troubleshooting as tb
from app import units
from app.bootstrap import ensure_schema
from rag.db import connect
from troubleshooting_support import PACKAGE, connect_rw, load_dtc_package, load_package, new_migrated_db

VALID_WARNING_CATEGORIES = {"hydraulic_pressure", "lockout_tagout", "high_voltage", "aircraft_proximity", "general"}
VALID_TREE_STATUS = {"draft", "approved", "retired"}
VALID_TREE_SOURCE = {"handwritten", "ai_generated", "history_improved"}
VALID_READING_TYPES = {"hydraulic_pressure", "flow", "temperature", "voltage", "fuel_pressure", "current", "other"}


@pytest.fixture(scope="module")
def content_path(tmp_path_factory) -> Path:
    path = new_migrated_db(tmp_path_factory.mktemp("content") / "content.db")
    conn = connect_rw(path)
    try:
        load_package(conn)
        load_dtc_package(conn)
    finally:
        conn.close()
    return path


@pytest.fixture
def ro(content_path):
    conn = connect(content_path, readonly=True, load_vec=False)
    yield conn
    conn.close()


def _all(conn, sql, args=()):
    return conn.execute(sql, args).fetchall()


def test_migration_004_is_recorded_and_adds_the_provenance_column(tmp_path):
    path = tmp_path / "m.db"
    ensure_schema(path)
    conn = sqlite3.connect(str(path))
    try:
        versions = {row[0] for row in conn.execute("SELECT version FROM schema_migrations")}
        assert 4 in versions
        columns = {row[1] for row in conn.execute("PRAGMA table_info(readings)")}
        assert "is_simulated" in columns
        assert conn.execute("SELECT name FROM sqlite_master WHERE name = 'safety_warnings'").fetchone() is not None
    finally:
        conn.close()


def test_every_safety_warning_has_a_known_category_and_a_text(ro):
    rows = _all(ro, "SELECT id, category, text FROM safety_warnings")
    assert rows, "the package must ship safety warnings"
    for row in rows:
        assert row["category"] in VALID_WARNING_CATEGORIES, row["id"]
        assert row["text"].strip(), row["id"]


def test_every_cross_reference_resolves_inside_the_package(ro):
    models = {r["id"] for r in _all(ro, "SELECT id FROM equipment_models")}
    symptoms = {r["id"] for r in _all(ro, "SELECT id FROM symptoms")}
    warnings = {r["id"] for r in _all(ro, "SELECT id FROM safety_warnings")}
    points = {r["id"]: r for r in _all(ro, "SELECT * FROM test_points")}
    for tp in points.values():
        assert tp["equipment_model_id"] in models, tp["id"]
        for wid in tb._parse_ids(tp["safety_warning_ids"]) or []:
            assert wid in warnings, f"{tp['id']} -> {wid}"
    for tree in _all(ro, "SELECT * FROM troubleshooting_trees"):
        assert tree["equipment_model_id"] in models, tree["id"]
        assert tree["symptom_id"] is None or tree["symptom_id"] in symptoms, tree["id"]
        assert tree["status"] in VALID_TREE_STATUS, tree["id"]
        assert tree["source"] in VALID_TREE_SOURCE, tree["id"]
    step_ids = {(r["tree_id"], r["id"]) for r in _all(ro, "SELECT tree_id, id FROM troubleshooting_steps")}
    for step in _all(ro, "SELECT * FROM troubleshooting_steps"):
        if step["test_point_id"] is not None:
            assert step["test_point_id"] in points, step["id"]
        for wid in tb._parse_ids(step["safety_warning_ids"]) or []:
            assert wid in warnings, f"{step['id']} -> {wid}"
        for target in (step["next_on_pass"], step["next_on_fail"]):
            if target is not None:
                assert (step["tree_id"], target) in step_ids, f"{step['id']} -> {target} is not in its tree"


def test_each_test_point_spec_is_cited_or_todo_and_numeric_specs_use_the_right_unit(ro):
    for tp in _all(ro, "SELECT * FROM test_points"):
        assert tp["spec_source"] and tp["spec_source"].strip(), f"{tp['id']} needs a spec_source or TODO"
        assert tp["reading_type"] in VALID_READING_TYPES, tp["id"]
        if tp["spec_min"] is not None or tp["spec_max"] is not None:
            assert units.is_known_unit(tp["expected_unit"]), f"{tp['id']} has a numeric spec with no known unit"
            assert units.canonical_unit(tp["expected_unit"]) == units.canonical_unit_for_reading_type(tp["reading_type"]), (
                f"{tp['id']}: unit {tp['expected_unit']} does not measure {tp['reading_type']}"
            )


def test_step_readings_agree_with_their_test_point_and_specs_need_one(ro):
    points = {r["id"]: r for r in _all(ro, "SELECT * FROM test_points")}
    for step in _all(ro, "SELECT * FROM troubleshooting_steps"):
        has_spec = step["spec_min"] is not None or step["spec_max"] is not None
        if has_spec:
            assert step["test_point_id"] is not None, f"{step['id']} carries a spec but no test point"
        if step["test_point_id"] is not None and step["reading_type"] is not None:
            assert step["reading_type"] == points[step["test_point_id"]]["reading_type"], step["id"]


def test_terminal_steps_carry_a_result_and_branching_steps_name_both_branches(ro):
    for step in _all(ro, "SELECT * FROM troubleshooting_steps"):
        pass_, fail = step["next_on_pass"], step["next_on_fail"]
        if pass_ is None and fail is None:
            assert step["terminal_result"], f"{step['id']} is a leaf with no explanation"
        else:
            assert pass_ is not None and fail is not None, f"{step['id']} names only one branch"


def test_every_tree_resolves_each_step_and_both_branches(ro):
    for tree in _all(ro, "SELECT * FROM troubleshooting_trees"):
        conn = ro
        steps = _all(conn, "SELECT * FROM troubleshooting_steps WHERE tree_id = ? ORDER BY step_order", (tree["id"],))
        assert steps, f"{tree['id']} has no steps"
        session = type("S", (), {"tree_id": tree["id"]})()
        for step in steps:
            guided = tb.resolve_step(conn, "sid", step)
            assert guided.available and guided.step_id == step["id"]
            for verdict in ("pass", "fail"):
                assert tb.advance(conn, session, step["id"], verdict) is not None


def test_tug_660_28_no_crank_tree_starts_at_its_first_step(ro):
    found = tb.start_tree(ro, "eqm-tug-660-28", "sym-engine-no-crank")
    assert found is not None
    tree, step = found
    assert tree["id"] == "tree-tug66028-no-crank"
    assert step["id"] == "st-tug66028-no-crank-01-confirm" and step["step_order"] == 1
    guided = tb.resolve_step(ro, "sid", step)
    assert len(guided.safety_warnings) == 3  # aircraft proximity, wheel chocks, moving machinery (step 1)


def test_relief_setting_converts_to_pascal_for_the_app(ro):
    spec = tb.spec_for_test_point(ro, "tp-tug66028-main-relief")
    assert spec["expected_unit"] == "psi" and spec["spec_min"] == 1500 and spec["spec_max"] == 1500
    assert spec["canonical_unit"] == "Pa"
    assert spec["canonical_spec_min"] == pytest.approx(1500 * 6894.75729316836, rel=1e-12)
    assert "TUG" in spec["spec_source"]


def test_a_todo_test_point_has_no_numeric_spec_and_says_so(ro):
    spec = tb.spec_for_test_point(ro, "tp-tug66028-battery")
    assert spec["spec_min"] is None and spec["spec_max"] is None
    assert spec["spec_source"].startswith("TODO")


def test_retired_trees_in_content_are_never_started(ro):
    retired = _all(ro, "SELECT id, equipment_model_id, symptom_id FROM troubleshooting_trees WHERE status = 'retired'")
    for tree in retired:
        found = tb.find_tree(ro, tree["equipment_model_id"], tree["symptom_id"])
        assert found is None or found["id"] != tree["id"]


def test_package_file_names_match_the_adr_contract():
    for name in ("equipment_models.json", "safety_warnings.json", "symptoms.json", "test_points.json", "trees.json"):
        assert (PACKAGE / name).is_file(), name
