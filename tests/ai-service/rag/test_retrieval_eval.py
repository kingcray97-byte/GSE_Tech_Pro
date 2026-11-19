"""Retrieval hit-rate on the 600i maintenance manual (BM25-only, no Ollama).

IMPORTANT: the 15 queries are SELF-AUTHORED from the same 600i PDF that was used to tune the chunking and
chunk-kind heuristics. This is an in-sample regression sanity check, NOT an out-of-sample generalization
benchmark. Do not quote it as a retrieval-quality figure for other manuals.

Two metrics are reported, both at top-k:
- strict: a result's own page_start is an expected page (the citation names the right page)
- lenient: an expected page falls anywhere inside a result's page range
Assertions use the strict metric. Run `pytest -s` to see the per-question report.
"""

from __future__ import annotations

import json

import pytest

from conftest import MAINT_600I, TESTSET, require
from ingest.pipeline import ingest_files
from rag.search import hybrid_search


@pytest.fixture
def ingested(conn, offline_config):
    job = ingest_files([require(MAINT_600I)], conn=conn, config=offline_config)
    assert job.chunks_created > 0
    return conn


def test_retrieval_hit_rate_bm25(ingested, unavailable_embedder):
    spec = json.loads(TESTSET.read_text(encoding="utf-8"))
    top_k = spec["top_k"]
    strict_hits, lenient_hits = [], []
    for case in spec["cases"]:
        resp = hybrid_search(case["question"], top_k=top_k, conn=ingested, query_embedder=unavailable_embedder)
        assert resp["fallback"] is True
        expected = set(case["expected_pages"])
        strict = any(r["page_start"] in expected for r in resp["results"])
        lenient = any(set(range(r["page_start"], r["page_end"] + 1)) & expected for r in resp["results"])
        strict_hits.append(strict)
        lenient_hits.append(lenient)
        cited = [r["page_start"] for r in resp["results"]]
        print(f"{'HIT ' if strict else 'MISS'} {case['question']!r} expected={case['expected_pages']} top{top_k} cited={cited}")
    n = len(strict_hits)
    print(f"\nRETRIEVAL HIT@{top_k} strict (page_start): {sum(strict_hits)}/{n} = {sum(strict_hits) / n:.0%}")
    print(f"RETRIEVAL HIT@{top_k} lenient (page range): {sum(lenient_hits)}/{n} = {sum(lenient_hits) / n:.0%}")
    # Floor, not a target: a drop below this means a regression in tokenization, chunking, or ranking.
    assert sum(strict_hits) / n >= 0.8
