"""Phase 7.4 photo inspection (POST /vision/inspect): the photo reaches the model as an image, manual pages come only
from our own search, IDs and numbers are handled by rule, photos stay off the cloud unless job details may go there,
and a tablet without an image projector says so. Scripted backends; nothing leaves 127.0.0.1."""

from __future__ import annotations

import base64

import pytest

from ai_support import fake_probe, offline_config
from test_chat_endpoint import ScriptedBackend, text_turn
from test_routing import ROUTES_ON, Health

from app.api import tablet_vision_configured
from app.llm import ChatUnavailable
from app.prompts import load_prompt
from vision.inspect import (
    ID_PLACEHOLDER,
    NOTE_ID_REDACTED,
    NOTE_NO_PAGES,
    NOTE_NO_SEARCH,
    NOTE_NUMBERS,
    NOTE_SIMULATED,
    NOTE_VERDICT,
    ImageError,
    decode_image,
    inspect_photo,
    redact_ids,
    search_terms,
)

TABLET_UP = ("lfm3-omni", "nomic-embed-text")
JPEG = b"\xff\xd8\xff\xe0" + b"\x00" * 64
PNG = b"\x89PNG\r\n\x1a\n" + b"\x00" * 64
DESCRIPTION = (
    "Wet oily area on the lower hydraulic hose fitting near the lift cylinder; drips on the frame below.\n"
    "Look up: hydraulic hose fitting leak lift cylinder"
)


def _b64(data: bytes = JPEG) -> str:
    return base64.b64encode(data).decode("ascii")


def _body(**extra):
    body = {"image_base64": _b64(), "media_type": "image/jpeg", "caption": "leak at the lift", "work_order_id": "WO-1"}
    body.update(extra)
    return body


def _image_part(messages):
    user = messages[-1]
    assert user["role"] == "user" and isinstance(user["content"], list)
    return next(p for p in user["content"] if p["type"] == "image_url")


# ------------------------------------------------------------------ endpoint


def test_tablet_describes_the_photo_and_suggests_manual_pages(make_client, seeded_db):
    tablet = ScriptedBackend([text_turn(DESCRIPTION)])
    client = make_client(db_path=seeded_db, backend=tablet, probe_fn=fake_probe(TABLET_UP))
    res = client.post("/vision/inspect", json=_body())
    assert res.status_code == 200, res.text
    body = res.json()
    assert body["route"] == "tablet" and body["model"] == "lfm3-omni"
    assert body["is_suggestion"] is True
    assert body["description"].startswith("Wet oily area")
    assert body["citations"], "the seeded manual should match a hydraulic leak"
    assert all(c["kind"] == "manual-page" and c["page_start"] for c in body["citations"])
    assert len(body["citations"]) <= 3
    assert "hydraulic hose fitting leak" in body["search_query"]
    # The photo went to the model as an OpenAI image part, with the vision prompt and no tools.
    messages, tools = tablet.calls[0]
    assert tools is None
    assert messages[0]["content"] == load_prompt("vision.md")
    assert _image_part(messages)["image_url"]["url"] == "data:image/jpeg;base64," + _b64()
    assert "leak at the lift" in messages[-1]["content"][0]["text"]


def test_model_text_cannot_add_a_manual_page(make_client, seeded_db):
    """A page the model names is not a citation: citations come only from the service's search."""
    tablet = ScriptedBackend([text_turn("See Operations Manual, page 999 for this. Look up: hydraulic leak")])
    client = make_client(db_path=seeded_db, backend=tablet, probe_fn=fake_probe(TABLET_UP))
    body = client.post("/vision/inspect", json=_body(caption=None)).json()
    assert body["citations"]
    assert all(c["page_start"] != 999 and "Operations Manual" not in c["display_text"] for c in body["citations"])


def test_no_matching_page_says_so():
    r = _run("Something odd. Look up: whatever", search=lambda q, k: [])
    assert r.citations == [] and NOTE_NO_PAGES in r.notes


def test_simulated_photo_is_labelled(make_client, seeded_db):
    tablet = ScriptedBackend([text_turn(DESCRIPTION)])
    client = make_client(db_path=seeded_db, backend=tablet, probe_fn=fake_probe(TABLET_UP))
    body = client.post("/vision/inspect", json=_body(is_simulated_photo=True)).json()
    assert body["notes"][0] == NOTE_SIMULATED


