"""Phase 4 chat tools (tools/gse_tools.py) through the registry, against the real package and a synthetic range.

No Ollama. `record_reading` must never write the database: the proposal is returned, the app persists it.
"""

from __future__ import annotations

import json
import sqlite3
from pathlib import Path

import pytest

from ai_support import offline_config
from app import units
from tools import ToolContext
from tools.catalog import default_registry
from tools.gse_tools import categories_for_topic, parse_dtc_code
from troubleshooting_support import connect_rw, insert_synthetic, load_dtc_package, load_package, new_migrated_db

PSI = 6894.75729316836
RECORD_PREFIX = "Reading proposal, NOT saved. The app must save it after the technician confirms: "


def _ctx(db):
    return ToolContext(db_path=db, config=offline_config())


def _payload(content: str, prefix: str) -> dict:
    assert content.startswith(prefix), content[:80]
    return json.loads(content[len(prefix):])


@pytest.fixture(scope="module")
def content(tmp_path_factory) -> Path:
    path = new_migrated_db(tmp_path_factory.mktemp("tools") / "content.db")
    conn = connect_rw(path)
    try:
        load_package(conn)
        load_dtc_package(conn)
    finally:
        conn.close()
    return path


@pytest.fixture
def ranged(tmp_path: Path) -> Path:
    """Synthetic 1000-1500 psi range (test values, not a manual spec)."""
    path = new_migrated_db(tmp_path / "ranged.db")
    conn = connect_rw(path)
    try:
        insert_synthetic(
            conn,
            models=[{"id": "eqm-a", "make": "Acme", "model": "A1"}],
            points=[{"id": "tp-range", "equipment_model_id": "eqm-a", "name": "range", "reading_type": "hydraulic_pressure",
                     "expected_unit": "psi", "spec_min": 1000, "spec_max": 1500, "spec_source": "TEST CITE p.9",
                     "safety_warning_ids": []}],
        )
    finally:
        conn.close()
    return path


def _count_readings(path: Path) -> int:
    conn = sqlite3.connect(str(path))
    try:
        return conn.execute("SELECT COUNT(*) FROM readings").fetchone()[0]
    finally:
        conn.close()


# ---------------------------------------------------------------- get_spec_range


def test_spec_range_returns_the_authored_and_canonical_values_with_a_citation(content):
    result = default_registry().invoke("get_spec_range", {"test_point_id": "tp-tug66028-main-relief"}, _ctx(content))
    assert result.ok is True
    payload = _payload(result.content, "Spec range: ")
    assert payload["spec_min"] == 1500 and payload["spec_max"] == 1500
    assert payload["expected_unit"] == "psi"
    assert payload["canonical_unit"] == "Pa"
    assert payload["canonical_spec_min"] == pytest.approx(1500 * PSI, rel=1e-12)
    assert [s.kind for s in result.sources] == ["manual-page"]
    assert result.sources[0].display_text == payload["spec_source"]


def test_spec_range_with_no_numeric_spec_says_todo_and_never_invents_one(content):
    result = default_registry().invoke("get_spec_range", {"test_point_id": "tp-tug66028-battery"}, _ctx(content))
    assert result.ok is True
    payload = _payload(result.content, "Spec range: ")
    assert payload["spec_source"].startswith("TODO: needs spec from manual")
    assert "spec_min" not in payload and "spec_max" not in payload
    assert result.sources == ()


def test_spec_range_for_an_unknown_test_point_is_not_found(content):
    result = default_registry().invoke("get_spec_range", {"test_point_id": "tp-nope"}, _ctx(content))
    assert result.ok is False and "No test point" in result.content


def test_spec_range_without_a_database_says_so():
    result = default_registry().invoke("get_spec_range", {"test_point_id": "x"}, _ctx(None))
    assert result.ok is False and "no database" in result.content


# ---------------------------------------------------------------- get_safety_warnings


