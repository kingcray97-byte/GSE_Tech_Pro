"""POST /search/repair-history (ADR-010 §7) against a synthetic migrated database, with no Ollama (fallback=true)."""

from __future__ import annotations

import sqlite3

import pytest

from ai_support import AUTH
from repair_history_support import build_seed, counts

CANDIDATE_KEYS = {
    "candidate_repair_id", "repair_description", "system", "match_tier", "similarity_score", "success_rate",
    "success_count", "attempt_count", "average_labor_hours", "last_done_at", "source_work_order_ids", "evidence",
}


@pytest.fixture(scope="module")
def rh_db(tmp_path_factory):
    path = tmp_path_factory.mktemp("rh") / "gse-tech-pro.db"
    return path, build_seed(path)


def test_migration_5_added_the_work_order_number_column(rh_db):
    path, _ = rh_db
    conn = sqlite3.connect(str(path))
    try:
        columns = {row[1] for row in conn.execute("PRAGMA table_info(work_orders)")}
        versions = {row[0] for row in conn.execute("SELECT version FROM schema_migrations")}
    finally:
        conn.close()
    assert "number" in columns
    assert 5 in versions


def test_search_requires_the_token(make_client, rh_db):
    path, ids = rh_db
    client = make_client(db_path=path)
    resp = client.post(
        "/search/repair-history", json={"work_order_id": ids["trigger"]}, headers={"X-GSE-Token": "wrong-token-value"}
    )
    assert resp.status_code == 401
    assert resp.json()["error"]["code"] == "unauthorized"


def test_a_known_work_order_returns_ranked_candidates_with_the_wire_shape(make_client, rh_db):
    path, ids = rh_db
    client = make_client(db_path=path)
    resp = client.post("/search/repair-history", json={"work_order_id": ids["trigger"], "top_k": 5}, headers=AUTH)
    assert resp.status_code == 200
    body = resp.json()
    assert set(body) == {"candidates", "fallback"}
    assert body["fallback"] is True  # no embedder: deterministic ranking
    assert [c["match_tier"] for c in body["candidates"]] == ["model", "all"]  # hard tier precedence
    top = body["candidates"][0]
    assert set(top) == CANDIDATE_KEYS
    assert top["repair_description"] == "Replace main hydraulic pump"
    assert top["system"] == "hydraulic"
    assert top["attempt_count"] == 3 and top["success_count"] == 1  # WO 4471 has a confirmed comeback
    assert 0.0 <= top["success_rate"] < 1.0
    assert set(top["source_work_order_ids"]) == {ids["h1"], ids["h2"], ids["h3"]}
    assert "WO #4471" in top["evidence"]
    assert body["candidates"][1]["repair_description"] == "Replace fuel filter"


def test_a_shop_with_no_closed_work_orders_returns_no_candidates_not_an_error(make_client, tmp_path):
    from repair_history_support import build_empty

    path = tmp_path / "no-history.db"
    open_wo = build_empty(path)
    client = make_client(db_path=path)
    resp = client.post("/search/repair-history", json={"work_order_id": open_wo}, headers=AUTH)
    assert resp.status_code == 200
    assert resp.json() == {"candidates": [], "fallback": True}


def test_extra_request_fields_are_ignored(make_client, rh_db):
    path, ids = rh_db
    client = make_client(db_path=path)
    resp = client.post(
        "/search/repair-history", json={"work_order_id": ids["trigger"], "future_field": 1}, headers=AUTH
    )
    assert resp.status_code == 200


def test_the_endpoint_never_writes_the_shared_tables(make_client, rh_db):
    path, ids = rh_db
    before = counts(path)
    client = make_client(db_path=path)
    client.post("/search/repair-history", json={"work_order_id": ids["trigger"]}, headers=AUTH)
    assert counts(path) == before


@pytest.mark.parametrize("payload", [{"work_order_id": ""}, {"work_order_id": "   "}, {}])
def test_blank_or_missing_work_order_is_422(make_client, rh_db, payload):
    path, _ = rh_db
    client = make_client(db_path=path)
    resp = client.post("/search/repair-history", json=payload, headers=AUTH)
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "validation_error"


@pytest.mark.parametrize("top_k", [0, 51])
def test_top_k_out_of_range_is_422(make_client, rh_db, top_k):
    path, ids = rh_db
    client = make_client(db_path=path)
    resp = client.post(
        "/search/repair-history", json={"work_order_id": ids["trigger"], "top_k": top_k}, headers=AUTH
    )
    assert resp.status_code == 422


def test_query_text_over_2000_chars_is_422(make_client, rh_db):
    path, ids = rh_db
    client = make_client(db_path=path)
    resp = client.post(
        "/search/repair-history", json={"work_order_id": ids["trigger"], "query_text": "x" * 2001}, headers=AUTH
    )
    assert resp.status_code == 422


def test_an_unknown_work_order_is_404(make_client, rh_db):
    path, _ = rh_db
    client = make_client(db_path=path)
    resp = client.post(
        "/search/repair-history", json={"work_order_id": "00000000-0000-0000-0000-000000000000"}, headers=AUTH
    )
    assert resp.status_code == 404
    assert resp.json()["error"]["code"] == "not_found"


def test_request_and_response_models_mirror_the_contract():
    from app.models import RepairCandidate, RepairHistorySearchRequest, RepairHistorySearchResponse

    assert set(RepairCandidate.model_json_schema()["properties"]) == CANDIDATE_KEYS
    assert set(RepairHistorySearchResponse.model_json_schema()["properties"]) == {"candidates", "fallback"}
    req = RepairHistorySearchRequest.model_validate({"work_order_id": "wo-1"})
    assert (req.top_k, req.query_text) == (5, None)
    assert RepairCandidate.model_config.get("extra") == "forbid"
