"""The owner's cloud provider (ADR-013, decided 2026-11-19): a hosted inference server with an OpenAI-compatible REST
API, authenticated by a long-lived API key that the owner's website issues. The tablet keeps the key in Windows
Credential Manager and sends it as `Authorization: Bearer <key>`.

These tests run the real LiteLLM `openai/` route and the real route health check against a key-checking fake server
on 127.0.0.1. No real key, no internet."""

from __future__ import annotations

import pytest

from ai_support import fake_probe, offline_config
from fake_llama_server import FakeLlamaServer
from test_chat_endpoint import ScriptedBackend, text_turn

from app.routing import RouteHealth
from rag.config import load_config

KEY = "gse-test-key-issued-by-website-0001"
KEY_NAME = "gse-cloud-api-key"


def _cloud_overrides(port: int) -> dict:
    return {
        "routes.cloud.enabled": True,
        "routes.cloud.model": "openai/owner-model",
        "routes.cloud.api_base": f"http://127.0.0.1:{port}/v1",  # loopback is the only place plain http is allowed
        "routes.cloud.key_name": KEY_NAME,
    }


def _ask():
    return {
        "messages": [{"role": "user", "content": "Hydraulic lift drifts down under load."}],
        "use_tools": False,
        "routing": {"mode": "prefer-cloud", "cloud_allowed": True, "share_job_details_with_cloud": False},
    }


def _client(make_client, db, server, *, stored_key, tablet):
    overrides = _cloud_overrides(server.port)
    cfg = offline_config(**overrides)
    keys = {KEY_NAME: stored_key} if stored_key else {}
    return make_client(
        db_path=db,
        backend=tablet,
        probe_fn=fake_probe(("lfm3-omni", "nomic-embed-text")),
        overrides=overrides,
        route_health=RouteHealth(cfg, secrets=keys.get),  # real TCP probe of the api_base host, fake Credential Manager
    )


def test_cloud_answers_with_the_website_issued_key_as_bearer(make_client, seeded_db):
    with FakeLlamaServer(reply="Check the lift cylinder seals.", required_key=KEY) as server:
        tablet = ScriptedBackend([])
        client = _client(make_client, seeded_db, server, stored_key=KEY, tablet=tablet)
        resp = client.post("/chat", json=_ask())
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert body["route"] == "cloud"
        assert body["model"] == "openai/owner-model"
        assert body["text"] == "Check the lift cylinder seals."
        assert server.auth_headers[-1] == f"Bearer {KEY}"
        sent = [p for path, p in server.requests if path == "/v1/chat/completions"][-1]
        assert sent["model"] == "owner-model"  # LiteLLM strips the openai/ provider prefix
        assert tablet.calls == []
        assert KEY not in resp.text and KEY not in client.get("/health").text


def test_revoked_key_falls_back_to_the_tablet(make_client, seeded_db):
    """A key revoked on the website gets 401 from the server; the answer still comes, from the tablet."""
    with FakeLlamaServer(required_key="the-new-key") as server:
        tablet = ScriptedBackend([text_turn("tablet answer")])
        client = _client(make_client, seeded_db, server, stored_key=KEY, tablet=tablet)
        body = client.post("/chat", json=_ask()).json()
        assert body["route"] == "tablet" and body["text"] == "tablet answer"
        assert client.app.state.svc.route_health.check("cloud") == (False, "Last request failed")


def test_no_key_on_the_tablet_never_calls_the_server(make_client, seeded_db):
    with FakeLlamaServer(required_key=KEY) as server:
        tablet = ScriptedBackend([text_turn("tablet answer")])
        client = _client(make_client, seeded_db, server, stored_key=None, tablet=tablet)
        assert client.post("/chat", json=_ask()).json()["route"] == "tablet"
        assert server.requests == []
        cloud = {r["route"]: r for r in client.get("/health").json()["routes"]}["cloud"]
        assert cloud["available"] is False and cloud["message"] == "Key not in Credential Manager"


@pytest.mark.parametrize("base", ["http://ai.example.com/v1", "ftp://ai.example.com", "ai.example.com/v1"])
def test_cloud_api_base_must_be_https(base):
    with pytest.raises(ValueError, match="https"):
        load_config(overrides={"routes.cloud.api_base": base})


@pytest.mark.parametrize("base", ["https://ai.example.com/v1", "http://127.0.0.1:8443/v1", "http://localhost:9000/v1"])
def test_https_or_loopback_cloud_api_base_is_accepted(base):
    assert load_config(overrides={"routes.cloud.api_base": base}).routes.cloud.api_base == base


def test_shipped_config_names_the_key_entry_and_keeps_cloud_off():
    cfg = offline_config()
    assert cfg.routes.cloud.enabled is False
    assert cfg.routes.cloud.key_name == KEY_NAME
    assert cfg.routes.cloud.model == "" and cfg.routes.cloud.api_base == ""  # filled in by the owner, never guessed
