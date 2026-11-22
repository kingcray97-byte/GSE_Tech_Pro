"""Phase 7.5a speech to text: `POST /voice/transcribe` (ARCHITECTURE §4.4 `voice_transcribe`).

One model turn: system prompt prompts/asr.md ("Perform ASR.") and the clip as an OpenAI `input_audio` part, no tools.
The engine check (PLAN 7.5) found that LFM3-Omni with an ordinary "transcribe this" prompt sometimes *answers* a spoken
question and can invent a value; with "Perform ASR." it transcribed every clip word for word. So:
- the prompt is fixed in a file and nothing from the request is added to it;
- a transcript much longer than the audio could hold is refused, not returned (an answer, not a transcript);
- the text is only ever a suggestion for the question box or a command the app reads back: nothing is saved from here.
Voice stays on the tablet: the endpoint never uses a remote route.
"""

from __future__ import annotations

import array
import base64
import binascii
import math
import re
import struct
import sys
import time
from dataclasses import dataclass, field
from typing import Any, Optional

from app.llm import ChatBackend

MAX_SECONDS = 30.0  # matches Core Voice.MaxSeconds
MIN_SECONDS = 0.3
MAX_AUDIO_BYTES = 3 * 1024 * 1024  # 30 s of 48 kHz 16-bit mono is 2.9 MB
MAX_WORDS_PER_SECOND = 5.0  # fast speech is about 3; above 5 plus slack the text did not come from the clip
WORD_SLACK = 6
# Loudest 100 ms window below this (dBFS) = no speech. The in-app check heard "I'm sorry." from 3 s of a quiet room,
# so near-silence never reaches the model. Speech close to a tablet microphone is far above this; tune in 7.5d.
SILENCE_DBFS = -45.0

NOTE_EMPTY = "No speech was heard. Hold the tablet closer and try again."
NOTE_RUNAWAY = (
    "The transcript was longer than the recording could hold, so it was thrown away (the model may have answered instead "
    "of transcribing). Try again."
)
NOTE_SIMULATED = "SIMULATED microphone: the words were spoken by a Windows voice from typed text."


class AudioError(ValueError):
    """The clip is missing, too large or long, not base64, or not a 16-bit PCM WAV."""


@dataclass
class WavInfo:
    sample_rate: int
    channels: int
    bits: int
    seconds: float
    data_offset: int = 0
    data_len: int = 0


def parse_wav(data: bytes) -> WavInfo:
    """Reads the RIFF/WAVE header: PCM 16-bit only; duration from the data chunk size."""
    if len(data) < 44 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise AudioError("The recording is not a WAV file.")
    pos, fmt, data_len = 12, None, None
    while pos + 8 <= len(data):
        cid, size = data[pos:pos + 4], struct.unpack_from("<I", data, pos + 4)[0]
        body = pos + 8
        if cid == b"fmt " and size >= 16:
            fmt = struct.unpack_from("<HHIIHH", data, body)
        elif cid == b"data":
            data_len = min(size, len(data) - body)
            data_offset = body
            break
        pos = body + size + (size & 1)
    if fmt is None or data_len is None:
        raise AudioError("The recording is not a complete WAV file.")
    audio_format, channels, rate, _byte_rate, _align, bits = fmt
    if audio_format not in (1, 0xFFFE) or bits != 16 or channels not in (1, 2) or not 8000 <= rate <= 48000:
        raise AudioError("The recording must be 16-bit PCM WAV, mono or stereo, 8-48 kHz.")
    seconds = data_len / float(rate * channels * 2)
    return WavInfo(rate, channels, bits, seconds, data_offset, data_len)


def loudest_window_dbfs(data: bytes, info: WavInfo, window_s: float = 0.1) -> float:
    """RMS level (dBFS) of the loudest 100 ms window: one number that separates speech from a quiet room."""
    raw = data[info.data_offset:info.data_offset + info.data_len - (info.data_len % 2)]
    samples = array.array("h")
    samples.frombytes(raw)
    if sys.byteorder != "little":
        samples.byteswap()
    n = max(1, int(info.sample_rate * info.channels * window_s))
    best = 0.0
    for start in range(0, len(samples), n):
        chunk = samples[start:start + n]
        if chunk:
            best = max(best, sum(v * v for v in chunk) / len(chunk))
    return -120.0 if best <= 0 else 10.0 * math.log10(best / (32768.0 ** 2))


def decode_audio(audio_base64: str) -> tuple[bytes, WavInfo]:
    text = (audio_base64 or "").strip()
    if not text:
        raise AudioError("No recording was sent.")
    if len(text) > (MAX_AUDIO_BYTES * 4) // 3 + 8:
        raise AudioError("The recording is too long (30 seconds at most).")
    try:
        data = base64.b64decode(text, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise AudioError("The recording could not be decoded.") from exc
    info = parse_wav(data)
    if info.seconds > MAX_SECONDS + 0.5:
        raise AudioError("The recording is too long (30 seconds at most).")
    if info.seconds < MIN_SECONDS:
        raise AudioError("The recording is too short. Hold Talk while you speak.")
    return data, info


def build_messages(system_prompt: str, audio_base64: str) -> list[dict[str, Any]]:
    return [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": [{"type": "input_audio", "input_audio": {"data": audio_base64.strip(), "format": "wav"}}]},
    ]


_QUOTES = "\"'“”‘’"


def clean_transcript(text: str) -> str:
    t = " ".join((text or "").split())
    t = re.sub(r"^(?:transcript(?:ion)?\s*:\s*)", "", t, flags=re.IGNORECASE)
    if len(t) >= 2 and t[0] in _QUOTES and t[-1] in _QUOTES:
        t = t[1:-1].strip()
    return t


def is_runaway(text: str, seconds: float) -> bool:
    return len(text.split()) > MAX_WORDS_PER_SECOND * seconds + WORD_SLACK


@dataclass
class TranscriptResult:
    text: str
    seconds: float
    notes: list[str] = field(default_factory=list)
    model_ms: float = 0.0
    refused: bool = False
    level_dbfs: Optional[float] = None


def transcribe(*, backend: ChatBackend, system_prompt: str, audio_base64: str, seconds: float,
               is_simulated: bool, level_dbfs: Optional[float] = None) -> TranscriptResult:
    """One model turn on the clip, unless it is silent. Raises ChatUnavailable from the backend."""
    if level_dbfs is not None and level_dbfs < SILENCE_DBFS:
        notes = ([NOTE_SIMULATED] if is_simulated else []) + [NOTE_EMPTY]
        return TranscriptResult("", seconds, notes, 0.0, level_dbfs=level_dbfs)
    started = time.perf_counter()
    turn = backend.complete(build_messages(system_prompt, audio_base64), None)
    model_ms = (time.perf_counter() - started) * 1000.0
    notes = [NOTE_SIMULATED] if is_simulated else []
    text = clean_transcript(turn.text)
    if not text:
        return TranscriptResult("", seconds, notes + [NOTE_EMPTY], model_ms)
    if is_runaway(text, seconds):
        return TranscriptResult("", seconds, notes + [NOTE_RUNAWAY], model_ms, refused=True, level_dbfs=level_dbfs)
    return TranscriptResult(text, seconds, notes, model_ms, level_dbfs=level_dbfs)
