"""llama.cpp runtime (ADR-012): the real probe and LiteLLM path against a fake llama-server, embeddings, the process
supervisor (always 127.0.0.1, never spawns without a model file, idle unload stops it, the next chat starts it), and
config guards. No model and no network beyond 127.0.0.1."""

from __future__ import annotations

import subprocess

import pytest

from ai_support import offline_config
from fake_llama_server import FakeLlamaServer

from app.llm import LlamaCppChatBackend
from app.runtime import (
    CachedProbe,
    LlamaRuntime,
    LlamaServerProcess,
    RuntimeMonitor,
    RuntimeProbe,
    chat_server_args,
    embedding_server_args,
    probe_now,
)
from rag import embedding as emb
from rag.config import load_config


# ------------------------------------------------------------------ probe: ready / loading / down


def test_probe_reads_ready_loading_and_down():
    with FakeLlamaServer(state="ready") as srv:
        assert probe_now(srv.base_url) == RuntimeProbe("ready")
        srv.state = "loading"
        assert probe_now(srv.base_url).state == "loading"
        port = srv.port
    down = probe_now(f"http://127.0.0.1:{port}")  # server stopped: connection refused
    assert down.state == "down" and down.reachable is False


def test_health_endpoint_follows_the_real_fake_server(make_client, seeded_db):
    with FakeLlamaServer(state="loading") as chat_srv, FakeLlamaServer() as embed_srv:
        client = make_client(
            db_path=seeded_db,
            probe_fn=probe_now,  # the real probe, over real HTTP
            overrides={"llama_server.port": chat_srv.port, "llama_server.embedding.port": embed_srv.port},
        )
        body = client.get("/health").json()
        assert body["status"] == "starting" and body["degradation_reason"] == "model_loading"
        chat_srv.state = "ready"
        client.app.state.svc.monitor.invalidate()
        body = client.get("/health").json()
        assert body["status"] == "ok" and body["chat_available"] is True


# ------------------------------------------------------------------ chat over the real LiteLLM openai/ route


def test_chat_works_offline_against_llama_server(make_client, seeded_db):
    """Acceptance (10A.7): with only 127.0.0.1 available, chat answers from the tablet and says so."""
    with FakeLlamaServer(reply="Check the hydraulic fluid level first.") as chat_srv, FakeLlamaServer() as embed_srv:
        overrides = {"llama_server.port": chat_srv.port, "llama_server.embedding.port": embed_srv.port}
        cfg = offline_config(**overrides)
        client = make_client(db_path=seeded_db, probe_fn=probe_now, overrides=overrides,
                             backend=LlamaCppChatBackend(cfg))
        resp = client.post("/chat", json={"messages": [{"role": "user", "content": "Lift is slow."}], "use_tools": False})
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert body["text"] == "Check the hydraulic fluid level first."
        assert body["route"] == "tablet"
        assert body["model"] == "lfm3-omni"
        sent = [p for path, p in chat_srv.requests if path == "/v1/chat/completions"]
        assert sent and sent[0]["model"] == "lfm3-omni"  # LiteLLM strips the openai/ provider prefix


# ------------------------------------------------------------------ embeddings


def test_embeddings_come_from_the_embedding_server():
    with FakeLlamaServer() as srv:
        cfg = load_config(overrides={"llama_server.embedding.port": srv.port})
        assert emb.is_embedding_available(cfg) is True
        vectors = emb.embed_texts(["hydraulic pump", "mast chain"], "search_document", cfg)
        assert vectors is not None and len(vectors) == 2 and len(vectors[0]) == 768
        path, payload = srv.requests[-1]
        assert path == "/v1/embeddings"
        assert payload["input"][0] == "search_document: hydraulic pump"  # ADR-003 task prefix kept


def test_embedding_server_loading_means_no_vectors():
    with FakeLlamaServer(state="loading") as srv:
        cfg = load_config(overrides={"llama_server.embedding.port": srv.port})
        assert emb.is_embedding_available(cfg) is False
        assert emb.embed_texts(["x"], "search_query", cfg) is None


