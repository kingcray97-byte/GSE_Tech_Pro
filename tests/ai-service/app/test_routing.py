"""AI routing (ADR-013): route order, fallback cloud -> shop server -> tablet, the route tag on every answer,
job-detail stripping for the cloud, the two-key cloud opt-in, and secrets (Credential Manager only, never logged).
Remote routes use scripted backends and injected probes; nothing leaves 127.0.0.1."""

from __future__ import annotations

import logging

import pytest

from ai_support import fake_probe, offline_config
from test_chat_endpoint import ScriptedBackend, text_turn, tool_turn

from app.chat import NOT_OFFERED_TOOL
from app.llm import ChatUnavailable, remote_backend
from app.routing import CLOUD_SAFE_TOOLS, RouteHealth, plan_routes
from rag.config import load_config

TABLET_UP = ("lfm3-omni", "nomic-embed-text")
SECRET = "sk-test-SECRET-value-0000"

ROUTES_ON = {
    "routes.shop_server.enabled": True,
    "routes.shop_server.model": "litellm_proxy/shop-model",
    "routes.shop_server.api_base": "http://shop.example:4000",
    "routes.cloud.enabled": True,
    "routes.cloud.model": "provider/cloud-model",
    "routes.cloud.key_name": "cloud-key",
    "routes.cloud.probe_host": "api.example:443",
}


# ------------------------------------------------------------------ route order (same table as AiRouting.Plan in C#)


@pytest.mark.parametrize(
    "mode,cloud_allowed,shop,cloud_enabled,expected",
    [
        ("tablet-only", True, True, True, ["tablet"]),
        ("prefer-shop-server", True, True, True, ["shop-server", "tablet"]),
        ("prefer-shop-server", True, False, True, ["tablet"]),
        ("prefer-cloud", True, True, True, ["cloud", "shop-server", "tablet"]),
        ("prefer-cloud", False, True, True, ["shop-server", "tablet"]),  # app admin has not allowed the cloud
        ("prefer-cloud", True, True, False, ["shop-server", "tablet"]),  # service config has not enabled it
        ("prefer-cloud", True, False, True, ["cloud", "tablet"]),
        ("prefer-cloud", False, False, False, ["tablet"]),
    ],
)
def test_route_plan(mode, cloud_allowed, shop, cloud_enabled, expected):
    assert plan_routes(mode, cloud_allowed, shop, cloud_enabled) == expected


# ------------------------------------------------------------------ helpers


class Health(RouteHealth):
    """RouteHealth with scripted probes and a fake Credential Manager."""

    def __init__(self, config, *, shop_ok=True, cloud_ok=True, keys=None):
        self.probe_calls: list[str] = []

        def shop(_cfg, _t):
            self.probe_calls.append("shop-server")
            return (shop_ok, "Online" if shop_ok else "Not reachable")

        def cloud(_cfg, _t):
            self.probe_calls.append("cloud")
            return (cloud_ok, "Online" if cloud_ok else "Offline")

        keys = {"cloud-key": SECRET} if keys is None else keys
        super().__init__(config, secrets=lambda name: keys.get(name), shop_probe_fn=shop, cloud_probe_fn=cloud)


def _client(make_client, db, *, tablet, shop=None, cloud=None, shop_ok=True, cloud_ok=True, keys=None):
    cfg = offline_config(**ROUTES_ON)
    return make_client(
        db_path=db,
        backend=tablet,
        probe_fn=fake_probe(TABLET_UP),
        overrides=ROUTES_ON,
        route_health=Health(cfg, shop_ok=shop_ok, cloud_ok=cloud_ok, keys=keys),
        remote_backends={k: v for k, v in (("shop-server", shop), ("cloud", cloud)) if v is not None},
    )


def _ask(mode="prefer-cloud", allowed=True, share=False, **extra):
    body = {
        "messages": [{"role": "user", "content": "Lift drifts down under load."}],
        "work_order_id": "WO-00042",
        "asset_id": "TUG-12",
        "routing": {"mode": mode, "cloud_allowed": allowed, "share_job_details_with_cloud": share},
    }
    body.update(extra)
    return body


# ------------------------------------------------------------------ routing and fallback


