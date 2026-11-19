"""app/troubleshooting.py on a small synthetic database: tree lookup, step resolution, advance, judgement, notes.

All numbers here are synthetic test values chosen to exercise the code paths. They are NOT manual specs.
"""

from __future__ import annotations

import sqlite3
from pathlib import Path
from types import SimpleNamespace

import pytest

from ai_support import REPO  # noqa: F401  (puts ai-service/ on sys.path)
from app import troubleshooting as tb
from app import units
from rag.db import connect
from troubleshooting_support import connect_rw, insert_synthetic, new_migrated_db

SESSION_ID = "11111111-2222-3333-4444-555555555555"


@pytest.fixture
def db(tmp_path: Path) -> Path:
    path = new_migrated_db(tmp_path / "gse.db")
    conn = connect_rw(path)
    try:
        insert_synthetic(
            conn,
            models=[{"id": "eqm-a", "make": "Acme", "model": "A1"}],
            warnings=[
                {"id": "sw-hyd", "category": "hydraulic_pressure", "text": "HYD WARNING", "source_citation": "CITE hyd"},
                {"id": "sw-lock", "category": "lockout_tagout", "text": "LOCK WARNING", "source_citation": "CITE lock"},
                {"id": "sw-hv", "category": "high_voltage", "text": "HV WARNING", "source_citation": "CITE hv"},
                {"id": "sw-gen", "category": "general", "text": "GEN WARNING", "source_citation": "CITE gen"},
                {"id": "sw-air", "category": "aircraft_proximity", "text": "AIR WARNING", "source_citation": "CITE air"},
            ],
            symptoms=[
                {"id": "sym-x", "code": "X", "name": "X", "category": "hydraulic", "system": "Hyd", "description": "d"},
                {"id": "sym-y", "code": "Y", "name": "Y", "category": "hydraulic", "system": "Hyd", "description": "d"},
                {"id": "sym-z", "code": "Z", "name": "Z", "category": "engine", "system": "Eng", "description": "d"},
            ],
            points=[
                {"id": "tp-psi", "equipment_model_id": "eqm-a", "name": "psi range", "reading_type": "hydraulic_pressure",
                 "expected_unit": "psi", "spec_min": 1000, "spec_max": 1500, "spec_source": "TEST CITE p.1",
                 "safety_warning_ids": ["sw-air"]},
                {"id": "tp-psi-single", "equipment_model_id": "eqm-a", "name": "psi setting",
                 "reading_type": "hydraulic_pressure", "expected_unit": "psi", "spec_min": 1500, "spec_max": 1500,
                 "spec_source": "TEST CITE p.2", "safety_warning_ids": []},
                {"id": "tp-volt-mismatch", "equipment_model_id": "eqm-a", "name": "voltage in psi",
                 "reading_type": "voltage", "expected_unit": "psi", "spec_min": 12, "spec_max": 14,
                 "spec_source": "TEST CITE p.3", "safety_warning_ids": []},
                {"id": "tp-volt-todo", "equipment_model_id": "eqm-a", "name": "voltage no spec",
                 "reading_type": "voltage", "expected_unit": "V", "spec_min": None, "spec_max": None,
                 "spec_source": "TODO: needs spec from manual", "safety_warning_ids": ["sw-hv"]},
            ],
            trees=[
                {"id": "tree-t1", "equipment_model_id": "eqm-a", "symptom_id": "sym-x", "title": "T1",
                 "system": "Hyd", "status": "approved", "source": "handwritten"},
                {"id": "tree-t2", "equipment_model_id": "eqm-a", "symptom_id": "sym-y", "title": "T2",
                 "system": "Hyd", "status": "retired", "source": "handwritten"},
                {"id": "tree-d", "equipment_model_id": "eqm-a", "symptom_id": "sym-z", "title": "Draft",
                 "system": "Eng", "status": "draft", "source": "handwritten"},
                {"id": "tree-t4", "equipment_model_id": "eqm-a", "symptom_id": None, "title": "T4",
                 "system": "Elec", "status": "draft", "source": "handwritten"},
                {"id": "tree-t5", "equipment_model_id": "eqm-a", "symptom_id": None, "title": "T5",
                 "system": "Elec", "status": "draft", "source": "handwritten"},
            ],
            steps=[
                # tree-t1: s1 is the test step, s2/s3 are terminals. s0 has the lowest order and is inserted last.
                {"id": "s1", "tree_id": "tree-t1", "step_order": 1, "instruction": "Read pressure", "question": "Is it in range?",
                 "reading_type": "hydraulic_pressure", "test_point_id": "tp-psi", "spec_min": None, "spec_max": None,
                 "spec_source": None, "safety_warning_ids": ["sw-hyd", "sw-lock"],
                 "next_on_pass": "s2", "next_on_fail": "s3", "terminal_result": None},
                {"id": "s2", "tree_id": "tree-t1", "step_order": 2, "instruction": "OK", "question": None,
                 "reading_type": None, "test_point_id": None, "spec_min": None, "spec_max": None, "spec_source": None,
                 "safety_warning_ids": [], "next_on_pass": None, "next_on_fail": None, "terminal_result": "PRESSURE OK"},
                {"id": "s3", "tree_id": "tree-t1", "step_order": 3, "instruction": "Low", "question": None,
                 "reading_type": None, "test_point_id": None, "spec_min": None, "spec_max": None, "spec_source": None,
                 "safety_warning_ids": [], "next_on_pass": None, "next_on_fail": None, "terminal_result": "PRESSURE LOW"},
                {"id": "s0", "tree_id": "tree-t1", "step_order": 0, "instruction": "Confirm the complaint",
                 "question": "Does the machine move?", "reading_type": None, "test_point_id": None, "spec_min": None,
                 "spec_max": None, "spec_source": None, "safety_warning_ids": ["sw-gen"],
                 "next_on_pass": "s1", "next_on_fail": "s3", "terminal_result": None},
                {"id": "t2-only", "tree_id": "tree-t2", "step_order": 1, "instruction": "retired", "question": None,
                 "reading_type": None, "test_point_id": None, "spec_min": None, "spec_max": None, "spec_source": None,
                 "safety_warning_ids": [], "next_on_pass": None, "next_on_fail": None, "terminal_result": "x"},
                # tree-t4: voltage mismatch, no-spec test point, question step, step-level canonical spec, bad refs.
                {"id": "v1", "tree_id": "tree-t4", "step_order": 1, "instruction": "Mismatch", "question": "q",
                 "reading_type": "voltage", "test_point_id": "tp-volt-mismatch", "spec_min": None, "spec_max": None,
                 "spec_source": None, "safety_warning_ids": [], "next_on_pass": "v2", "next_on_fail": "v3",
                 "terminal_result": None},
                {"id": "v2", "tree_id": "tree-t4", "step_order": 2, "instruction": "No spec", "question": "q",
                 "reading_type": "voltage", "test_point_id": "tp-volt-todo", "spec_min": None, "spec_max": None,
                 "spec_source": None, "safety_warning_ids": [], "next_on_pass": "v3", "next_on_fail": "v4",
                 "terminal_result": None},
                {"id": "v3", "tree_id": "tree-t4", "step_order": 3, "instruction": "Missing warning", "question": None,
                 "reading_type": None, "test_point_id": None, "spec_min": None, "spec_max": None, "spec_source": None,
                 "safety_warning_ids": ["sw-missing"], "next_on_pass": None, "next_on_fail": None,
                 "terminal_result": "Leaf three"},
                {"id": "v4", "tree_id": "tree-t4", "step_order": 4, "instruction": "Step-level canonical spec",
                 "question": "q", "reading_type": "voltage", "test_point_id": None, "spec_min": 12.0, "spec_max": 14.0,
                 "spec_source": "STEP CITE", "safety_warning_ids": "not-json", "next_on_pass": None,
                 "next_on_fail": None, "terminal_result": "Leaf four"},
                # v5: a pass branch only (no fail branch).
                {"id": "v5", "tree_id": "tree-t4", "step_order": 5, "instruction": "Pass-only", "question": "q",
                 "reading_type": None, "test_point_id": None, "spec_min": None, "spec_max": None, "spec_source": None,
                 "safety_warning_ids": [], "next_on_pass": "v4", "next_on_fail": None, "terminal_result": None},
                # tree-t5: a dangling reference, to prove integrity errors surface instead of silently stopping.
                {"id": "d1", "tree_id": "tree-t5", "step_order": 1, "instruction": "Dangling", "question": "q",
                 "reading_type": None, "test_point_id": None, "spec_min": None, "spec_max": None, "spec_source": None,
                 "safety_warning_ids": [], "next_on_pass": "nope", "next_on_fail": "nope", "terminal_result": None},
            ],
        )
    finally:
        conn.close()
    return path


