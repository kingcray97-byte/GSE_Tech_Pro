"""Phase 7.4 photo inspection: `POST /vision/inspect` (ARCHITECTURE §4.4 `vision_inspect`, §4.5 safety rules).

Flow: check the image -> one model turn with the photo and prompts/vision.md (no tools) -> rule-based notes and
ID redaction -> manual search on the description's "Look up:" words (plus the caption) -> manual pages to compare.

What the model may and may not do is enforced here, not trusted to the prompt:
- Manual pages come only from our own search, never from model text.
- A serial / part number / VIN the model writes anyway is replaced with a pointer to Read data plate (rule 5).
- A number with a unit gets a note that it is not a measurement; verdict words get a note that the AI does not
  judge pass/fail. The description itself is otherwise returned as written, labelled as a suggestion.
"""

from __future__ import annotations

import base64
import binascii
import re
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from app.chat import _SPEC_LIKE
from app.llm import ChatBackend
from app.models import Citation

MAX_IMAGE_BYTES = 4 * 1024 * 1024  # matches Core PhotoInspection.MaxImageBytes
MAX_CAPTION_CHARS = 500
MAX_DESCRIPTION_CHARS = 1200
MAX_PAGES = 3
MAX_QUERY_CHARS = 300

JPEG = "image/jpeg"
PNG = "image/png"
_MAGIC = {JPEG: (b"\xff\xd8\xff",), PNG: (b"\x89PNG\r\n\x1a\n",)}

NOTE_SIMULATED = "SIMULATED photo from the simulator camera: the description is a test of the workflow, not of real equipment."
NOTE_NUMBERS = (
    "A number in the description was read from the photo by the AI. It is not a measurement: "
    "measure or read the gauge yourself and enter the reading."
)
NOTE_VERDICT = "The AI does not judge pass or fail. Compare with the manual pages and decide yourself."
NOTE_ID_REDACTED = "An ID the AI tried to read was removed. Read serial and data-plate text with Read data plate (Camera)."
NOTE_NO_PAGES = "No manual page matched this description. Try Manual Search with your own words."
NOTE_NO_SEARCH = "Manual search is not available right now, so no pages are suggested."
NOTE_EMPTY = "The AI gave no description. Try a closer, sharper photo with better light."
ID_PLACEHOLDER = "[not read by the AI - use Read data plate]"

# Label + value. The value must contain a digit so ordinary words after "part" are left alone. A second, short label
# may sit between them ("the serial number is SN 4711-AB", found in the in-app check), and is kept with the label.
_ID_LABEL = (
    r"(?:serial(?:\s*(?:no\.?|number|#))?|s\s*/\s*n|sn|vin|part\s*(?:no\.?|number|#)|p\s*/\s*n|pn|"
    r"model\s*(?:no\.?|number|#))(?![A-Za-z])"
)
_ID_VALUE = re.compile(
    r"(?P<label>\b" + _ID_LABEL + r"\s*(?:is|reads|shows|:|#|=)?\s*"
    r"(?:(?:" + _ID_LABEL + r"|no\.?|number|#)\s*[:#=]?\s*)?)"
    r"(?P<value>(?=[A-Z0-9./\-]*\d)[A-Z0-9][A-Z0-9./\-]{1,}[A-Z0-9])",  # ends on a letter/digit: keeps the full stop
    re.IGNORECASE,
)
# A long run of letters and digits (VIN-like) anywhere: 11+ chars with both letters and digits.
_LONG_ID = re.compile(r"\b(?=[A-Z0-9]*\d)(?=[A-Z0-9]*[A-Z])[A-Z0-9]{11,17}\b", re.IGNORECASE)
_VERDICT = re.compile(
    r"\b(?:pass(?:es|ed)?|fail(?:s|ed|ing)?|within\s+(?:spec|limits?|tolerance)|out\s+of\s+(?:spec|limits?|tolerance)|"
    r"in\s+spec|meets?\s+spec|acceptable|unacceptable|serviceable|unserviceable)\b",
    re.IGNORECASE,
)
# Anywhere in the text: the real LFM3-Omni often writes "... Look up: lift hose leak" on the same line (in-app check).
_LOOK_UP = re.compile(r"look\s*up\s*:\s*(?P<terms>[^\n]+)", re.IGNORECASE)


class ImageError(ValueError):
    """The image is missing, too large, not base64, or not the media type it claims."""


