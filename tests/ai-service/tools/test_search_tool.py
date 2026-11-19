"""The search_manuals tool against the real ingested seed manual (BM25-only, no Ollama)."""

from __future__ import annotations

from ai_support import offline_config
from tools import ToolContext
from tools.catalog import default_registry


def _ctx(db):
    return ToolContext(db_path=db, config=offline_config())


def test_search_tool_returns_passages_and_manual_page_sources(seeded_db):
    result = default_registry().invoke(
        "search_manuals", '{"query": "flexramp lubricate axle", "top_k": 3}', _ctx(seeded_db)
    )
    assert result.ok is True
    assert "Manual passages" in result.content
    assert "text-only ranking" in result.content  # fallback path is labelled for the model
    assert result.sources
    src = result.sources[0]
    assert src.kind == "manual-page"
    assert src.manual_id and src.page_start and "page" in src.display_text


def test_search_tool_with_no_match_says_not_found(seeded_db):
    result = default_registry().invoke(
        "search_manuals", '{"query": "zzqqxxyy frobnicate quux"}', _ctx(seeded_db)
    )
    assert result.ok is True
    assert "Not found in the manuals" in result.content
    assert result.sources == ()


def test_search_tool_respects_top_k_cap(seeded_db):
    result = default_registry().invoke(
        "search_manuals", '{"query": "hydraulic pressure", "top_k": 10}', _ctx(seeded_db)
    )
    assert len(result.sources) <= 10
    assert result.content.count("\n[") <= 10