def test_default_request_stays_on_the_tablet(make_client, seeded_db):
    tablet, shop, cloud = ScriptedBackend([text_turn("tablet")]), ScriptedBackend([]), ScriptedBackend([])
    client = _client(make_client, seeded_db, tablet=tablet, shop=shop, cloud=cloud)
    body = client.post("/chat", json={"messages": [{"role": "user", "content": "hi"}]}).json()
    assert body["route"] == "tablet" and body["text"] == "tablet"
    assert shop.calls == [] and cloud.calls == []


def test_prefer_cloud_answers_from_cloud_and_says_so(make_client, seeded_db):
    tablet, cloud = ScriptedBackend([]), ScriptedBackend([text_turn("from the cloud")])
    client = _client(make_client, seeded_db, tablet=tablet, cloud=cloud)
    body = client.post("/chat", json=_ask()).json()
    assert body["route"] == "cloud"
    assert body["is_suggestion"] is True  # same safety label on every route
    assert tablet.calls == []


def test_cloud_down_falls_back_to_shop_then_tablet(make_client, seeded_db):
    tablet = ScriptedBackend([text_turn("tablet answer")])
    shop = ScriptedBackend([ChatUnavailable("ai_unavailable", "shop gone")])
    cloud = ScriptedBackend([])
    client = _client(make_client, seeded_db, tablet=tablet, shop=shop, cloud=cloud, cloud_ok=False)
    body = client.post("/chat", json=_ask()).json()
    assert body["route"] == "tablet" and body["text"] == "tablet answer"
    assert cloud.calls == []  # failed its health check: skipped without trying
    assert len(shop.calls) == 1  # tried, failed, fell back


def test_cloud_failing_mid_session_next_answer_comes_from_the_tablet(make_client, seeded_db):
    """Acceptance (10A.7): unplug mid-session and the next answer comes from the tablet."""
    tablet = ScriptedBackend([text_turn("tablet after unplug"), text_turn("still tablet")])
    cloud = ScriptedBackend([text_turn("cloud first"), ChatUnavailable("ai_unavailable", "network gone")])
    client = _client(make_client, seeded_db, tablet=tablet, cloud=cloud)
    svc = client.app.state.svc
    assert client.post("/chat", json=_ask(mode="prefer-cloud")).json()["route"] == "cloud"
    second = client.post("/chat", json=_ask(mode="prefer-cloud")).json()
    assert second["route"] == "tablet" and second["text"] == "tablet after unplug"
    # The failed route is marked down, so the third question does not wait on the cloud at all.
    assert svc.route_health.check("cloud") == (False, "Last request failed")
    third = client.post("/chat", json=_ask(mode="prefer-cloud")).json()
    assert third["route"] == "tablet"
    assert len(cloud.calls) == 2


def test_cloud_needs_the_app_admin_switch_too(make_client, seeded_db):
    tablet, cloud = ScriptedBackend([text_turn("tablet")]), ScriptedBackend([])
    client = _client(make_client, seeded_db, tablet=tablet, cloud=cloud)
    body = client.post("/chat", json=_ask(allowed=False)).json()
    assert body["route"] == "tablet" and cloud.calls == []


def test_cloud_without_key_is_unavailable(make_client, seeded_db):
    tablet, cloud = ScriptedBackend([text_turn("tablet")]), ScriptedBackend([])
    client = _client(make_client, seeded_db, tablet=tablet, cloud=cloud, keys={})
    assert client.post("/chat", json=_ask()).json()["route"] == "tablet"
    routes = {r["route"]: r for r in client.get("/health").json()["routes"]}
    assert routes["cloud"]["available"] is False
    assert routes["cloud"]["message"] == "Key not in Credential Manager"


def test_every_route_down_and_tablet_down_is_honest_503(make_client, seeded_db):
    cfg = offline_config(**ROUTES_ON)
    client = make_client(
        db_path=seeded_db,
        backend=ScriptedBackend([]),
        overrides=ROUTES_ON,  # default probe: tablet llama-server down
        route_health=Health(cfg, shop_ok=False, cloud_ok=False),
    )
    resp = client.post("/chat", json=_ask())
    assert resp.status_code == 503
    assert resp.json()["error"]["code"] == "ai_unavailable"


# ------------------------------------------------------------------ job details stay home unless allowed


def _system_text(backend):
    return backend.calls[0][0][0]["content"]