@pytest.mark.parametrize(
    "data,media,expected",
    [
        (b"", "image/jpeg", 422),  # empty string fails the request model
        (PNG, "image/jpeg", 400),  # wrong signature for the type
        (b"GIF89a" + b"\x00" * 10, "image/png", 400),
    ],
)
def test_bad_images_are_refused_before_the_model(make_client, seeded_db, data, media, expected):
    tablet = ScriptedBackend([])
    client = make_client(db_path=seeded_db, backend=tablet, probe_fn=fake_probe(TABLET_UP))
    res = client.post("/vision/inspect", json=_body(image_base64=_b64(data) if data else "", media_type=media))
    assert res.status_code == expected
    assert tablet.calls == []


def test_gif_media_type_is_rejected(make_client, seeded_db):
    client = make_client(db_path=seeded_db, backend=ScriptedBackend([]), probe_fn=fake_probe(TABLET_UP))
    assert client.post("/vision/inspect", json=_body(media_type="image/gif")).status_code == 422


def test_model_down_is_an_honest_503(make_client, seeded_db):
    client = make_client(db_path=seeded_db, backend=ScriptedBackend([]))  # default probe: llama-server down
    res = client.post("/vision/inspect", json=_body())
    assert res.status_code == 503
    assert res.json()["error"]["code"] == "ai_unavailable"


def test_needs_the_token(make_client, seeded_db):
    client = make_client(db_path=seeded_db, backend=ScriptedBackend([]), probe_fn=fake_probe(TABLET_UP))
    assert client.post("/vision/inspect", json=_body(), headers={"X-GSE-Token": "wrong"}).status_code == 401


# ------------------------------------------------------------------ routes (ADR-013: a photo is a job detail)


def _routed(make_client, db, *, tablet, shop=None, cloud=None, shop_ok=True):
    cfg = offline_config(**ROUTES_ON)
    return make_client(
        db_path=db,
        backend=tablet,
        probe_fn=fake_probe(TABLET_UP),
        overrides=ROUTES_ON,
        route_health=Health(cfg, shop_ok=shop_ok),
        remote_backends={k: v for k, v in (("shop-server", shop), ("cloud", cloud)) if v is not None},
    )


def _routing(share: bool, mode: str = "prefer-cloud"):
    return {"mode": mode, "cloud_allowed": True, "share_job_details_with_cloud": share}


def test_photo_skips_the_cloud_unless_job_details_may_go(make_client, seeded_db):
    tablet, cloud = ScriptedBackend([text_turn(DESCRIPTION)]), ScriptedBackend([])
    client = _routed(make_client, seeded_db, tablet=tablet, cloud=cloud)
    body = client.post("/vision/inspect", json=_body(routing=_routing(share=False))).json()
    assert cloud.calls == []
    assert body["route"] == "tablet"


def test_photo_goes_to_the_cloud_when_job_details_are_shared(make_client, seeded_db):
    tablet, cloud = ScriptedBackend([]), ScriptedBackend([text_turn(DESCRIPTION)])
    client = _routed(make_client, seeded_db, tablet=tablet, cloud=cloud)
    body = client.post("/vision/inspect", json=_body(routing=_routing(share=True))).json()
    assert body["route"] == "cloud" and body["is_suggestion"] is True
    assert body["citations"]  # pages still come from the tablet's own search
    assert tablet.calls == []


def test_shop_server_gets_the_photo_and_a_failure_falls_back_to_the_tablet(make_client, seeded_db):
    tablet = ScriptedBackend([text_turn(DESCRIPTION), text_turn(DESCRIPTION)])
    shop = ScriptedBackend([ChatUnavailable("ai_unavailable", "model takes no images"), text_turn(DESCRIPTION)])
    client = _routed(make_client, seeded_db, tablet=tablet, shop=shop)
    first = client.post("/vision/inspect", json=_body(routing=_routing(False, "prefer-shop-server"))).json()
    assert first["route"] == "tablet" and len(shop.calls) == 1
    # A vision failure does not mark the shop server down for chat: it is tried again next time.
    second = client.post("/vision/inspect", json=_body(routing=_routing(False, "prefer-shop-server"))).json()
    assert second["route"] == "shop-server"


