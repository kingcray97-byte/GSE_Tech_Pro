"""/guided/start and /guided/next over the REAL troubleshooting package (no Ollama).

Covers the walk, the no-tree shape, the GuidedStep wire shape (ADR-008/009: no new fields), 409 for a stale step,
422 for a bad reading, the canonicalized reading echo on a leaf, and the rule that the service never writes readings.
"""

from __future__ import annotations

import sqlite3

import pytest

from ai_support import REPO  # noqa: F401  (puts ai-service/ on sys.path)
from app.guided import NO_TREE_MESSAGE
from troubleshooting_support import connect_rw, load_package, new_migrated_db

TUG = "eqm-tug-660-28"
TUG600I = "eqm-tug-600i"
NO_CRANK = "sym-engine-no-crank"
GUIDED_KEYS = {
    "session_id", "available", "tree_id", "step_id", "step_order", "instruction", "question", "safety_warnings",
    "reading_type", "test_point_id", "spec_min", "spec_max", "spec_source", "terminal_result", "next_on_pass",
    "next_on_fail",
}


@pytest.fixture
def content_db(tmp_path):
    path = new_migrated_db(tmp_path / "walk.db")
    conn = connect_rw(path)
    try:
        load_package(conn)
    finally:
        conn.close()
    return path


def _start(client, model=TUG, symptom=NO_CRANK):
    resp = client.post("/guided/start", json={"equipment_model_id": model, "symptom_id": symptom})
    assert resp.status_code == 200, resp.text
    return resp.json()


def _next(client, session_id, step_id, verdict=None, **extra):
    body = {"session_id": session_id, "step_id": step_id, "verdict": verdict, **extra}
    return client.post("/guided/next", json=body)


def test_start_returns_the_first_step_with_the_full_wire_shape(make_client, content_db):
    client = make_client(db_path=content_db)
    step = _start(client)
    assert set(step) == GUIDED_KEYS
    assert len(step["session_id"]) == 36
    assert step["available"] is True
    assert step["tree_id"] == "tree-tug66028-no-crank"
    assert step["step_id"] == "st-tug66028-no-crank-01-confirm" and step["step_order"] == 1
    assert step["next_on_pass"] == "st-tug66028-no-crank-02-operator-state"
    assert step["next_on_fail"] == "st-tug66028-no-crank-13-not-no-crank"
    assert len(step["safety_warnings"]) == 3
    assert step["spec_min"] is None and step["reading_type"] is None  # a question step


def test_start_for_an_unmatched_symptom_keeps_the_no_tree_step(make_client, content_db):
    client = make_client(db_path=content_db)
    step = _start(client, symptom="sym-hydraulic-no-lift")  # the 660-28 has no hydraulic tree
    assert step["available"] is False and step["instruction"] == NO_TREE_MESSAGE
    assert step["step_id"] is None and step["tree_id"] is None


def test_start_for_an_unknown_model_keeps_the_no_tree_step(make_client, content_db):
    client = make_client(db_path=content_db)
    assert _start(client, model="eqm-nope", symptom=None)["available"] is False


def test_pass_and_fail_walk_the_tree_and_fail_reaches_a_leaf(make_client, content_db):
    client = make_client(db_path=content_db)
    start = _start(client)
    sid = start["session_id"]
    passed = _next(client, sid, start["step_id"], "pass").json()
    assert passed["step_id"] == "st-tug66028-no-crank-02-operator-state" and passed["step_order"] == 2
    # Back on step 1 is not possible; a fail from step 2 would go to its own leaf. Start a fresh walk to hit a leaf.
    fresh = _start(client)
    leaf = _next(client, fresh["session_id"], fresh["step_id"], "fail").json()
    assert leaf["step_id"] == "st-tug66028-no-crank-13-not-no-crank"
    assert leaf["next_on_pass"] is None and leaf["next_on_fail"] is None
    assert leaf["terminal_result"]


def test_a_terminal_step_stays_put_whatever_the_verdict(make_client, content_db):
    client = make_client(db_path=content_db)
    fresh = _start(client)
    leaf = _next(client, fresh["session_id"], fresh["step_id"], "fail").json()
    again = _next(client, fresh["session_id"], leaf["step_id"], "pass").json()
    assert again["step_id"] == leaf["step_id"]


