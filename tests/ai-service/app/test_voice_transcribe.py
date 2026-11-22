"""Phase 7.5a speech to text (POST /voice/transcribe): the clip reaches the tablet model as audio with the fixed ASR
prompt, bad or over-long clips are refused before the model, a transcript the clip could not hold is thrown away,
voice never takes a remote route, and a tablet without the projector says so. Scripted backends; nothing leaves 127.0.0.1."""

from __future__ import annotations

import base64
import io
import math
import struct
import wave

import pytest

from ai_support import fake_probe, offline_config
from test_chat_endpoint import ScriptedBackend, text_turn
from test_routing import ROUTES_ON, Health

from app.prompts import load_prompt
from voice.transcribe import (
    NOTE_EMPTY,
    NOTE_RUNAWAY,
    NOTE_SIMULATED,
    AudioError,
    clean_transcript,
    decode_audio,
    is_runaway,
    parse_wav,
)

TABLET_UP = ("lfm3-omni", "nomic-embed-text")


def wav(seconds: float = 2.0, rate: int = 16000, channels: int = 1, width: int = 2, amplitude: int = 8000) -> bytes:
    """A 440 Hz tone (speech-like level, about -15 dBFS); amplitude=0 is silence."""
    buf = io.BytesIO()
    n = int(seconds * rate)
    with wave.open(buf, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(width)
        w.setframerate(rate)
        if width == 2:
            frames = b"".join(struct.pack("<h", int(amplitude * math.sin(2 * math.pi * 440 * i / rate))) * channels
                              for i in range(n))
        else:
            frames = b"\x80" * n * channels
        w.writeframes(frames)
    return buf.getvalue()


def _b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def _client(make_client, db, backend):
    return make_client(db_path=db, backend=backend, probe_fn=fake_probe(TABLET_UP))


# ------------------------------------------------------------------ endpoint


def test_clip_is_transcribed_on_the_tablet_with_the_asr_prompt(make_client, seeded_db):
    tablet = ScriptedBackend([text_turn(' "Reading is 2300 psi." ')])
    client = _client(make_client, seeded_db, tablet)
    res = client.post("/voice/transcribe", json={"audio_base64": _b64(wav(3.5)), "is_simulated": True})
    assert res.status_code == 200, res.text
    body = res.json()
    assert body == {"text": "Reading is 2300 psi.", "notes": [NOTE_SIMULATED], "audio_seconds": 3.5,
                    "route": "tablet", "model": "lfm3-omni"}
    messages, tools = tablet.calls[0]
    assert tools is None
    assert messages[0] == {"role": "system", "content": load_prompt("asr.md")}
    assert load_prompt("asr.md") == "Perform ASR."  # the prompt the engine check proved; nothing else is added
    parts = messages[1]["content"]
    assert parts == [{"type": "input_audio", "input_audio": {"data": _b64(wav(3.5)), "format": "wav"}}]


def test_an_answer_instead_of_a_transcript_is_thrown_away(make_client, seeded_db):
    """The engine check saw LFM3-Omni answer a spoken question; a text far longer than the clip is refused."""
    answer = "The main relief valve pressure is 2500 psi according to the manual, so set it there and check the gauge again " * 2
    client = _client(make_client, seeded_db, ScriptedBackend([text_turn(answer)]))
    body = client.post("/voice/transcribe", json={"audio_base64": _b64(wav(2.0))}).json()
    assert body["text"] == "" and body["notes"] == [NOTE_RUNAWAY]


def test_empty_answer_says_nothing_was_heard(make_client, seeded_db):
    client = _client(make_client, seeded_db, ScriptedBackend([text_turn("   ")]))
    body = client.post("/voice/transcribe", json={"audio_base64": _b64(wav(1.0))}).json()
    assert body["text"] == "" and body["notes"] == [NOTE_EMPTY]


def test_a_quiet_room_never_reaches_the_model(make_client, seeded_db):
    """Found in the in-app check: 3 s of a quiet room came back as "I'm sorry." Near-silence is answered by rule."""
    tablet = ScriptedBackend([])  # any model call would fail the test
    client = _client(make_client, seeded_db, tablet)
    for amplitude in (0, 120):  # digital silence, and room hiss at about -52 dBFS
        body = client.post("/voice/transcribe", json={"audio_base64": _b64(wav(3.0, amplitude=amplitude))}).json()
        assert body["text"] == "" and body["notes"] == [NOTE_EMPTY]
    assert tablet.calls == []


@pytest.mark.parametrize(
    "data",
    [
        b"not a wav file at all, just bytes" * 3,
        wav(31.5),  # too long
        wav(0.1),  # too short
        wav(1.0, width=1),  # 8-bit
        wav(1.0, rate=96000),
    ],
)
def test_bad_clips_are_refused_before_the_model(make_client, seeded_db, data):
    tablet = ScriptedBackend([])
    client = _client(make_client, seeded_db, tablet)
    res = client.post("/voice/transcribe", json={"audio_base64": _b64(data)})
    assert res.status_code == 400, res.text
    assert tablet.calls == []


def test_model_down_is_an_honest_503(make_client, seeded_db):
    client = make_client(db_path=seeded_db, backend=ScriptedBackend([]))  # default probe: llama-server down
    res = client.post("/voice/transcribe", json={"audio_base64": _b64(wav(1.0))})
    assert res.status_code == 503 and res.json()["error"]["code"] == "ai_unavailable"


def test_voice_never_takes_a_remote_route(make_client, seeded_db):
    tablet = ScriptedBackend([text_turn("Next step.")])
    shop, cloud = ScriptedBackend([]), ScriptedBackend([])
    client = make_client(
        db_path=seeded_db, backend=tablet, probe_fn=fake_probe(TABLET_UP), overrides=ROUTES_ON,
        route_health=Health(offline_config(**ROUTES_ON)), remote_backends={"shop-server": shop, "cloud": cloud},
    )
    body = client.post("/voice/transcribe", json={"audio_base64": _b64(wav(1.0))}).json()
    assert body["route"] == "tablet" and body["text"] == "Next step."
    assert shop.calls == [] and cloud.calls == []


def test_needs_the_token(make_client, seeded_db):
    client = _client(make_client, seeded_db, ScriptedBackend([]))
    res = client.post("/voice/transcribe", json={"audio_base64": _b64(wav(1.0))}, headers={"X-GSE-Token": "nope"})
    assert res.status_code == 401


# ------------------------------------------------------------------ rules (no HTTP)


def test_parse_wav_reads_rate_channels_and_length():
    info = parse_wav(wav(2.5, rate=22050, channels=2))
    assert (info.sample_rate, info.channels, info.bits) == (22050, 2, 16)
    assert info.seconds == pytest.approx(2.5, abs=0.01)
    with pytest.raises(AudioError):
        parse_wav(b"RIFF\x00\x00\x00\x00WAVEjunk")


def test_decode_audio_limits():
    data, info = decode_audio(_b64(wav(30.0)))
    assert info.seconds == pytest.approx(30.0, abs=0.01) and data.startswith(b"RIFF")
    for bad in ("", "!!not base64!!"):
        with pytest.raises(AudioError):
            decode_audio(bad)


def test_loudness_separates_speech_level_from_a_quiet_room():
    from voice.transcribe import SILENCE_DBFS, loudest_window_dbfs

    def level(amplitude):
        data = wav(1.0, amplitude=amplitude)
        return loudest_window_dbfs(data, parse_wav(data))

    assert level(0) == -120.0
    assert level(120) < SILENCE_DBFS < level(8000)
    assert level(8000) == pytest.approx(-15.3, abs=0.5)  # sine RMS = amplitude / sqrt(2)


def test_clean_transcript_and_runaway_rule():
    assert clean_transcript('  Transcript: "Next   step."  ') == "Next step."
    assert clean_transcript("“Yes, save it.”") == "Yes, save it."
    assert not is_runaway("Add note left lift cylinder hose is wet at the lower fitting", 4.9)
    assert is_runaway(" ".join(["word"] * 30), 2.0)