def test_tablet_without_a_projector_cannot_inspect():
    cfg = offline_config()
    assert tablet_vision_configured(cfg)  # attach mode: the running llama-server decides
    cfg.llama_server.binary = "C:/GSE/llama-server.exe"
    assert not tablet_vision_configured(cfg)
    cfg.llama_server.mmproj = "C:/GSE/models/mmproj.gguf"
    assert tablet_vision_configured(cfg)


# ------------------------------------------------------------------ rules (no HTTP)


def _run(text, *, caption=None, simulated=False, search=lambda q, k: []):
    return inspect_photo(
        backend=ScriptedBackend([text_turn(text)]),
        system_prompt="p",
        image_base64=_b64(),
        media_type="image/jpeg",
        caption=caption,
        is_simulated_photo=simulated,
        search=search,
    )


def test_ids_the_model_reads_are_removed():
    text, changed = redact_ids("Data plate shows Serial No: 4711-AB and VIN 1FTFW1ET5DFC10312; part number P-100 too.")
    assert changed
    assert "4711" not in text and "1FTFW1ET5DFC10312" not in text and "P-100" not in text
    assert text.count(ID_PLACEHOLDER) == 3
    # Found in the in-app check: a second label between "is" and the value.
    for sentence in ("The serial number is SN 4711-AB.", "Model number: no. 660-28X", "VIN reads #12345"):
        out, changed = redact_ids(sentence)
        assert changed and ID_PLACEHOLDER in out, sentence
        assert not any(ch.isdigit() for ch in out.replace(ID_PLACEHOLDER, "")), out
    assert redact_ids("The serial number is SN 4711-AB. Next part.")[0].endswith(f"{ID_PLACEHOLDER}. Next part.")
    untouched, changed = redact_ids("The part near the serial plate is corroded.")
    assert not changed and untouched == "The part near the serial plate is corroded."
    assert NOTE_ID_REDACTED in _run("The S/N is AB1234.").notes


def test_numbers_and_verdicts_get_notes_but_the_text_is_kept():
    r = _run("Gauge shows about 2300 psi, which looks within spec. Look up: gauge")
    assert "2300 psi" in r.description  # kept, but flagged
    assert NOTE_NUMBERS in r.notes and NOTE_VERDICT in r.notes
    plain = _run("Frayed wire near the connector. Look up: harness connector")
    assert NOTE_NUMBERS not in plain.notes and NOTE_VERDICT not in plain.notes


def test_search_terms_use_look_up_words_and_drop_numbers():
    assert search_terms("Leak.\nLook up: hydraulic hose 3/8 fitting", "left side 2") == "left side hydraulic hose fitting"
    assert search_terms("Corroded battery terminal.", None) == "Corroded battery terminal"


def test_inline_look_up_from_the_real_model_is_used_and_hidden():
    """Found with the real LFM3-Omni: "Look up:" on the same line as the description."""
    seen = []
    r = _run("A grid of grey squares. Look up: lift hose leak check", caption=None,
             search=lambda q, k: seen.append(q) or [])
    assert seen == ["lift hose leak check"]
    assert r.description == "A grid of grey squares." and "Look up" not in r.description


def test_search_failure_or_no_database_keeps_the_description():
    def boom(_q, _k):
        raise RuntimeError("db locked")

    r = _run(DESCRIPTION, search=boom)
    assert r.description.startswith("Wet oily") and NOTE_NO_SEARCH in r.notes and r.citations == []
    assert NOTE_NO_SEARCH in _run(DESCRIPTION, search=None).notes


def test_empty_answer_says_so():
    r = _run("   ")
    assert r.description == "" and r.notes and "closer" in r.notes[-1]


def test_decode_image_limits():
    assert decode_image(_b64(), "image/jpeg") == JPEG
    assert decode_image(_b64(PNG), "image/png") == PNG
    with pytest.raises(ImageError):
        decode_image("not base64!!", "image/jpeg")
    with pytest.raises(ImageError):
        decode_image(_b64(JPEG + b"\x00" * (4 * 1024 * 1024)), "image/jpeg")


def test_vision_prompt_fits_and_forbids_ids_and_verdicts():
    prompt = load_prompt("vision.md")
    assert "Do not read serial numbers" in prompt and "pass, fail" in prompt