def test_wrong_dimension_is_a_hard_error():
    with FakeLlamaServer(dim=384) as srv:
        cfg = load_config(overrides={"llama_server.embedding.port": srv.port})
        with pytest.raises(emb.EmbeddingDimensionError):
            emb.embed_texts(["x"], "search_document", cfg)


# ------------------------------------------------------------------ process supervisor


class FakeProc:
    def __init__(self, args):
        self.args = args
        self.returncode = None
        self.terminated = False

    def poll(self):
        return self.returncode

    def terminate(self):
        self.terminated = True
        self.returncode = 0

    def kill(self):
        self.returncode = -9

    def wait(self, timeout=None):
        return self.returncode


def _spawn_mode(tmp_path, **extra):
    binary = tmp_path / "llama-server.exe"
    chat = tmp_path / "chat.gguf"
    embed = tmp_path / "embed.gguf"
    chat.write_bytes(b"GGUF")
    embed.write_bytes(b"GGUF")
    values = {
        "llama_server.binary": str(binary),
        "llama_server.model": str(chat),
        "llama_server.embedding.model": str(embed),
        "llama_server.extra_args": ["--reasoning", "off"],
    }
    values.update(extra)
    return offline_config(**values)


def test_server_args_always_bind_loopback_and_carry_config(tmp_path):
    cfg = _spawn_mode(tmp_path, **{"llama_server.threads": 4})
    chat = chat_server_args(cfg)
    assert chat[0].endswith("llama-server.exe")
    assert chat[chat.index("--host") + 1] == "127.0.0.1"
    assert chat[chat.index("--port") + 1] == str(cfg.llama_server.port)
    assert chat[chat.index("--alias") + 1] == "lfm3-omni"
    assert chat[chat.index("--threads") + 1] == "4"
    assert chat[-2:] == ["--reasoning", "off"]
    embed = embedding_server_args(cfg)
    assert embed[embed.index("--host") + 1] == "127.0.0.1"
    assert "--embedding" in embed and embed[embed.index("--pooling") + 1] == "mean"


def test_runtime_spawns_both_and_stops_both(tmp_path):
    spawned: list[FakeProc] = []

    def spawn(args):
        proc = FakeProc(args)
        spawned.append(proc)
        return proc

    cfg = _spawn_mode(tmp_path)
    runtime = LlamaRuntime(cfg, RuntimeMonitor(cfg, probe_fn=lambda _u: RuntimeProbe("down")), spawn=spawn)
    runtime.start_all()
    runtime.start_all()  # idempotent: already running
    assert len(spawned) == 2
    assert runtime.chat.running() and runtime.embedding.running()
    runtime.stop_all()
    assert all(p.terminated for p in spawned)
    assert not runtime.chat.running()


def test_no_spawn_without_a_model_file(tmp_path):
    calls = []
    cfg = offline_config(**{"llama_server.binary": str(tmp_path / "llama-server.exe"),
                            "llama_server.model": str(tmp_path / "missing.gguf")})
    runtime = LlamaRuntime(cfg, RuntimeMonitor(cfg, probe_fn=lambda _u: RuntimeProbe("down")),
                           spawn=lambda a: calls.append(a))
    runtime.start_all()
    assert calls == []
    assert runtime.chat.ensure_started() is False


def test_attach_mode_never_spawns():
    calls = []
    cfg = offline_config()  # binary empty
    runtime = LlamaRuntime(cfg, RuntimeMonitor(cfg, probe_fn=lambda _u: RuntimeProbe("ready")),
                           spawn=lambda a: calls.append(a))
    runtime.start_all()
    assert calls == [] and runtime.chat.manages_process is False


def test_a_crashed_server_is_restarted(tmp_path):
    spawned: list[FakeProc] = []
    cfg = _spawn_mode(tmp_path)
    proc_probe = CachedProbe("http://x", probe_fn=lambda _u: RuntimeProbe("down"))
    server = LlamaServerProcess("chat", lambda: chat_server_args(cfg), cfg.llama_server.model, proc_probe,
                                enabled=True, spawn=lambda a: spawned.append(FakeProc(a)) or spawned[-1])
    server.ensure_started()
    spawned[0].returncode = 1  # crashed
    assert server.running() is False
    server.ensure_started()
    assert len(spawned) == 2 and server.running()


