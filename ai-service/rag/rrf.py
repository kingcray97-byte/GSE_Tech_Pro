"""Reciprocal Rank Fusion (pure function, no I/O).

score(d) = sum over rankings r containing d of 1 / (k + rank_r(d)), rank is 1-based.
Ties keep first-seen order (stable sort), so results are deterministic.
"""

from __future__ import annotations

from typing import Hashable, Sequence

RRF_K = 60


def rrf_merge(rankings: Sequence[Sequence[Hashable]], k: int = RRF_K) -> list[tuple[Hashable, float]]:
    """Merge ranked id lists. Each list is best-first. Duplicates inside one list count once (first rank)."""
    if k <= 0:
        raise ValueError("k must be positive")
    scores: dict[Hashable, float] = {}
    for ranking in rankings:
        seen: set[Hashable] = set()
        for rank, item in enumerate(ranking, start=1):
            if item in seen:
                continue
            seen.add(item)
            scores[item] = scores.get(item, 0.0) + 1.0 / (k + rank)
    return sorted(scores.items(), key=lambda kv: kv[1], reverse=True)
