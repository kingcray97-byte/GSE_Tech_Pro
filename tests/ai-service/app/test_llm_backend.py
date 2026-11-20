"""LlamaCppChatBackend (tablet route via LiteLLM openai/ -> llama-server): config-only model name, streaming accumulation (text and tool-call deltas), error mapping,
and the offline environment guard. The fake litellm module is injected via sys.modules; one test uses the real
LiteLLM against a closed port to prove the offline error path."""

from __future__ import annotations

import os
import sys
import types

import pytest

from ai_support import OFFLINE_CHAT_PORT, offline_config

from app import llm as llm_mod
from app.llm import ChatUnavailable, IdleUnloader, LlamaCppChatBackend


def test_offline_cost_map_guard_is_set_before_litellm_import():
    # LiteLLM would otherwise fetch its price map from the internet at import time.
    assert os.environ.get("LITELLM_LOCAL_MODEL_COST_MAP") == "True"


def test_litellm_telemetry_is_disabled_in_env_and_on_the_module():
    # Telemetry threads must not attempt outbound connections (offline-first).
    assert os.environ.get("LITELLM_TELEMETRY") == "False"
    litellm = llm_mod._import_litellm()
    assert litellm.telemetry is False


def test_preload_runs_even_when_the_runtime_is_unreachable(make_client, empty_db):
    # The import is local-only, so it happens at startup whether or not llama-server is up (no 6 s stall later).
    calls = []

    class Recorder:
        def preload(self):
            calls.append("preload")

        def complete(self, messages, tools):
            raise AssertionError("not called in this test")

        def unload(self):
            pass

    client = make_client(db_path=empty_db, backend=Recorder())  # default probe: llama-server unreachable
    assert client.app.state.svc.warm.is_set()
    assert calls == ["preload"]


def _fake_litellm(chunks, captured):
    module = types.ModuleType("litellm")

    def completion(**kwargs):
        captured.update(kwargs)
        return iter(chunks)

    module.completion = completion
    return module


def test_model_name_comes_from_config_only(monkeypatch):
    captured: dict = {}
    chunks = [{"choices": [{"delta": {"content": "Hello"}}]}, {"choices": [{"delta": {"content": " there"}}]}]
    monkeypatch.setitem(sys.modules, "litellm", _fake_litellm(chunks, captured))
    cfg = offline_config(**{"models.chat": "some-other-chat-model"})
    backend = LlamaCppChatBackend(cfg)
    turn = backend.complete([{"role": "user", "content": "hi"}], None)
    assert captured["model"] == "openai/some-other-chat-model"
    assert captured["api_base"] == f"http://127.0.0.1:{OFFLINE_CHAT_PORT}/v1"
    assert captured["stream"] is True
    assert "tools" not in captured
    assert turn.text == "Hello there"
    assert turn.first_token_ms is not None


def test_tool_call_deltas_are_assembled(monkeypatch):
    captured: dict = {}
    chunks = [
        {"choices": [{"delta": {"tool_calls": [{"index": 0, "id": "call_9",
                                                 "function": {"name": "search_manuals", "arguments": '{"query": '}}]}}]},
        {"choices": [{"delta": {"tool_calls": [{"index": 0, "function": {"arguments": '"torque"}'}}]}}]},
    ]
    monkeypatch.setitem(sys.modules, "litellm", _fake_litellm(chunks, captured))
    backend = LlamaCppChatBackend(offline_config())
    tools = [{"type": "function", "function": {"name": "search_manuals", "parameters": {}}}]
    turn = backend.complete([{"role": "user", "content": "torque?"}], tools)
    assert captured["tools"] == tools
    assert len(turn.tool_calls) == 1
    call = turn.tool_calls[0]
    assert call.id == "call_9" and call.name == "search_manuals"
    assert call.arguments == '{"query": "torque"}'


def test_object_style_chunks_are_read_too(monkeypatch):
    class Obj:
        def __init__(self, **kw):
            self.__dict__.update(kw)

    delta = Obj(content="Ok", tool_calls=None)
    chunk = Obj(choices=[Obj(delta=delta)])
    monkeypatch.setitem(sys.modules, "litellm", _fake_litellm([chunk], {}))
    turn = LlamaCppChatBackend(offline_config()).complete([{"role": "user", "content": "x"}], None)
    assert turn.text == "Ok" and turn.tool_calls == []


def test_real_litellm_against_a_closed_port_maps_to_ai_unavailable():
    # No mock: this is the real LiteLLM -> httpx path. The port is closed, so it must fail fast and typed.
    backend = LlamaCppChatBackend(offline_config(), timeout_s=5.0)
    with pytest.raises(ChatUnavailable) as info:
        backend.complete([{"role": "user", "content": "hello"}], None)
    assert info.value.code == "ai_unavailable"
    assert "not reachable" in info.value.detail


def test_map_error_names_timeouts_as_starting():
    class Timeout(Exception):
        pass

    err = llm_mod._map_error(Timeout("slow"))
    assert err.code == "ai_starting"


def test_map_error_does_not_leak_message_text():
    err = llm_mod._map_error(ValueError("secret prompt content"))
    assert err.code == "ai_unavailable"
    assert "secret prompt content" not in err.detail


def test_idle_unloader_fires_once_per_idle_period():
    now = {"t": 0.0}
    calls = []
    unloader = IdleUnloader(10, lambda: calls.append(now["t"]), clock=lambda: now["t"])
    now["t"] = 5 * 60
    assert unloader.maybe_unload() is False
    now["t"] = 10 * 60 + 1
    assert unloader.maybe_unload() is True
    assert unloader.maybe_unload() is False  # already fired for this idle period
    unloader.touch()  # traffic re-arms it
    now["t"] = 30 * 60
    assert unloader.maybe_unload() is True
    assert len(calls) == 2


def test_idle_unloader_disabled_when_zero():
    calls = []
    unloader = IdleUnloader(0, lambda: calls.append(1), clock=lambda: 10_000.0)
    assert unloader.maybe_unload() is False
    assert calls == []


def test_idle_unloader_survives_a_failing_unload():
    def boom():
        raise OSError("llama-server gone")

    unloader = IdleUnloader(1, boom, clock=iter([0.0, 120.0]).__next__)
    assert unloader.maybe_unload() is True  # reported, not raised