def _ro(path: Path) -> sqlite3.Connection:
    return connect(path, readonly=True, load_vec=False)


def _row(conn, step_id):
    return conn.execute("SELECT * FROM troubleshooting_steps WHERE id = ?", (step_id,)).fetchone()


# ---------------------------------------------------------------- tree lookup


def test_find_tree_matches_symptom_and_never_falls_back_to_another_symptom(db):
    conn = _ro(db)
    try:
        assert tb.find_tree(conn, "eqm-a", "sym-x")["id"] == "tree-t1"
        assert tb.find_tree(conn, "eqm-a", "sym-y") is None  # retired tree is excluded
        assert tb.find_tree(conn, "eqm-a", "sym-none") is None  # no silent fallback to another complaint
        assert tb.find_tree(conn, "eqm-none", None) is None
    finally:
        conn.close()


def test_find_tree_without_symptom_requires_a_symptom_when_several_trees_match(db):
    # eqm-a has several non-retired trees: with no symptom and no complaint, no tree is guessed.
    conn = _ro(db)
    try:
        assert tb.find_tree(conn, "eqm-a") is None
    finally:
        conn.close()


def test_first_step_is_the_lowest_step_order_even_when_inserted_last(db):
    conn = _ro(db)
    try:
        found = tb.start_tree(conn, "eqm-a", "sym-x")
        assert found is not None
        tree, step = found
        assert tree["id"] == "tree-t1" and step["id"] == "s0"
    finally:
        conn.close()


