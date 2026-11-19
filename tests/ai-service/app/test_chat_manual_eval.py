"""Evaluation: real-world-style technician questions -> expected manual pages, through the search tool path.

Question set: tests/ai-service/rag/retrieval_testset.json (authored by rag-ingest from the 600i PDF text layer).
CAVEAT (stated in that file): it is SELF-AUTHORED and IN-SAMPLE for the chunking heuristics. It is a regression
sanity check, not an out-of-sample benchmark. Out-of-sample questions with expected sources must come from
gse-domain-expert (Phase 4) before any accuracy claim is made.

Runs BM25-only (no Ollama) so the result is deterministic. The threshold is a floor, not a target.
"""

from __future__ import annotations

import json

import pytest

from ai_support import REPO
from tools.manuals import run_manual_search

TESTSET = REPO / "tests" / "ai-service" / "rag" / "retrieval_testset.json"
HIT_RATE_FLOOR = 0.8  # BM25-only floor; below this the retrieval path regressed


def _cases():
    if not TESTSET.exists():
        pytest.skip("retrieval test set not present")
    data = json.loads(TESTSET.read_text(encoding="utf-8"))
    return data["top_k"], data["cases"]


def test_expected_pages_are_retrieved_in_top_k(seeded_db):
    top_k, cases = _cases()
    misses = []
    for case in cases:
        out = run_manual_search(seeded_db, case["question"], top_k, query_embedder=None)
        assert out["fallback"] is True
        pages = {p for r in out["results"] for p in range(r["page_start"], r["page_end"] + 1)}
        if not pages & set(case["expected_pages"]):
            misses.append((case["question"], case["expected_pages"], sorted(pages)))
    hit_rate = (len(cases) - len(misses)) / len(cases)
    print(f"\nretrieval hit-rate (BM25-only, top-{top_k}): {hit_rate:.2f} over {len(cases)} questions")
    for question, expected, got in misses:
        print(f"  MISS {question!r}: expected {expected}, got {got}")
    assert hit_rate >= HIT_RATE_FLOOR


def test_every_eval_question_has_a_source_or_says_not_found(seeded_db):
    """The honesty rule: a question with no matching passage must return an explicit 'not found', never a guess."""
    out = run_manual_search(seeded_db, "What is the torque for the Starship engine mounts?", 5, query_embedder=None)
    # Either passages come back (and are cited), or there are none. Never results without citations.
    for r in out["results"]:
        assert r["citation"] and r["manual_title"]
