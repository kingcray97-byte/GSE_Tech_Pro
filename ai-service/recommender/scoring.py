"""Pure scoring for the repair-history recommender (ADR-010 §3-§5). No I/O, no SQL.

Every tunable is a named constant below; docs/decisions/010-recommendation-engine.md is the normative source.
Nothing here invents a number: inputs are recorded counts, labor hours, timestamps and signal sets.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass
from datetime import datetime, timezone

# §3 similarity weights. A signal that is undefined for the query is dropped and the rest renormalized.
W_SYMPTOM = 0.45
W_DTC = 0.25
W_SYSTEM = 0.20
W_TEXT = 0.10
SIMILARITY_WEIGHTS = {
    "symptom_sim": W_SYMPTOM,
    "dtc_sim": W_DTC,
    "system_sim": W_SYSTEM,
    "text_sim": W_TEXT,
}

# §4 Wilson score interval, 95% confidence.
WILSON_Z = 1.96

# §4 rank_score = success_rate + W_RANK_SIMILARITY * similarity + W_RANK_RECENCY * recency
W_RANK_SIMILARITY = 0.10
W_RANK_RECENCY = 0.05

# §4 recency step function on age in whole days: (inclusive upper bound, weight). Older than the last bound → floor.
RECENCY_STEPS: tuple[tuple[int, float], ...] = ((30, 1.0), (90, 0.75), (365, 0.5))
RECENCY_FLOOR = 0.25

# Small English stopword list (same spirit as rag.search). No stemming.
STOPWORDS = frozenset(
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how",
        "i", "if", "in", "is", "it", "its", "my", "of", "on", "or", "should", "the", "this", "to",
        "we", "what", "when", "where", "which", "with", "you", "there", "me",
    }
)

# Unicode letters and digits. Underscore and every other non-alphanumeric character split tokens.
_TOKEN_RE = re.compile(r"[^\W_]+")
_FRACTION_RE = re.compile(r"\.(\d+)")


@dataclass(frozen=True)
class QuerySignals:
    """What the trigger work order looks like. Empty sets mean the signal is undefined for this query."""

    symptoms: frozenset[str]
    dtcs: frozenset[tuple[str, str]]
    systems: frozenset[str]
    tokens: frozenset[str]


@dataclass(frozen=True)
class CandidateSignals:
    """One contributing past work order (one repair row). `system` is the repair's normalized system."""

    symptoms: frozenset[str]
    dtcs: frozenset[tuple[str, str]]
    system: str | None
    tokens: frozenset[str]


def normalize_text(value: str | None) -> str:
    """Case-fold and trim for in-Python comparisons (systems, DTC codes, protocols)."""
    return value.strip().lower() if value else ""


def tokenize(text: str | None) -> frozenset[str]:
    """Unicode lowercase, split on non-alphanumeric, drop stopwords. Unigrams only, no stemming."""
    if not text:
        return frozenset()
    return frozenset(t for t in _TOKEN_RE.findall(text.lower()) if t not in STOPWORDS)


def jaccard(a: frozenset[str], b: frozenset[str]) -> float:
    """|a ∩ b| / |a ∪ b|. Both empty is undefined; callers guard that case."""
    union = a | b
    if not union:
        return 0.0
    return len(a & b) / len(union)


def similarity_breakdown(query: QuerySignals, cand: CandidateSignals) -> dict[str, float | None]:
    """Per-signal similarities in [0, 1]. None means the query has no such signal (term dropped)."""
    symptom = len(query.symptoms & cand.symptoms) / len(query.symptoms) if query.symptoms else None
    dtc = len(query.dtcs & cand.dtcs) / len(query.dtcs) if query.dtcs else None
    system = (1.0 if cand.system and cand.system in query.systems else 0.0) if query.systems else None
    text = jaccard(query.tokens, cand.tokens) if query.tokens else None
    return {"symptom_sim": symptom, "dtc_sim": dtc, "system_sim": system, "text_sim": text}


def similarity_composite(query: QuerySignals, cand: CandidateSignals) -> float:
    """The deterministic SimilarityScore (§3). 0.0 when the query has no signal at all."""
    parts = similarity_breakdown(query, cand)
    total_weight = 0.0
    total = 0.0
    for name, value in parts.items():
        if value is None:
            continue
        weight = SIMILARITY_WEIGHTS[name]
        total_weight += weight
        total += weight * value
    if total_weight == 0.0:
        return 0.0
    return total / total_weight


