"""POST /search/manuals against a real ingested manual with no Ollama (BM25-only, fallback=true)."""

from __future__ import annotations

import re

import pytest

UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


def test_bm25_only_search_reports_fallback_and_cites_the_page(make_client, seeded_db):
    client = make_client(db_path=seeded_db)
    resp = client.post(
        "/search/manuals",
        json={"query": "How often do I lubricate the flexramp axle?", "top_k": 5},
    )
    assert resp.status_code == 200
    body = resp.json()
    assert body["fallback"] is True  # no Ollama: text-only ranking, and the App badges it
    assert body["results"], "expected at least one passage for a lubrication question"
    pages = {p for r in body["results"] for p in range(r["page_start"], r["page_end"] + 1)}
    assert pages & {2, 10}, f"expected a lubrication page (2 or 10) in the top results, got {sorted(pages)}"
    top = body["results"][0]
    assert top["citation"].startswith(top["manual_title"])
    assert "page" in top["citation"]


def test_chunk_id_is_the_guid_never_the_integer_rowid(make_client, seeded_db):
    client = make_client(db_path=seeded_db)
    body = client.post("/search/manuals", json={"query": "flexramp lubricate"}).json()
    assert body["results"]
    for r in body["results"]:
        assert UUID_RE.match(r["chunk_id"]), r["chunk_id"]
        assert UUID_RE.match(r["manual_id"]), r["manual_id"]


def test_embedding_model_listed_but_embed_call_failing_degrades_to_bm25(make_client, seeded_db):
    """Ollama lists the model, but the embed request fails. Search must answer, text-only, not error."""
    from ai_support import fake_probe

    client = make_client(
        db_path=seeded_db,
        probe_fn=fake_probe(models=("lfm3-omni", "nomic-embed-text")),
    )
    resp = client.post("/search/manuals", json={"query": "flexramp lubricate axle"})
    assert resp.status_code == 200
    body = resp.json()
    assert body["fallback"] is True
    assert body["results"]


def test_include_text_false_omits_passage_text(make_client, seeded_db):
    client = make_client(db_path=seeded_db)
    body = client.post(
        "/search/manuals", json={"query": "flexramp lubricate", "include_text": False}
    ).json()
    assert body["results"]
    assert all(r["text"] is None for r in body["results"])


def test_unknown_manual_filter_returns_no_results_not_an_error(make_client, seeded_db):
    client = make_client(db_path=seeded_db)
    resp = client.post(
        "/search/manuals",
        json={"query": "flexramp", "manual_id": "00000000-0000-0000-0000-000000000000"},
    )
    assert resp.status_code == 200
    assert resp.json() == {"results": [], "fallback": True}


@pytest.mark.parametrize(
    "payload",
    [
        {"query": ""},
        {"query": "torque", "top_k": 0},
        {"query": "torque", "top_k": 51},
        {"query": "torque", "chunk_kinds": ["not_a_kind"]},
        {},
    ],
)
def test_invalid_requests_are_422_validation_error(make_client, seeded_db, payload):
    client = make_client(db_path=seeded_db)
    resp = client.post("/search/manuals", json=payload)
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "validation_error"

