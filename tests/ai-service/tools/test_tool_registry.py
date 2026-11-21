"""Tool registry contract (tools/registry.py) and the Phase 3 tool catalog (tools/catalog.py)."""

from __future__ import annotations

import pytest
from pydantic import BaseModel

from ai_support import offline_config
from tools import ToolContext, ToolRegistry, ToolResult, ToolSpec
from tools.catalog import default_registry
from tools.manuals import SEARCH_MANUALS

EXPECTED_TOOLS = {
    "search_manuals", "get_asset", "get_repair_history", "get_part_history", "get_spec_range",
    "record_reading", "add_note", "start_guided_test", "lookup_dtc", "find_similar_repairs",
    "get_safety_warnings", "ocr_dataplate",
}


class _Args(BaseModel):
    n: int


def _ctx(db_path=None):
    return ToolContext(db_path=db_path, config=offline_config())


AVAILABLE = {
    "search_manuals", "get_spec_range", "record_reading", "start_guided_test", "lookup_dtc", "get_safety_warnings",
    # Phase 5 (repair history, read-only)
    "find_similar_repairs", "get_asset", "get_repair_history", "get_part_history",
    # Phase 7.3 (technician-confirmed data plate, read-only)
    "ocr_dataplate",
}


def test_catalog_has_every_planned_tool_and_the_implemented_ones_are_available():
    reg = default_registry()
    assert set(reg.names()) == EXPECTED_TOOLS
    available = {name for name in reg.names() if reg.get(name).available}
    assert available == AVAILABLE


def test_schemas_are_openai_style_function_tools():
    schemas = default_registry().schemas()
    assert {s["function"]["name"] for s in schemas} == EXPECTED_TOOLS
    search = next(s for s in schemas if s["function"]["name"] == "search_manuals")
    params = search["function"]["parameters"]
    assert params["type"] == "object"
    assert "query" in params["required"]
    assert params["properties"]["top_k"]["maximum"] == 10
    assert "title" not in params


@pytest.mark.parametrize("name", sorted(EXPECTED_TOOLS - AVAILABLE))
def test_stubs_say_not_available_and_never_return_data(name):
    result = default_registry().invoke(name, "{}", _ctx())
    assert result.ok is False
    assert "not available yet" in result.content
    assert "Do not guess" in result.content
    assert result.sources == ()


def test_unknown_tool_is_reported_to_the_model():
    result = default_registry().invoke("format_disk", "{}", _ctx())
    assert result.ok is False and "Unknown tool" in result.content


def test_bad_json_and_bad_types_are_reported_not_raised():
    reg = default_registry()
    assert "invalid JSON" in reg.invoke("search_manuals", "{nope", _ctx()).content
    bad = reg.invoke("search_manuals", '{"query": "x", "top_k": 99}', _ctx())
    assert bad.ok is False and "top_k" in bad.content
    assert "arguments must be a JSON object" in reg.invoke("search_manuals", "[1, 2]", _ctx()).content


def test_mapping_arguments_are_accepted():
    reg = ToolRegistry([ToolSpec("count_things", "count", _Args, lambda ctx, a: ToolResult(True, str(a.n)))])
    assert reg.invoke("count_things", {"n": 7}, _ctx()).content == "7"


def test_handler_exception_becomes_a_clean_failure():
    def boom(ctx, args):
        raise RuntimeError("database exploded with details")

    reg = ToolRegistry([ToolSpec("boom", "x", _Args, boom)])
    result = reg.invoke("boom", '{"n": 1}', _ctx())
    assert result.ok is False
    assert "database exploded" not in result.content  # internal detail never reaches the model
    assert "failed" in result.content


def test_long_tool_output_is_clipped_for_a_small_context():
    reg = ToolRegistry([ToolSpec("big", "x", _Args, lambda c, a: ToolResult(True, "x" * 5000))])
    out = reg.invoke("big", '{"n": 1}', _ctx())
    assert len(out.content) <= 1500


def test_duplicate_and_bad_names_are_rejected():
    spec = ToolSpec("same_name", "x", _Args, lambda c, a: ToolResult(True, ""))
    with pytest.raises(ValueError, match="duplicate"):
        ToolRegistry([spec, spec])
    with pytest.raises(ValueError, match="snake_case"):
        ToolRegistry([ToolSpec("bad-name!", "x", _Args, lambda c, a: ToolResult(True, ""))])


def test_search_tool_without_a_database_says_so():
    result = SEARCH_MANUALS.handler(_ctx(db_path=None), SEARCH_MANUALS.args_model(query="torque"))
    assert result.ok is False
    assert "no database" in result.content