# ---------------------------------------------------------------- step resolution


def test_resolve_step_canonicalizes_a_psi_spec_and_orders_warnings_by_severity(db):
    conn = _ro(db)
    try:
        guided = tb.resolve_step(conn, SESSION_ID, _row(conn, "s1"))
    finally:
        conn.close()
    assert guided.available is True
    assert guided.session_id == SESSION_ID
    assert guided.tree_id == "tree-t1" and guided.step_id == "s1" and guided.step_order == 1
    assert guided.instruction == "Read pressure" and guided.question == "Is it in range?"
    assert guided.reading_type == "hydraulic_pressure"
    assert guided.test_point_id == "tp-psi"
    assert guided.spec_min == pytest.approx(units.to_canonical(1000.0, "psi"), rel=1e-12)
    assert guided.spec_max == pytest.approx(units.to_canonical(1500.0, "psi"), rel=1e-12)
    assert guided.spec_source == "TEST CITE p.1"
    assert guided.next_on_pass == "s2" and guided.next_on_fail == "s3"
    assert guided.terminal_result is None
    # Step warnings (hydraulic, lockout) plus the test point's own (aircraft), in ADR-009 severity order.
    assert guided.safety_warnings == ["HYD WARNING", "LOCK WARNING", "AIR WARNING"]


def test_canonical_spec_is_in_pascal_for_a_psi_test_point(db):
    conn = _ro(db)
    try:
        spec = tb.spec_for_test_point(conn, "tp-psi")
    finally:
        conn.close()
    assert spec["canonical_unit"] == "Pa"
    assert spec["canonical_spec_min"] == pytest.approx(1000.0 * 6894.75729316836, rel=1e-12)
    assert spec["canonical_spec_max"] == pytest.approx(1500.0 * 6894.75729316836, rel=1e-12)
    assert spec["expected_unit"] == "psi"  # the authored unit stays for display