def test_cloud_without_share_gets_no_job_context_and_only_manual_search(make_client, seeded_db):
    cloud = ScriptedBackend([text_turn("generic answer")])
    client = _client(make_client, seeded_db, tablet=ScriptedBackend([]), cloud=cloud)
    client.post("/chat", json=_ask(share=False))
    system = _system_text(cloud)
    assert "WO-00042" not in system and "TUG-12" not in system
    assert "not shared" in system
    offered = {t["function"]["name"] for t in cloud.calls[0][1]}
    assert offered == set(CLOUD_SAFE_TOOLS)


def test_cloud_cannot_call_a_tool_it_was_not_offered(make_client, seeded_db):
    cloud = ScriptedBackend([tool_turn("get_repair_history", '{"asset_id": "TUG-12"}'), text_turn("ok")])
    client = _client(make_client, seeded_db, tablet=ScriptedBackend([]), cloud=cloud)
    client.post("/chat", json=_ask(share=False))
    tool_msgs = [m for m in cloud.calls[1][0] if m["role"] == "tool"]
    assert tool_msgs and tool_msgs[0]["content"] == NOT_OFFERED_TOOL


def test_cloud_with_share_gets_the_same_context_as_the_tablet(make_client, seeded_db):
    cloud = ScriptedBackend([text_turn("answer")])
    client = _client(make_client, seeded_db, tablet=ScriptedBackend([]), cloud=cloud)
    client.post("/chat", json=_ask(share=True))
    assert "WO-00042" in _system_text(cloud)
    assert len(cloud.calls[0][1]) > len(CLOUD_SAFE_TOOLS)


def test_shop_server_always_gets_job_context(make_client, seeded_db):
    shop = ScriptedBackend([text_turn("shop answer")])
    client = _client(make_client, seeded_db, tablet=ScriptedBackend([]), shop=shop)
    body = client.post("/chat", json=_ask(mode="prefer-shop-server")).json()
    assert body["route"] == "shop-server"
    assert "WO-00042" in _system_text(shop)


# ------------------------------------------------------------------ secrets


@pytest.mark.parametrize("field", ["api_key", "key", "token", "secret", "password"])
def test_config_with_a_key_in_it_is_refused(tmp_path, field):
    cfg_file = tmp_path / "config.yaml"
    cfg_file.write_text(f"routes:\n  cloud:\n    enabled: true\n    {field}: sk-plain\n", encoding="utf-8")
    with pytest.raises(ValueError, match="Credential Manager"):
        load_config(cfg_file)


def test_key_is_passed_to_litellm_but_never_logged_or_returned(make_client, seeded_db, monkeypatch, caplog):
    import sys
    import types

    captured: dict = {}
    fake = types.ModuleType("litellm")

    def completion(**kwargs):
        captured.update(kwargs)
        return iter([{"choices": [{"delta": {"content": "cloud says hi"}}]}])

    fake.completion = completion
    monkeypatch.setitem(sys.modules, "litellm", fake)

    cfg = offline_config(**ROUTES_ON)
    health = Health(cfg)
    caplog.set_level(logging.DEBUG)
    client = make_client(
        db_path=seeded_db,
        backend=ScriptedBackend([]),
        probe_fn=fake_probe(TABLET_UP),
        overrides=ROUTES_ON,
        route_health=health,
        remote_backends={"cloud": remote_backend(cfg, "cloud", lambda: health.key_for("cloud"))},
    )
    resp = client.post("/chat", json=_ask(use_tools=False))
    assert resp.json()["route"] == "cloud"
    assert captured["api_key"] == SECRET
    assert captured["model"] == "provider/cloud-model"
    assert SECRET not in resp.text
    assert SECRET not in client.get("/health").text
    assert all(SECRET not in r.getMessage() for r in caplog.records)


def test_keyring_lookup_failure_is_not_fatal(monkeypatch):
    import sys
    import types

    from app import routing

    broken = types.ModuleType("keyring")

    def get_password(_s, _u):
        raise RuntimeError("no backend")

    broken.get_password = get_password
    monkeypatch.setitem(sys.modules, "keyring", broken)
    assert routing.keyring_lookup("cloud-key") is None
    assert routing.keyring_lookup("") is None


def test_cloud_probe_sends_nothing_and_handles_offline():
    from app.routing import cloud_probe
    from rag.config import RemoteRouteConfig

    ok, msg = cloud_probe(RemoteRouteConfig(enabled=True, model="m", probe_host="127.0.0.1:9"), 0.5)
    assert (ok, msg) == (False, "Offline")
    ok, msg = cloud_probe(RemoteRouteConfig(enabled=True, model="m"), 0.5)
    assert ok is False and "probe_host" in msg
