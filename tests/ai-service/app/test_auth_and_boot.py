"""Token enforcement on every endpoint, refusal to serve without a token, and loopback-only binding."""

from __future__ import annotations

import logging

import pytest

from fastapi.testclient import TestClient

from ai_support import TEST_TOKEN, offline_config  # noqa: F401

from app.main import RefuseToServe, create_app, main
from app.security import token_matches

ENDPOINTS = [
    ("GET", "/health", None),
    ("GET", "/config", None),
    ("POST", "/chat", {"messages": [{"role": "user", "content": "hi"}]}),
    ("POST", "/search/manuals", {"query": "torque"}),
    ("POST", "/ingest", {"paths": ["C:/manuals"]}),
    ("GET", "/ingest/status?job_id=abc", None),
    ("DELETE", "/manuals/abc", None),
    ("POST", "/guided/start", {"equipment_model_id": "m1"}),
    ("POST", "/guided/next", {"session_id": "s1"}),
    ("GET", "/no-such-route", None),  # unknown paths must not reveal routing either
]


def _call(client, method, path, body, headers):
    return client.request(method, path, json=body, headers=headers)


@pytest.mark.parametrize("method,path,body", ENDPOINTS)
def test_missing_token_is_401_on_every_endpoint(make_client, empty_db, method, path, body):
    client = make_client(db_path=empty_db, token=TEST_TOKEN)
    resp = _call(client, method, path, body, headers={"X-GSE-Token": ""})
    assert resp.status_code == 401
    err = resp.json()["error"]
    assert err["code"] == "unauthorized"
    assert TEST_TOKEN not in resp.text


@pytest.mark.parametrize("method,path,body", ENDPOINTS)
def test_wrong_token_is_401_on_every_endpoint(make_client, empty_db, method, path, body):
    client = make_client(db_path=empty_db, token=TEST_TOKEN)
    resp = _call(client, method, path, body, headers={"X-GSE-Token": "nope-" + "x" * 30})
    assert resp.status_code == 401
    assert resp.json()["error"]["code"] == "unauthorized"


def test_no_header_at_all_is_401(make_client, empty_db):
    client = make_client(db_path=empty_db)
    bare = TestClient(client.app)  # no default headers at all
    resp = bare.get("/health")
    assert resp.status_code == 401
    assert resp.json()["error"]["code"] == "unauthorized"


def test_blank_whitespace_token_is_401(make_client, empty_db):
    client = make_client(db_path=empty_db)
    resp = client.get("/config", headers={"X-GSE-Token": "   "})
    assert resp.status_code == 401


def test_correct_token_is_accepted(make_client, empty_db):
    client = make_client(db_path=empty_db)
    assert client.get("/health").status_code == 200
    assert client.get("/config").status_code == 200


def test_token_is_never_logged(make_client, empty_db, caplog):
    caplog.set_level(logging.DEBUG)
    client = make_client(db_path=empty_db)
    client.get("/health")
    client.get("/health", headers={"X-GSE-Token": "wrong-token-value"})
    client.post("/chat", json={"messages": [{"role": "user", "content": "hi"}]})
    joined = "\n".join(r.getMessage() for r in caplog.records)
    assert TEST_TOKEN not in joined


def test_empty_token_refuses_to_serve(empty_db):
    cfg = offline_config(token="   ")
    with pytest.raises(RefuseToServe):
        create_app(cfg, db_path=empty_db)


def test_main_refuses_empty_token_with_nonzero_exit(capsys):
    code = main(["--token", ""])
    assert code == 3
    assert "refusing to start" in capsys.readouterr().err


def test_main_bad_config_path_exits_2(tmp_path, capsys):
    code = main(["--config", str(tmp_path / "missing.yaml"), "--token", TEST_TOKEN])
    assert code == 2
    assert "configuration error" in capsys.readouterr().err


def test_service_host_must_be_loopback():
    from rag.config import load_config

    with pytest.raises(ValueError):
        load_config(overrides={"host": "0.0.0.0"})


@pytest.mark.parametrize(
    "provided,expected,ok",
    [
        (TEST_TOKEN, TEST_TOKEN, True),
        (None, TEST_TOKEN, False),
        ("", TEST_TOKEN, False),
        ("  ", TEST_TOKEN, False),
        (TEST_TOKEN + "x", TEST_TOKEN, False),
        (TEST_TOKEN[:-1], TEST_TOKEN, False),
        ("anything", "", False),  # an empty expected token never matches
    ],
)
def test_token_matches_is_strict(provided, expected, ok):
    assert token_matches(provided, expected) is ok


def test_without_config_arg_the_setup_written_config_wins(tmp_path, monkeypatch):
    # Installed tablet: setup writes AiService\config\config.yaml and does not ship app\config.yaml.
    import rag.config as rag_config
    from app.main import parse_args

    installed = tmp_path / "config" / "config.yaml"
    installed.parent.mkdir()
    installed.write_text("service:\n  port: 9431\n  token: from-setup\n", encoding="utf-8")
    monkeypatch.setattr(rag_config, "_CONFIG_CANDIDATES", (installed, tmp_path / "app" / "config.yaml"))

    args = parse_args([])
    assert args.config is None
    config = rag_config.load_config(args.config, overrides={"port": None, "token": None})
    assert (config.service.port, config.service.token) == (9431, "from-setup")