def decode_image(image_base64: str, media_type: str) -> bytes:
    """Base64 -> bytes, checked against the size cap and the JPEG / PNG signature."""
    if media_type not in _MAGIC:
        raise ImageError("Only JPEG and PNG photos can be inspected.")
    text = (image_base64 or "").strip()
    if not text:
        raise ImageError("No photo was sent.")
    if len(text) > (MAX_IMAGE_BYTES * 4) // 3 + 8:
        raise ImageError("The photo is too large to inspect (4 MB at most).")
    try:
        data = base64.b64decode(text, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise ImageError("The photo could not be decoded.") from exc
    if not data or len(data) > MAX_IMAGE_BYTES:
        raise ImageError("The photo is too large to inspect (4 MB at most).")
    if not any(data.startswith(m) for m in _MAGIC[media_type]):
        raise ImageError("The photo does not match its type (JPEG or PNG).")
    return data


def build_messages(system_prompt: str, image_base64: str, media_type: str, caption: Optional[str]) -> list[dict[str, Any]]:
    """One system message and one user message holding the question and the photo (OpenAI image_url format)."""
    ask = "Describe what this photo shows."
    cleaned = " ".join((caption or "").split())[:MAX_CAPTION_CHARS]
    if cleaned:
        ask += f" The technician says: {cleaned}"
    return [
        {"role": "system", "content": system_prompt},
        {
            "role": "user",
            "content": [
                {"type": "text", "text": ask},
                {"type": "image_url", "image_url": {"url": f"data:{media_type};base64,{image_base64.strip()}"}},
            ],
        },
    ]


def redact_ids(text: str) -> tuple[str, bool]:
    """Replace ID values the model wrote (serial, part number, VIN, model number) with a pointer to OCR."""
    changed = False

    def _label(m: re.Match[str]) -> str:
        nonlocal changed
        changed = True
        return m.group("label") + ID_PLACEHOLDER

    out = _ID_VALUE.sub(_label, text)

    def _long(m: re.Match[str]) -> str:
        nonlocal changed
        changed = True
        return ID_PLACEHOLDER

    out = _LONG_ID.sub(_long, out)
    return out, changed


def search_terms(description: str, caption: Optional[str]) -> str:
    """The model's "Look up:" words when present (else the description), with the caption first. No numbers."""
    m = _LOOK_UP.search(description)
    base = m.group("terms") if m else description
    words = " ".join(filter(None, [" ".join((caption or "").split()), base]))
    words = re.sub(r"\[not read by the AI[^\]]*\]", " ", words)
    words = re.sub(r"[^A-Za-z\s\-]", " ", words)  # numbers and punctuation do not help BM25 here
    return " ".join(words.split())[:MAX_QUERY_CHARS]


@dataclass
class InspectionResult:
    description: str
    notes: list[str] = field(default_factory=list)
    citations: list[Citation] = field(default_factory=list)
    search_query: Optional[str] = None
    model_ms: float = 0.0
    redacted: bool = False


SearchFn = Callable[[str, int], list[dict[str, Any]]]


def manual_search_fn(db_path: Optional[Path], embedder: Any) -> Optional[SearchFn]:
    """Manual search bound to the service database, or None when there is no database."""
    if db_path is None or not Path(db_path).is_file():
        return None
    from tools.manuals import run_manual_search

    def _search(query: str, top_k: int) -> list[dict[str, Any]]:
        out = run_manual_search(db_path, query, top_k, query_embedder=embedder, include_text=False)
        return list(out.get("results") or [])

    return _search


def inspect_photo(
    *,
    backend: ChatBackend,
    system_prompt: str,
    image_base64: str,
    media_type: str,
    caption: Optional[str],
    is_simulated_photo: bool,
    search: Optional[SearchFn],
) -> InspectionResult:
    """One model turn on the photo, then rule-based notes and manual pages. Raises ChatUnavailable from the backend."""
    started = time.perf_counter()
    turn = backend.complete(build_messages(system_prompt, image_base64, media_type, caption), None)
    model_ms = (time.perf_counter() - started) * 1000.0

    raw = (turn.text or "").strip()[:MAX_DESCRIPTION_CHARS]
    notes: list[str] = []
    if is_simulated_photo:
        notes.append(NOTE_SIMULATED)
    if not raw:
        notes.append(NOTE_EMPTY)
        return InspectionResult(description="", notes=notes, model_ms=model_ms)

    description, redacted = redact_ids(raw)
    if redacted:
        notes.append(NOTE_ID_REDACTED)
    if _SPEC_LIKE.search(description):
        notes.append(NOTE_NUMBERS)
    if _VERDICT.search(description):
        notes.append(NOTE_VERDICT)

    query = search_terms(description, caption)
    citations: list[Citation] = []
    if search is None:
        notes.append(NOTE_NO_SEARCH)
    elif query:
        try:
            results = search(query, MAX_PAGES)
        except Exception:  # noqa: BLE001 - a search failure must not lose the description
            results = []
            notes.append(NOTE_NO_SEARCH)
        seen: set[str] = set()
        for r in results:
            text = str(r.get("citation") or "")
            if not text or text in seen:
                continue
            seen.add(text)
            citations.append(
                Citation(kind="manual-page", display_text=text, manual_id=r.get("manual_id"), page_start=r.get("page_start"))
            )
        if not citations and NOTE_NO_SEARCH not in notes:
            notes.append(NOTE_NO_PAGES)
    else:
        notes.append(NOTE_NO_PAGES)

    shown = _LOOK_UP.sub("", description).strip()  # the search words are shown on their own line in the app
    return InspectionResult(
        description=shown or description,
        notes=notes,
        citations=citations,
        search_query=query or None,
        model_ms=model_ms,
        redacted=redacted,
    )