def test_wait_ready_gives_up_after_timeout_and_on_crash(tmp_path):
    cfg = _spawn_mode(tmp_path)
    now = {"t": 0.0}
    probe = CachedProbe("http://x", probe_fn=lambda _u: RuntimeProbe("loading"))
    procs: list[FakeProc] = []
    server = LlamaServerProcess("chat", lambda: chat_server_args(cfg), cfg.llama_server.model, probe,
                                enabled=True, spawn=lambda a: procs.append(FakeProc(a)) or procs[-1])
    server.ensure_started()

    def sleep(s):
        now["t"] += s

    assert server.wait_ready(3.0, sleep=sleep, clock=lambda: now["t"]) is False
    assert now["t"] >= 3.0
    procs[0].returncode = 1
    assert server.wait_ready(60.0, sleep=sleep, clock=lambda: now["t"]) is False


def test_idle_unload_stops_the_servers_and_the_next_chat_starts_them(make_client, seeded_db, tmp_path):
    """After idle unload, the next chat starts llama-server again and waits for the model, then answers."""
    spawned: list[FakeProc] = []
    state = {"chat": "ready"}

    def probe(url):
        return RuntimeProbe(state["chat"]) if url.endswith(":9") else RuntimeProbe("down")

    def spawn(args):
        spawned.append(FakeProc(args))
        state["chat"] = "ready"  # the fake "loads" instantly
        return spawned[-1]

    from test_chat_endpoint import ScriptedBackend, text_turn

    overrides = {"llama_server.binary": str(tmp_path / "llama-server.exe"),
                 "llama_server.model": str(tmp_path / "chat.gguf")}
    (tmp_path / "chat.gguf").write_bytes(b"GGUF")
    backend = ScriptedBackend([text_turn("first"), text_turn("second")])
    client = make_client(db_path=seeded_db, probe_fn=probe, backend=backend, overrides=overrides, spawn=spawn)
    svc = client.app.state.svc
    # Wire the real unload path (the scripted backend's unload only counts calls).
    assert client.post("/chat", json={"messages": [{"role": "user", "content": "a"}], "use_tools": False}).json()["text"] == "first"
    assert len(spawned) == 1
    svc.runtime.stop_all()  # what LlamaCppChatBackend.unload does on idle
    state["chat"] = "down"
    svc.monitor.invalidate()
    assert spawned[0].terminated
    resp = client.post("/chat", json={"messages": [{"role": "user", "content": "b"}], "use_tools": False})
    assert resp.status_code == 200 and resp.json()["text"] == "second"
    assert len(spawned) == 2  # restarted on demand


def test_llamacpp_backend_unload_stops_the_runtime():
    calls = []

    class Rt:
        def stop_all(self):
            calls.append("stop")

    LlamaCppChatBackend(offline_config(), Rt()).unload()
    assert calls == ["stop"]


# ------------------------------------------------------------------ config guards


@pytest.mark.parametrize("flag", ["--host", "--host=0.0.0.0", "--port", "-m"])
def test_extra_args_cannot_override_bind_port_or_model(flag):
    with pytest.raises(ValueError, match="must not set"):
        offline_config(**{"llama_server.extra_args": [flag, "x"]})


def test_obsolete_ollama_section_is_ignored(tmp_path):
    cfg_file = tmp_path / "config.yaml"
    cfg_file.write_text("ollama:\n  host: 127.0.0.1:11434\n  no_cloud: true\nmodels:\n  chat: lfm3-omni\n",
                        encoding="utf-8")
    cfg = load_config(cfg_file)
    assert cfg.models.chat == "lfm3-omni"


def test_default_spawn_hides_the_console_window(monkeypatch):
    seen = {}

    def fake_popen(args, **kwargs):
        seen.update(kwargs)
        return FakeProc(args)

    from app import runtime as rt

    monkeypatch.setattr(rt.subprocess, "Popen", fake_popen)
    rt._default_spawn(["llama-server.exe"])
    assert seen["stdin"] is subprocess.DEVNULL and seen["stdout"] is subprocess.DEVNULL
    assert seen["creationflags"] == getattr(subprocess, "CREATE_NO_WINDOW", 0)