def test_topic_keywords_map_to_safety_categories():
    assert categories_for_topic("hydraulic test port") == ("hydraulic_pressure", "lockout_tagout")
    assert categories_for_topic("battery terminals") == ("lockout_tagout", "high_voltage")  # ADR-009 severity order
    assert categories_for_topic("aircraft ramp") == ("aircraft_proximity",)
    assert categories_for_topic("xyzzy") == ("general",)


def test_hydraulic_topic_returns_hydraulic_and_lockout_warnings_with_citations(content):
    result = default_registry().invoke("get_safety_warnings", {"topic": "hydraulic test port"}, _ctx(content))
    assert result.ok is True
    assert "(hydraulic_pressure)" in result.content and "(lockout_tagout)" in result.content
    assert "(general)" not in result.content
    assert result.sources and all(s.kind == "manual-page" for s in result.sources)


def test_every_shown_warning_is_whole_never_cut_mid_text(content):
    result = default_registry().invoke("get_safety_warnings", {"topic": "general"}, _ctx(content))
    assert result.ok is True
    warning_lines = [line for line in result.content.splitlines() if line.startswith("[")]
    assert warning_lines and all("Source:" in line for line in warning_lines)
    assert len(result.content) <= 1500


def test_an_unmatched_topic_falls_back_to_general_warnings(content):
    result = default_registry().invoke("get_safety_warnings", {"topic": "zzqq unknown thing"}, _ctx(content))
    assert result.ok is True
    assert "(general)" in result.content and "(hydraulic_pressure)" not in result.content


def test_a_large_topic_says_how_many_warnings_were_not_shown(content):
    result = default_registry().invoke("get_safety_warnings", {"topic": "general"}, _ctx(content))
    assert "not shown here" in result.content  # four general warnings do not fit one tool result


# ---------------------------------------------------------------- lookup_dtc


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("SPN 98 FMI 3", ("J1939", "SPN 98 FMI 3")),
        ("spn 098 fmi 03", ("J1939", "SPN 98 FMI 3")),
        ("MID 128 PID 100 FMI 1", ("J1708", "MID 128 PID 100 FMI 1")),
        ("MID 128 SID 100 FMI 1", ("J1708", "MID 128 SID 100 FMI 1")),
        ("P0100", ("OBD2", "P0100")),
        ("p0100", ("OBD2", "P0100")),
        ("hello", None),
    ],
)
def test_dtc_codes_are_canonicalized_once(raw, expected):
    assert parse_dtc_code(raw) == expected


def test_a_known_j1939_code_returns_its_description_and_a_dtc_citation(content):
    result = default_registry().invoke("lookup_dtc", {"code": "SPN 98 FMI 3"}, _ctx(content))
    assert result.ok is True
    assert "DTC SPN 98 FMI 3 (J1939)" in result.content
    assert "Likely causes" in result.content
    assert result.sources and result.sources[0].kind == "dtc"


def test_an_obd_code_with_different_case_still_matches(content):
    result = default_registry().invoke("lookup_dtc", {"code": "p0100"}, _ctx(content))
    assert result.ok is True and "P0100" in result.content


def test_a_j1587_code_matches_under_the_stored_protocol(content):
    result = default_registry().invoke("lookup_dtc", {"code": "MID 128 PID 100 FMI 1"}, _ctx(content))
    assert result.ok is True and "(J1708)" in result.content


def test_a_code_not_in_the_library_is_an_honest_no_match(content):
    result = default_registry().invoke("lookup_dtc", {"code": "SPN 99999 FMI 1"}, _ctx(content))
    assert result.ok is True
    assert "No exact match" in result.content and "Do not guess" in result.content
    assert result.sources == ()


def test_an_unrecognized_code_format_is_an_error_not_a_guess(content):
    result = default_registry().invoke("lookup_dtc", {"code": "check engine light"}, _ctx(content))
    assert result.ok is False and "Unrecognized code format" in result.content


# ---------------------------------------------------------------- start_guided_test