def test_question_step_with_no_test_point_has_no_spec_and_no_invented_source(db):
    conn = _ro(db)
    try:
        guided = tb.resolve_step(conn, SESSION_ID, _row(conn, "s0"))
    finally:
        conn.close()
    assert guided.reading_type is None and guided.test_point_id is None
    assert guided.spec_min is None and guided.spec_max is None
    assert guided.spec_source is None
    assert guided.safety_warnings == ["GEN WARNING"]


def test_step_level_spec_without_a_test_point_is_taken_as_already_canonical(db):
    conn = _ro(db)
    try:
        guided = tb.resolve_step(conn, SESSION_ID, _row(conn, "v4"))
    finally:
        conn.close()
    assert guided.spec_min == 12.0 and guided.spec_max == 14.0
    assert guided.spec_source == "STEP CITE"


def test_unit_that_does_not_match_the_reading_type_withholds_the_spec(db):
    conn = _ro(db)
    try:
        guided = tb.resolve_step(conn, SESSION_ID, _row(conn, "v1"))
    finally:
        conn.close()
    assert guided.reading_type == "voltage"
    assert guided.spec_min is None and guided.spec_max is None


def test_test_point_without_a_numeric_spec_reports_todo(db):
    conn = _ro(db)
    try:
        guided = tb.resolve_step(conn, SESSION_ID, _row(conn, "v2"))
    finally:
        conn.close()
    assert guided.spec_min is None and guided.spec_max is None
    assert guided.spec_source == "TODO: needs spec from manual"
    assert guided.safety_warnings == ["HV WARNING"]


def test_missing_warning_id_shows_a_visible_placeholder_not_silence(db):
    conn = _ro(db)
    try:
        guided = tb.resolve_step(conn, SESSION_ID, _row(conn, "v3"))
    finally:
        conn.close()
    assert len(guided.safety_warnings) == 1
    assert "missing from the database" in guided.safety_warnings[0]


def test_unreadable_warning_json_is_flagged_and_never_dropped(db):
    conn = _ro(db)
    try:
        guided = tb.resolve_step(conn, SESSION_ID, _row(conn, "v4"))
    finally:
        conn.close()
    assert guided.safety_warnings == [
        "Safety warnings for this step could not be read. Stop and check the manual before continuing."
    ]


def test_terminal_note_is_appended_to_the_leaf_only(db):
    conn = _ro(db)
    try:
        leaf = tb.resolve_step(conn, SESSION_ID, _row(conn, "s2"), note="Reading note")
        mid = tb.resolve_step(conn, SESSION_ID, _row(conn, "s1"), note="Reading note")
    finally:
        conn.close()
    assert leaf.terminal_result == "PRESSURE OK\n\nReading note"
    assert mid.terminal_result is None


def test_warning_texts_sort_by_adr009_severity_then_id(db):
    conn = _ro(db)
    try:
        texts = tb.warning_texts(conn, ["sw-air", "sw-gen", "sw-hv", "sw-lock", "sw-hyd", "sw-hyd"])
    finally:
        conn.close()
    assert texts == ["HYD WARNING", "LOCK WARNING", "HV WARNING", "AIR WARNING", "GEN WARNING"]


# ---------------------------------------------------------------- advance


def _session(tree_id="tree-t1"):
    return SimpleNamespace(tree_id=tree_id)


def test_advance_follows_pass_and_fail_branches(db):
    conn = _ro(db)
    try:
        assert tb.advance(conn, _session(), "s1", "pass")["id"] == "s2"
        assert tb.advance(conn, _session(), "s1", "fail")["id"] == "s3"
    finally:
        conn.close()


def test_advance_with_no_verdict_or_na_keeps_the_same_step_when_a_fail_branch_exists(db):
    conn = _ro(db)
    try:
        assert tb.advance(conn, _session(), "s1", None)["id"] == "s1"
        assert tb.advance(conn, _session(), "s1", "na")["id"] == "s1"
    finally:
        conn.close()


