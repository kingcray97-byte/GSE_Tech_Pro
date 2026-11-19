"""Optional vector path (ADR-010 §3): fallback flag, RRF ordering, and SimilarityScore invariance.

The embedder here is a TEST DOUBLE with hand-picked cosines so the expected RRF order can be worked out on paper.
It is never used for the no-Ollama assertions: those use the real "absent" behaviour (None) instead.
"""

from __future__ import annotations

import math


from recommender import run_repair_history_search
from support import NOW, open_trigger

# Unit query vector is [1, 0], so cosine(query, v) == v[0] for the unit vectors below.
TAG_VECTORS = {
    "tag-a": [0.1, math.sqrt(1 - 0.1 * 0.1)],  # cos 0.10
    "tag-b": [0.0, 1.0],  # cos 0.00
    "tag-c": [1.0, 0.0],  # cos 1.00
}


def tagged_embedder(texts: list[str]) -> list[list[float]]:
    """TEST DOUBLE: candidate texts carry a tag; anything without a tag is the query."""
    out: list[list[float]] = []
    for text in texts:
        tag = next((t for t in TAG_VECTORS if t in text), None)
        out.append(list(TAG_VECTORS[tag]) if tag else [1.0, 0.0])
    return out


def _build_three_equal_groups(db):
    """Three repair groups in the model tier with identical outcomes, labor and recency.

    Only the symptom signal differs: A matches 9 of the 10 query symptoms, B 5, C 1. So the deterministic
    order is A > B > C. Cosines are A 0.10, B 0.00, C 1.00, so the vector order is C > A > B.
    """
    seed = db.seed
    m1 = seed.model("TUG", "MA-50", "tug")
    symptoms = [seed.symptom(f"Symptom {i}", None) for i in range(10)]
    trigger, asset = open_trigger(seed, m1, symptom_ids=tuple(symptoms))
    groups = {
        "Replace tag-a seal": symptoms[:9],
        "Replace tag-b seal": symptoms[:5],
        "Replace tag-c seal": symptoms[:1],
    }
    for description, syms in groups.items():
        for _ in range(8):
            seed.closed_case(asset, description, system=None, result="fixed", days_ago=10, labor=1.0, symptoms=tuple(syms))
    return trigger


def _order(result):
    return [c["repair_description"] for c in result["candidates"]]


def test_no_embedder_is_deterministic_and_fallback(db):
    trigger = _build_three_equal_groups(db)
    result = run_repair_history_search(db.path, trigger, query_text="pump noise", now=NOW)
    assert result["fallback"] is True
    assert _order(result) == ["Replace tag-a seal", "Replace tag-b seal", "Replace tag-c seal"]


def test_unavailable_embedder_returns_none_and_falls_back(db, unavailable_embedder):
    trigger = _build_three_equal_groups(db)
    baseline = run_repair_history_search(db.path, trigger, query_text="pump noise", now=NOW)
    result = run_repair_history_search(
        db.path, trigger, query_text="pump noise", now=NOW, query_embedder=unavailable_embedder
    )
    assert result["fallback"] is True
    assert result == baseline


def test_embedder_with_wrong_vector_count_falls_back(db):
    trigger = _build_three_equal_groups(db)

    def short(texts):
        return [[1.0, 0.0]]

    baseline = run_repair_history_search(db.path, trigger, query_text="pump noise", now=NOW)
    result = run_repair_history_search(db.path, trigger, query_text="pump noise", now=NOW, query_embedder=short)
    assert result["fallback"] is True
    assert result == baseline


def test_embedder_is_not_called_without_query_text(db):
    trigger = _build_three_equal_groups(db)
    calls: list[list[str]] = []

    def spy(texts):
        calls.append(list(texts))
        return tagged_embedder(texts)

    result = run_repair_history_search(db.path, trigger, now=NOW, query_embedder=spy)
    assert calls == []  # empty query blob: nothing to embed, so the deterministic path is used
    assert result["fallback"] is True


def test_hybrid_rrf_reorders_but_keeps_similarity_score(db):
    trigger = _build_three_equal_groups(db)
    deterministic = run_repair_history_search(db.path, trigger, query_text="pump noise", now=NOW)
    hybrid = run_repair_history_search(
        db.path, trigger, query_text="pump noise", now=NOW, query_embedder=tagged_embedder
    )
    assert hybrid["fallback"] is False
    # RRF (k=60) of [A, B, C] (deterministic) and [C, A, B] (cosine): A 1/61+1/62, C 1/63+1/61, B 1/62+1/63.
    assert _order(hybrid) == ["Replace tag-a seal", "Replace tag-c seal", "Replace tag-b seal"]
    assert _order(deterministic) == ["Replace tag-a seal", "Replace tag-b seal", "Replace tag-c seal"]
    # The vector path changes only ORDER. Every per-candidate value, including SimilarityScore, is identical.
    by_desc_det = {c["repair_description"]: c for c in deterministic["candidates"]}
    for c in hybrid["candidates"]:
        assert c == by_desc_det[c["repair_description"]]


def test_hybrid_respects_top_k_and_tier_precedence(db):
    trigger = _build_three_equal_groups(db)
    hybrid = run_repair_history_search(
        db.path, trigger, query_text="pump noise", top_k=2, now=NOW, query_embedder=tagged_embedder
    )
    assert _order(hybrid) == ["Replace tag-a seal", "Replace tag-c seal"]
    assert {c["match_tier"] for c in hybrid["candidates"]} == {"model"}