def test_start_guided_test_returns_the_first_step_for_the_model_and_symptom(content):
    result = default_registry().invoke(
        "start_guided_test", {"equipment_model_id": "eqm-tug-660-28", "symptom_id": "sym-engine-no-crank"}, _ctx(content)
    )
    assert result.ok is True
    assert result.content.startswith("Guided test '")
    assert "Instruction:" in result.content and "Question:" in result.content
    assert "Safety warnings" in result.content
    assert "guided walker" in result.content  # the app creates the session; chat does not
    assert result.sources == ()  # the first step has no numeric spec, so there is nothing to cite


def test_start_guided_test_with_no_tree_says_so(content):
    result = default_registry().invoke(
        "start_guided_test", {"equipment_model_id": "eqm-nope", "symptom_id": None}, _ctx(content)
    )
    assert result.ok is True and "No troubleshooting tree" in result.content


# ---------------------------------------------------------------- record_reading (proposal only)


def test_record_reading_converts_and_returns_a_proposal_without_writing(content):
    before = _count_readings(content)
    result = default_registry().invoke(
        "record_reading", {"test_point_id": "tp-tug66028-main-relief", "value": 1600, "unit": "psi"}, _ctx(content)
    )
    assert result.ok is True
    assert "NOT saved" in result.content
    payload = _payload(result.content, RECORD_PREFIX)
    assert payload["persisted"] is False
    assert payload["canonical_unit"] == "Pa"
    assert payload["canonical_value"] == pytest.approx(1600 * PSI, rel=1e-12)
    assert payload["entered_value"] == 1600 and payload["entered_unit"] == "psi"
    assert payload["pass_fail"] == "na"  # single published setting: no tolerance, so no verdict
    assert _count_readings(content) == before


def test_record_reading_judges_a_range_in_canonical_units(ranged):
    reg = default_registry()

    def verdict(value, unit):
        result = reg.invoke("record_reading", {"test_point_id": "tp-range", "value": value, "unit": unit}, _ctx(ranged))
        assert result.ok is True
        return _payload(result.content, RECORD_PREFIX)

    assert verdict(1200, "psi")["pass_fail"] == "pass"
    assert verdict(900, "psi")["pass_fail"] == "fail"
    assert verdict(1600, "psi")["pass_fail"] == "fail"
    assert verdict(7000, "kPa")["pass_fail"] == "pass"  # about 1015 psi
    assert verdict(1200, "psi")["spec_source"] == "TEST CITE p.9"


def test_record_reading_rejects_a_unit_from_another_dimension(ranged):
    result = default_registry().invoke(
        "record_reading", {"test_point_id": "tp-range", "value": 12, "unit": "V"}, _ctx(ranged)
    )
    assert result.ok is False and "does not measure" in result.content


def test_record_reading_rejects_an_unknown_unit(ranged):
    result = default_registry().invoke(
        "record_reading", {"test_point_id": "tp-range", "value": 1, "unit": "furlong"}, _ctx(ranged)
    )
    assert result.ok is False and "Unknown unit" in result.content


def test_record_reading_rejects_an_unknown_test_point(ranged):
    result = default_registry().invoke(
        "record_reading", {"test_point_id": "tp-nope", "value": 1, "unit": "psi"}, _ctx(ranged)
    )
    assert result.ok is False and "No test point" in result.content


def test_record_reading_rejects_a_non_finite_value(ranged):
    result = default_registry().invoke(
        "record_reading", {"test_point_id": "tp-range", "value": float("nan"), "unit": "psi"}, _ctx(ranged)
    )
    assert result.ok is False


def test_record_reading_canonical_value_round_trips_through_the_units_module(ranged):
    result = default_registry().invoke(
        "record_reading", {"test_point_id": "tp-range", "value": 1234.5, "unit": "psi"}, _ctx(ranged)
    )
    payload = _payload(result.content, RECORD_PREFIX)
    back = units.from_canonical(payload["canonical_value"], "Pa", "psi")
    assert back == pytest.approx(1234.5, rel=1e-12)