def test_a_stale_step_id_is_a_conflict(make_client, content_db):
    client = make_client(db_path=content_db)
    start = _start(client)
    resp = _next(client, start["session_id"], "st-tug66028-no-crank-05-cranking-voltage", "pass")
    assert resp.status_code == 409
    assert resp.json()["error"]["code"] == "conflict"


def test_an_unknown_reading_unit_is_a_validation_error(make_client, content_db):
    client = make_client(db_path=content_db)
    start = _start(client)
    resp = _next(client, start["session_id"], start["step_id"], "pass", reading_value=2.0, reading_unit="furlong")
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "validation_error"


def test_a_reading_without_a_unit_is_a_validation_error(make_client, content_db):
    client = make_client(db_path=content_db)
    start = _start(client)
    resp = _next(client, start["session_id"], start["step_id"], "pass", reading_value=2.0)
    assert resp.status_code == 422


def test_leaf_explanation_echoes_the_canonical_reading(make_client, content_db):
    client = make_client(db_path=content_db)
    current = _start(client, model=TUG, symptom=NO_CRANK)
    sid = current["session_id"]
    # Walk the pass path to the rest-voltage step (04), then fail it with a reading.
    for _ in range(3):
        current = _next(client, sid, current["step_id"], "pass").json()
    assert current["step_id"] == "st-tug66028-no-crank-04-rest-voltage"
    step = _next(client, sid, current["step_id"], "fail", reading_value=11, reading_unit="V").json()
    assert step["step_id"] == "st-tug66028-no-crank-16-rest-voltage-low"
    assert "Reading at step st-tug66028-no-crank-04-rest-voltage: 11 V = 11 V (canonical)." in step["terminal_result"]


def test_canonical_spec_appears_on_the_step_when_the_test_point_has_one(make_client, content_db):
    # The shipped 600i lift test point has no numeric spec yet. Give it one in this throwaway copy to check the
    # service canonicalizes it (psi -> Pa) on the wire, exactly as the app expects.
    conn = sqlite3.connect(str(content_db))
    conn.execute("UPDATE test_points SET spec_min = 1500, spec_max = 1500, expected_unit = 'psi', "
                 "spec_source = 'TEST CITE' WHERE id = 'tp-tug600i-main-lift-pressure'")
    conn.commit()
    conn.close()
    client = make_client(db_path=content_db)
    current = _start(client, model=TUG600I, symptom="sym-hydraulic-no-lift")
    sid = current["session_id"]
    for _ in range(3):
        current = _next(client, sid, current["step_id"], "pass").json()
    assert current["step_id"] == "st-tug600i-no-lift-04-main-pressure"
    assert current["reading_type"] == "hydraulic_pressure"
    assert current["spec_min"] == pytest.approx(1500 * 6894.75729316836, rel=1e-12)
    assert current["spec_source"] == "TEST CITE"


def test_the_service_never_writes_readings(make_client, content_db):
    conn = sqlite3.connect(str(content_db))
    conn.execute(
        "INSERT INTO readings (id, work_order_id, reading_type, value_canonical, unit_canonical, entered_at, "
        "created_at, updated_at) VALUES ('r-sentinel', 'wo-1', 'voltage', 12.0, 'V', '2026-11-16', '2026-11-16', "
        "'2026-11-16')"
    )
    conn.commit()
    before = conn.execute("SELECT * FROM readings ORDER BY id").fetchall()
    conn.close()

    client = make_client(db_path=content_db)
    start = _start(client)
    _next(client, start["session_id"], start["step_id"], "fail", reading_value=12.0, reading_unit="V")

    conn = sqlite3.connect(str(content_db))
    after = conn.execute("SELECT * FROM readings ORDER BY id").fetchall()
    conn.close()
    assert after == before


def test_unknown_session_is_404(make_client, content_db):
    client = make_client(db_path=content_db)
    resp = client.post("/guided/next", json={"session_id": "nope"})
    assert resp.status_code == 404
    assert resp.json()["error"]["code"] == "not_found"
