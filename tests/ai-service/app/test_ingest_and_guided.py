"""POST /ingest + GET /ingest/status round-trip with the real seed PDF, and the /guided/* 'no tree yet' contract."""

from __future__ import annotations

import time

import pytest

from ai_support import MAINT_600I


def require_seed():
    if not MAINT_600I.exists():
        pytest.skip(f"seed manual not present: {MAINT_600I.name}")
    return MAINT_600I


def _wait_done(client, job_id: str, timeout_s: float = 180.0) -> dict:
    deadline = time.monotonic() + timeout_s
    body: dict = {}
    while time.monotonic() < deadline:
        body = client.get(f"/ingest/status?job_id={job_id}").json()
        if body["state"] in ("succeeded", "failed", "cancelled"):
            return body
        time.sleep(0.2)
    raise AssertionError(f"ingest job did not finish: {body}")


def test_ingest_round_trip_writes_chunks_without_vectors(make_client, empty_db):
    src = require_seed()
    client = make_client(db_path=empty_db)
    accepted = client.post("/ingest", json={"paths": [str(src)]})
    assert accepted.status_code == 202
    job_id = accepted.json()["job_id"]
    assert job_id and len(job_id) == 36

    status = _wait_done(client, job_id)
    assert status["state"] == "succeeded"
    assert status["job_id"] == job_id
    assert status["files_total"] == 1
    assert status["files_done"] == 1
    assert status["chunks_created"] > 0
    assert status["chunks_embedded"] == 0  # no Ollama: text-only index (ADR-008 rule 4)
    assert status["current_file"] is None
    assert isinstance(status["errors"], list)

    # Search now finds the freshly ingested manual (BM25-only).
    hits = client.post("/search/manuals", json={"query": "flexramp lubricate axle"}).json()
    assert hits["fallback"] is True and hits["results"]


def test_relative_paths_are_rejected(make_client, empty_db):
    client = make_client(db_path=empty_db)
    resp = client.post("/ingest", json={"paths": ["manuals/tug.pdf"]})
    assert resp.status_code == 400
    assert resp.json()["error"]["code"] == "invalid_request"


@pytest.mark.parametrize(
    "path",
    [
        r"\\fileserver\manuals\tug.pdf",     # UNC: would start SMB/NTLM traffic
        "//fileserver/manuals/tug.pdf",     # UNC, forward-slash form
        r"\\?\UNC\fileserver\manuals",       # extended-length UNC
    ],
)
def test_unc_paths_are_rejected_before_any_filesystem_access(make_client, empty_db, path):
    client = make_client(db_path=empty_db)
    resp = client.post("/ingest", json={"paths": [path]})
    assert resp.status_code == 400
    assert resp.json()["error"]["code"] == "invalid_request"
    assert "UNC" in resp.json()["error"]["message"]


def test_drive_root_is_rejected(make_client, empty_db):
    import pathlib

    client = make_client(db_path=empty_db)
    root = pathlib.Path(pathlib.Path.cwd().anchor)  # e.g. C:\ : walking it would scan the whole disk
    resp = client.post("/ingest", json={"paths": [str(root)]})
    assert resp.status_code == 400
    assert "root" in resp.json()["error"]["message"]


def test_missing_path_is_reported_in_errors_not_as_a_crash(make_client, empty_db, tmp_path):
    client = make_client(db_path=empty_db)
    missing = tmp_path / "nope.pdf"
    job_id = client.post("/ingest", json={"paths": [str(missing)]}).json()["job_id"]
    status = _wait_done(client, job_id)
    assert status["chunks_created"] == 0
    assert any("not found" in e["message"] for e in status["errors"])


def test_unknown_job_is_404_not_found(make_client, empty_db):
    client = make_client(db_path=empty_db)
    resp = client.get("/ingest/status?job_id=00000000-0000-0000-0000-000000000000")
    assert resp.status_code == 404
    assert resp.json()["error"]["code"] == "not_found"


def test_status_without_job_id_is_422(make_client, empty_db):
    client = make_client(db_path=empty_db)
    resp = client.get("/ingest/status")
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "validation_error"


def test_ingest_request_needs_at_least_one_path(make_client, empty_db):
    client = make_client(db_path=empty_db)
    assert client.post("/ingest", json={"paths": []}).status_code == 422


GUIDED_MESSAGE = "No troubleshooting tree is available for this model and symptom yet."


def test_guided_start_returns_a_session_and_the_no_tree_step(make_client, empty_db):
    client = make_client(db_path=empty_db)
    resp = client.post(
        "/guided/start",
        json={"equipment_model_id": "11111111-1111-1111-1111-111111111111", "complaint_text": "no hydraulics"},
    )
    assert resp.status_code == 200
    step = resp.json()
    assert len(step["session_id"]) == 36
    assert step["available"] is False
    assert step["instruction"] == GUIDED_MESSAGE
    assert step["step_order"] == 0
    assert step["safety_warnings"] == []
    assert step["tree_id"] is None and step["step_id"] is None


def test_guided_next_on_a_known_session_keeps_the_no_tree_shape(make_client, empty_db):
    client = make_client(db_path=empty_db)
    session_id = client.post("/guided/start", json={"equipment_model_id": "m1"}).json()["session_id"]
    resp = client.post(
        "/guided/next",
        json={"session_id": session_id, "step_id": None, "verdict": "pass", "reading_value": 2300, "reading_unit": "psi"},
    )
    assert resp.status_code == 200
    step = resp.json()
    assert step["session_id"] == session_id
    assert step["available"] is False
    assert step["instruction"] == GUIDED_MESSAGE


def test_guided_next_on_an_unknown_session_is_404(make_client, empty_db):
    client = make_client(db_path=empty_db)
    resp = client.post("/guided/next", json={"session_id": "does-not-exist"})
    assert resp.status_code == 404
    assert resp.json()["error"]["code"] == "not_found"


@pytest.mark.parametrize("verdict", ["maybe", "PASS", ""])
def test_guided_next_rejects_unknown_verdicts(make_client, empty_db, verdict):
    client = make_client(db_path=empty_db)
    session_id = client.post("/guided/start", json={"equipment_model_id": "m1"}).json()["session_id"]
    resp = client.post("/guided/next", json={"session_id": session_id, "verdict": verdict})
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "validation_error"


def test_seed_path_constant_points_at_the_real_manual():
    # Guards the fixture path: a moved manual should fail loudly here, not silently skip everything.
    assert MAINT_600I.name.endswith("(Delta).pdf")