# ------------------------------------------------------------------ pinned-build settings (ADR-012, 2026-11-19)


def test_shipped_config_uses_liquids_listed_lfm3_settings():
    cfg = offline_config()  # the real app/config.yaml
    assert cfg.llama_server.extra_args == ["--reasoning", "off"]  # LFM3-Omni-2B is non-reasoning
    assert cfg.models.chat_temperature == 0.0  # greedy decoding for text


def test_installer_template_uses_the_same_lfm3_settings(tmp_path):
    from ai_support import REPO

    text = (REPO / "installer" / "ai-service-config.yaml").read_text(encoding="utf-8")
    for token, value in (("@LLAMA_SERVER@", "'C:/x/llama-server.exe'"), ("@CHAT_MODEL@", "''"),
                         ("@EMBED_MODEL@", "''"), ("@SHOP_SERVER_URL@", "''"), ("@SHOP_SERVER_ENABLED@", "false")):
        text = text.replace(token, value)
    filled = tmp_path / "config.yaml"
    filled.write_text(text, encoding="utf-8")
    cfg = load_config(filled, overrides={"token": "t"})
    assert cfg.llama_server.extra_args == ["--reasoning", "off"]
    assert cfg.models.chat_temperature == 0.0
    assert cfg.routes.cloud.enabled is False


def test_subcommand_builds_llama_serve_command_line(tmp_path):
    cfg = _spawn_mode(tmp_path, **{"llama_server.binary": str(tmp_path / "llama.exe"), "llama_server.subcommand": "serve"})
    chat = chat_server_args(cfg)
    assert chat[0].endswith("llama.exe") and chat[1] == "serve"
    assert chat[chat.index("--host") + 1] == "127.0.0.1"
    assert embedding_server_args(cfg)[1] == "serve"
    assert chat_server_args(_spawn_mode(tmp_path))[1] == "--model"  # no subcommand: llama-server.exe --model ...


def test_subcommand_must_be_one_word():
    with pytest.raises(ValueError, match="one word"):
        offline_config(**{"llama_server.subcommand": "serve --host 0.0.0.0"})


def test_temperature_from_config_reaches_litellm(monkeypatch):
    import sys
    import types

    captured: dict = {}
    fake = types.ModuleType("litellm")
    fake.completion = lambda **kw: captured.update(kw) or iter([{"choices": [{"delta": {"content": "ok"}}]}])
    monkeypatch.setitem(sys.modules, "litellm", fake)
    LlamaCppChatBackend(offline_config(**{"models.chat_temperature": 0.3})).complete([{"role": "user", "content": "x"}], None)
    assert captured["temperature"] == 0.3
    LlamaCppChatBackend(offline_config()).complete([{"role": "user", "content": "x"}], None)
    assert captured["temperature"] == 0.0


def test_photo_reaches_llama_server_as_an_image_part(make_client, seeded_db):
    """Phase 7.4: the real LiteLLM -> llama-server path keeps the OpenAI image_url part (what --mmproj reads)."""
    import base64

    jpeg = b"\xff\xd8\xff\xe0" + b"\x00" * 32
    b64 = base64.b64encode(jpeg).decode("ascii")
    reply = "Oily film on the hose fitting.\nLook up: hydraulic hose fitting leak"
    with FakeLlamaServer(reply=reply) as chat_srv, FakeLlamaServer() as embed_srv:
        overrides = {"llama_server.port": chat_srv.port, "llama_server.embedding.port": embed_srv.port}
        cfg = offline_config(**overrides)
        client = make_client(db_path=seeded_db, probe_fn=probe_now, overrides=overrides,
                             backend=LlamaCppChatBackend(cfg))
        resp = client.post("/vision/inspect", json={"image_base64": b64, "media_type": "image/jpeg"})
        assert resp.status_code == 200, resp.text
        assert resp.json()["description"].startswith("Oily film")
        sent = [p for path, p in chat_srv.requests if path == "/v1/chat/completions"]
        user = sent[0]["messages"][-1]
        parts = {p["type"]: p for p in user["content"]}
        assert parts["image_url"]["image_url"]["url"] == f"data:image/jpeg;base64,{b64}"