def cosine(a: list[float], b: list[float]) -> float:
    """Cosine similarity. Zero-norm vectors give 0.0. Dimension mismatch is a hard error."""
    if len(a) != len(b):
        raise ValueError(f"vector dimension mismatch: {len(a)} != {len(b)}")
    dot = sum(x * y for x, y in zip(a, b, strict=True))
    norm_a = math.sqrt(sum(x * x for x in a))
    norm_b = math.sqrt(sum(y * y for y in b))
    if norm_a == 0.0 or norm_b == 0.0:
        return 0.0
    return dot / (norm_a * norm_b)


def wilson_lower_bound(successes: int, attempts: int, z: float = WILSON_Z) -> float:
    """Wilson score lower bound (ADR-010 §4). attempts == 0 → 0.0 (such candidates are dropped upstream)."""
    if attempts <= 0:
        return 0.0
    if not 0 <= successes <= attempts:
        raise ValueError("successes must be between 0 and attempts")
    n = float(attempts)
    p = successes / n
    z2 = z * z
    centre = p + z2 / (2.0 * n)
    margin = z * math.sqrt((p * (1.0 - p) + z2 / (4.0 * n)) / n)
    value = (centre - margin) / (1.0 + z2 / n)
    return min(1.0, max(0.0, value))


def recency_weight(age_days: int | None) -> float:
    """Step function of age in whole days (ADR-010 §4). Unknown age gets the floor weight."""
    if age_days is None:
        return RECENCY_FLOOR
    for upper, weight in RECENCY_STEPS:
        if age_days <= upper:
            return weight
    return RECENCY_FLOOR


def rank_score(success_rate: float, similarity: float, recency: float) -> float:
    """ADR-010 §4 final rank within a tier."""
    return success_rate + W_RANK_SIMILARITY * similarity + W_RANK_RECENCY * recency


def parse_timestamp(value: str | None) -> datetime | None:
    """Parse an ISO-8601 timestamp (C# 'o' format with 7 fraction digits, or 'Z') to aware UTC. None if unusable.

    Naive timestamps are treated as UTC. Fractions are truncated to microseconds.
    """
    if not value or not isinstance(value, str):
        return None
    text = value.strip()
    if text[-1:] in ("Z", "z"):
        text = text[:-1] + "+00:00"
    text = _FRACTION_RE.sub(lambda m: "." + (m.group(1) + "000000")[:6], text, count=1)
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def format_utc(moment: datetime) -> str:
    """ISO-8601 UTC with a trailing Z and 7 fraction digits (the wire format in ADR-010 §7)."""
    moment = moment.astimezone(timezone.utc)
    return f"{moment:%Y-%m-%dT%H:%M:%S}.{moment.microsecond:06d}0Z"


def age_days(last: datetime, now: datetime) -> int:
    """Whole days (floor) between `last` and `now`. Future timestamps give negative values."""
    return math.floor((now - last).total_seconds() / 86400.0)


def recency_phrase(days: int) -> str:
    """ADR-010 §5 `{recency}` fragment. Singular for 1 week/month (grammar; the ADR table lists plurals only)."""
    if days <= 0:
        return "today"
    if days == 1:
        return "yesterday"
    if days < 7:
        return f"{days} days ago"
    if days < 56:
        weeks = days // 7
        return f"{weeks} week{'' if weeks == 1 else 's'} ago"
    months = days // 30
    return f"{months} month{'' if months == 1 else 's'} ago"


def wo_display_number(number: str | None, work_order_id: str) -> str:
    """`work_orders.number` when present; otherwise the temporary id prefix (ADR-010 §8).

    TEMPORARY: the id-prefix fallback exists only until migration 005 adds work_orders.number.
    """
    if number is not None and str(number).strip():
        return str(number).strip()
    return work_order_id.replace("-", "")[:8]


def render_evidence(
    *,
    success_count: int,
    attempt_count: int,
    model_label: str,
    average_labor_hours: float | None,
    last_done_days: int | None,
    wo_number: str,
) -> str:
    """ADR-010 §5 template. Labor and recency clauses are omitted when their source is unknown."""
    parts = [f"Fixed {success_count} of {attempt_count} on {model_label}"]
    if average_labor_hours is not None:
        parts.append(f"avg {average_labor_hours:.1f} hrs")
    if last_done_days is not None:
        parts.append(f"last done {recency_phrase(last_done_days)}")
    parts.append(f"WO #{wo_number}")
    return ", ".join(parts)
