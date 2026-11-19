"""Phase 5 chat tools (tools/repair_history.py) through the registry, against a synthetic migrated database.

No Ollama and no embedder. Every tool is read-only: the counts of the shared tables never change.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from ai_support import offline_config
from repair_history_support import build_seed, counts
from tools import ToolContext
from tools.catalog import default_registry
from tools.registry import MAX_TOOL_RESULT_CHARS


@pytest.fixture(scope="module")
def rh(tmp_path_factory) -> tuple[Path, dict[str, str]]:
    path = tmp_path_factory.mktemp("rh_tools") / "gse-tech-pro.db"
    return path, build_seed(path)


def _ctx(db):
    return ToolContext(db_path=db, config=offline_config())


def _invoke(name, args, db):
    return default_registry().invoke(name, args, _ctx(db))


# ---------------------------------------------------------------- registry


def test_the_four_repair_history_tools_are_real_and_add_note_is_still_a_stub():
    registry = default_registry()
    for name in ("find_similar_repairs", "get_asset", "get_repair_history", "get_part_history"):
        assert registry.get(name).available is True, name
    assert registry.get("add_note").available is False


# ---------------------------------------------------------------- find_similar_repairs


def test_find_similar_repairs_lists_ranked_candidates_with_work_order_sources(rh):
    path, ids = rh
    before = counts(path)
    result = _invoke("find_similar_repairs", {"work_order_id": ids["trigger"]}, path)
    assert result.ok is True
    lines = [line for line in result.content.splitlines() if line.startswith("[")]
    assert len(lines) == 2
    assert "Replace main hydraulic pump (hydraulic): Fixed 1 of 3" in lines[0]  # what the repair was, and its system
    assert "model match" in lines[0] and "success rate" in lines[0]
    assert len(result.content) <= MAX_TOOL_RESULT_CHARS
    assert "not shown here" not in result.content  # both candidates fit whole
    assert "WO #4471" in lines[0]
    assert len(result.sources) == 2
    assert all(s.kind == "work-order" for s in result.sources)
    evidence = result.sources[0].display_text
    assert evidence.startswith("Fixed 1 of 3 on TUG MA-60, avg 2.0 hrs, last done ")
    assert evidence.endswith(", WO #4471")
    assert result.sources[0].work_order_number == "4471"
    assert counts(path) == before


def test_find_similar_repairs_with_no_history_says_so_without_guessing(tmp_path):
    from repair_history_support import build_empty

    path = tmp_path / "no-history.db"
    open_wo = build_empty(path)
    result = _invoke("find_similar_repairs", {"work_order_id": open_wo}, path)
    assert result.ok is True
    assert "No similar past repairs" in result.content and "Do not guess" in result.content
    assert result.sources == ()


def test_find_similar_repairs_unknown_work_order_is_a_failed_result(rh):
    path, _ = rh
    result = _invoke("find_similar_repairs", {"work_order_id": "00000000-0000-0000-0000-000000000000"}, path)
    assert result.ok is False and "No work order matches" in result.content


def test_find_similar_repairs_blank_work_order_id_is_rejected_cleanly(rh):
    path, _ = rh
    result = _invoke("find_similar_repairs", {"work_order_id": "   "}, path)
    assert result.ok is False and "Invalid work order id" in result.content


def test_find_similar_repairs_without_a_database_says_so():
    result = _invoke("find_similar_repairs", {"work_order_id": "wo-1"}, None)
    assert result.ok is False and "no database" in result.content


# ---------------------------------------------------------------- get_asset


@pytest.mark.parametrize("identifier", ["TUG-01", "tug-01", "SN-1001", "sn-1001"])
def test_get_asset_finds_by_asset_number_or_serial_case_insensitively(rh, identifier):
    path, ids = rh
    result = _invoke("get_asset", {"identifier": identifier}, path)
    assert result.ok is True
    assert "TUG-01" in result.content and "TUG MA-50" in result.content
    assert f"(id {ids['trig_asset']})" in result.content  # the id get_repair_history needs
    assert "Hour meter: 1234.5" in result.content and "Location: Bay 3" in result.content
    assert result.sources == ()


def test_get_asset_finds_by_vin(rh):
    path, ids = rh
    result = _invoke("get_asset", {"identifier": "acme1234567890xyz"}, path)
    assert result.ok is True and "FL-07" in result.content
    assert "VIN: ACME1234567890XYZ" in result.content
    assert "Hour meter: not recorded" in result.content  # honest: never invented


def test_get_asset_not_found_is_honest(rh):
    path, _ = rh
    result = _invoke("get_asset", {"identifier": "NOPE-999"}, path)
    assert result.ok is True and result.content == "No asset matches 'NOPE-999'. Do not guess an asset."


# ---------------------------------------------------------------- get_repair_history


def test_get_repair_history_lists_work_orders_outcomes_and_comebacks(rh):
    path, ids = rh
    result = _invoke("get_repair_history", {"asset_id": ids["hist_asset"]}, path)
    assert result.ok is True
    assert "WO #4471" in result.content and "WO #4480" in result.content
    assert "Outcome: fixed" in result.content
    assert "confirmed comeback" in result.content  # WO 4471 was confirmed to come back
    assert {s.display_text for s in result.sources} >= {"WO #4471", "WO #4480"}
    assert all(s.kind == "work-order" for s in result.sources)


def test_get_repair_history_shows_a_not_fixed_outcome(rh):
    path, ids = rh
    result = _invoke("get_repair_history", {"asset_id": ids["trig_asset"]}, path)
    assert result.ok is True and "WO #4300" in result.content and "Outcome: not_fixed" in result.content


def test_get_repair_history_for_an_asset_without_work_orders_says_so(rh):
    path, ids = rh
    result = _invoke("get_repair_history", {"asset_id": ids["empty_asset"]}, path)
    assert result.ok is True and result.content == "No repair history for this asset."
    assert result.sources == ()


def test_get_repair_history_is_capped_and_says_older_rows_exist(rh):
    path, ids = rh
    result = _invoke("get_repair_history", {"asset_id": ids["big_asset"]}, path)
    assert result.ok is True
    assert len(result.content) <= MAX_TOOL_RESULT_CHARS
    assert "Older work orders are not shown here." in result.content
    assert len([s for s in result.sources]) <= 6


# ---------------------------------------------------------------- get_part_history


def test_get_part_history_by_part_number_ignores_case_and_cites_the_work_order(rh):
    path, _ = rh
    result = _invoke("get_part_history", {"part_number": "hp-pump-100"}, path)
    assert result.ok is True
    assert "HP-PUMP-100" in result.content and "WO #4471" in result.content
    assert "at 1200 hrs" in result.content
    assert [s.display_text for s in result.sources] == ["WO #4471"]


def test_get_part_history_by_asset_id(rh):
    path, ids = rh
    result = _invoke("get_part_history", {"asset_id": ids["hist_asset"]}, path)
    assert result.ok is True and "HP-PUMP-100" in result.content


def test_get_part_history_for_an_asset_with_no_parts_is_honest(rh):
    path, ids = rh
    result = _invoke("get_part_history", {"asset_id": ids["trig_asset"]}, path)
    assert result.ok is True and result.content == "No part replacement history."


def test_get_part_history_needs_a_part_number_or_an_asset(rh):
    path, _ = rh
    result = _invoke("get_part_history", {}, path)
    assert result.ok is False and "Give a part number or an asset id" in result.content


def test_get_part_history_without_a_database_says_so():
    result = _invoke("get_part_history", {"part_number": "X"}, None)
    assert result.ok is False and "no database" in result.content


def test_repair_history_tools_without_a_database_say_so():
    for name, args in (
        ("get_asset", {"identifier": "TUG-01"}),
        ("get_repair_history", {"asset_id": "a"}),
    ):
        result = _invoke(name, args, None)
        assert result.ok is False and "no database" in result.content, name
