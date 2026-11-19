"""/health and /config shapes and the ADR-008 degradation rules (no Ollama, Ollama with missing models, starting)."""

from __future__ import annotations

import threading

from ai_support import blocking_probe, fake_probe

HEALTH_KEYS = {
    "status", "ollama_reachable", "chat_available", "search_available", "chat_model",
    "embedding_model", "degradation_reason", "version",
}


def test_no_ollama_is_honest_degraded_and_still_200(make_client, seeded_db):
    client = make_client(db_path=seeded_db)
    resp = client.get("/health")
    assert resp.status_code == 200
    body = resp.json()
    assert set(body) == HEALTH_KEYS
    assert body["status"] == "degraded"
    assert body["ollama_reachable"] is False
    assert body["chat_available"] is False
    assert body["search_available"] is True
    assert body["degradation_reason"] == "ollama_unreachable"
    assert body["chat_model"] == "lfm3-omni"
    assert body["embedding_model"] == "nomic-embed-text"
    assert body["version"] == "0.3.0"


def test_ready_when_chat_and_embedding_models_present(make_client, seeded_db):
    client = make_client(
        db_path=seeded_db,
        probe_fn=fake_probe(models=("lfm3-omni:latest", "nomic-embed-text:latest")),
    )
    body = client.get("/health").json()
    assert body["status"] == "ok"
    assert body["ollama_reachable"] is True
    assert body["chat_available"] is True
    assert body["degradation_reason"] is None


def test_chat_model_missing_is_reported(make_client, seeded_db):
    client = make_client(db_path=seeded_db, probe_fn=fake_probe(models=("nomic-embed-text:latest",)))
    body = client.get("/health").json()
    assert body["status"] == "degraded"
    assert body["ollama_reachable"] is True
    assert body["chat_available"] is False
    assert body["degradation_reason"] == "chat_model_missing"


def test_embedding_model_missing_keeps_chat_and_says_bm25_only(make_client, seeded_db):
    client = make_client(db_path=seeded_db, probe_fn=fake_probe(models=("lfm3-omni",)))
    body = client.get("/health").json()
    assert body["chat_available"] is True
    assert body["search_available"] is True
    assert body["degradation_reason"] == "embedding_model_missing"


def test_unopenable_database_is_db_unavailable(make_client, tmp_path):
    blocker = tmp_path / "not-a-folder"
    blocker.write_text("x", encoding="utf-8")  # a file where the database folder should be
    client = make_client(db_path=blocker / "gse-tech-pro.db")
    body = client.get("/health").json()
    assert body["status"] == "degraded"
    assert body["search_available"] is False
    assert body["degradation_reason"] == "db_unavailable"
    search = client.post("/search/manuals", json={"query": "torque"})
    assert search.status_code == 503
    assert search.json()["error"]["code"] == "ai_unavailable"


def test_starting_while_first_probe_is_pending(make_client, empty_db):
    gate = threading.Event()
    client = make_client(db_path=empty_db, probe_fn=blocking_probe(gate), wait_warm=False)
    try:
        body = client.get("/health").json()
        assert body["status"] == "starting"
    finally:
        gate.set()


def test_real_probe_on_a_closed_port_reports_unreachable(seeded_db):
    """The one test that uses the real network probe (no injected fake). Port 9 is closed by design."""
    from fastapi.testclient import TestClient

    from ai_support import TEST_TOKEN, offline_config

    from app.main import create_app

    app = create_app(offline_config(token=TEST_TOKEN), db_path=seeded_db)
    with TestClient(app, headers={"X-GSE-Token": TEST_TOKEN}) as client:
        assert app.state.svc.warm.wait(30)
        body = client.get("/health").json()
    assert body["ollama_reachable"] is False
    assert body["degradation_reason"] == "ollama_unreachable"


def test_config_reports_effective_port_and_no_ollama_host(make_client, empty_db):
    client = make_client(db_path=empty_db, port=9123, overrides={"idle_unload_minutes": 10})
    body = client.get("/config").json()
    assert body == {
        "base_url": "http://127.0.0.1:9123",
        "chat_model": "lfm3-omni",
        "embedding_model": "nomic-embed-text",
        "embedding_dimension": 768,
        "idle_unload_minutes": 10,
    }
    assert "ollama" not in str(body).lower()