def test_advance_with_no_verdict_takes_pass_only_when_there_is_no_fail_branch(db):
    # v5 has a pass branch and no fail branch: no verdict means the pass branch is taken.
    conn = _ro(db)
    try:
        assert tb.advance(conn, _session("tree-t4"), "v5", None)["id"] == "v4"
        assert tb.advance(conn, _session("tree-t4"), "v5", "pass")["id"] == "v4"
        # A fail verdict with no fail branch has nowhere to go: the same step is returned, never a guess.
        assert tb.advance(conn, _session("tree-t4"), "v5", "fail")["id"] == "v5"
    finally:
        conn.close()


def test_terminal_step_returns_itself_whatever_the_verdict(db):
    conn = _ro(db)
    try:
        for verdict in ("pass", "fail", "na", None):
            assert tb.advance(conn, _session(), "s2", verdict)["id"] == "s2"
    finally:
        conn.close()


def test_advance_rejects_a_step_outside_the_session_tree(db):
    conn = _ro(db)
    try:
        with pytest.raises(LookupError):
            tb.advance(conn, _session(), "t2-only", "pass")
        with pytest.raises(LookupError):
            tb.advance(conn, _session(tree_id=None), "s1", "pass")
    finally:
        conn.close()


def test_dangling_next_step_is_an_integrity_error_not_a_silent_stop(db):
    conn = _ro(db)
    try:
        with pytest.raises(tb.TreeIntegrityError):
            tb.advance(conn, _session("tree-t5"), "d1", "pass")
    finally:
        conn.close()


# ---------------------------------------------------------------- judgement and notes


@pytest.mark.parametrize(
    ("value", "lo", "hi", "expected"),
    [
        (5.0, 1.0, 10.0, "pass"),
        (1.0, 1.0, 10.0, "pass"),  # inclusive lower bound
        (10.0, 1.0, 10.0, "pass"),  # inclusive upper bound
        (0.9, 1.0, 10.0, "fail"),
        (10.1, 1.0, 10.0, "fail"),
        (5.0, 1.0, 1.0, "na"),  # single published setting: no tolerance, so no verdict
        (5.0, None, None, "na"),
        (5.0, 1.0, None, "pass"),
        (0.5, 1.0, None, "fail"),
        (50.0, None, 10.0, "fail"),
    ],
)
def test_judge_pass_fail_rules(value, lo, hi, expected):
    assert tb.judge(value, lo, hi) == expected


def test_reading_note_canonicalizes_and_flags_a_disagreeing_app_verdict(db):
    conn = _ro(db)
    try:
        note = tb.reading_note(conn, _row(conn, "s1"), 1200.0, "psi", "fail")
        agree = tb.reading_note(conn, _row(conn, "s1"), 1200.0, "psi", "pass")
    finally:
        conn.close()
    assert "1200 psi" in note and "Pa (canonical)" in note
    assert f"{units.to_canonical(1200.0, 'psi'):.6g}" in note
    assert "differs" in note and "app verdict was recorded" in note
    assert "differs" not in agree


def test_spec_judgement_with_a_kpa_reading_matches_the_psi_spec(db):
    # 7000 kPa is about 1015.3 psi: inside the synthetic 1000-1500 psi range once converted to Pa.
    conn = _ro(db)
    try:
        spec = tb.spec_for_test_point(conn, "tp-psi")
    finally:
        conn.close()
    value = units.to_canonical(7000.0, "kPa")
    assert tb.judge(value, spec["canonical_spec_min"], spec["canonical_spec_max"]) == "pass"


def test_missing_database_table_raises_an_operational_error_for_the_caller_to_handle(tmp_path):
    empty = tmp_path / "bare.db"
    conn = sqlite3.connect(str(empty))
    try:
        with pytest.raises(sqlite3.OperationalError):
            tb.find_tree(conn, "eqm-a")
    finally:
        conn.close()
