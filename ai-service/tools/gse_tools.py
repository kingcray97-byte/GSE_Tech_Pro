"""Phase 4 chat tools for GSE troubleshooting: spec ranges, safety warnings, DTC lookup, guided start, reading proposal.

Every handler reads the shared SQLite content and returns short plain text for the model (ADR-008 small-context
rule). Facts come from tools, never from model memory. Specs cite their source (`spec_source`), and a missing spec
says so: `TODO: needs spec from manual`. `record_reading` is proposal-only: it never writes the database (ADR-009 §3,
the app is the single writer of `readings`).
"""

from __future__ import annotations

import json
import math
import re
import sqlite3
from typing import Any, Optional

from pydantic import BaseModel, Field

from app import troubleshooting as tb
from app import units
from rag.db import connect
from tools.registry import MAX_TOOL_RESULT_CHARS, ToolContext, ToolResult, ToolSource, ToolSpec

_TODO_PREFIX = "TODO"
_HEADER_BUDGET = 260  # room for the header and the 'not shown' note, so the registry clip never cuts a warning


# ---------------------------------------------------------------- arguments


class GetSpecRangeArgs(BaseModel):
    test_point_id: str = Field(min_length=1, max_length=64, description="Test point id, for example tp-tug66028-battery.")


class RecordReadingArgs(BaseModel):
    test_point_id: str = Field(min_length=1, max_length=64)
    value: float
    unit: str = Field(min_length=1, max_length=16)


class StartGuidedTestArgs(BaseModel):
    equipment_model_id: str = Field(min_length=1, max_length=64)
    symptom_id: Optional[str] = Field(default=None, max_length=64)


class LookupDtcArgs(BaseModel):
    code: str = Field(min_length=1, max_length=32, description="For example SPN 94 FMI 3 or P0230.")


class GetSafetyWarningsArgs(BaseModel):
    topic: str = Field(min_length=1, max_length=120, description="The step or test point, for example 'hydraulic test port'.")


# ---------------------------------------------------------------- shared helpers


def _open(ctx: ToolContext) -> Optional[sqlite3.Connection]:
    if ctx.db_path is None:
        return None
    return connect(ctx.db_path, readonly=True, load_vec=False)


def _no_database() -> ToolResult:
    return ToolResult(False, "Troubleshooting data is unavailable: no database is configured.")


def _is_todo(citation: Optional[str]) -> bool:
    return not citation or citation.strip().upper().startswith(_TODO_PREFIX)


def _fit_lines(lines: list[str], budget: int) -> tuple[list[str], int]:
    """Keep whole lines while they fit in `budget` characters. Returns (kept, dropped_count).

    Never cut a safety warning in half: a partial warning is worse than a visible 'not shown' note.
    """
    kept: list[str] = []
    used = 0
    for line in lines:
        cost = len(line) + 1
        if used + cost > budget:
            break
        kept.append(line)
        used += cost
    return kept, len(lines) - len(kept)


# ---------------------------------------------------------------- get_spec_range


def _get_spec_range(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, GetSpecRangeArgs)
    conn = _open(ctx)
    if conn is None:
        return _no_database()
    try:
        spec = tb.spec_for_test_point(conn, args.test_point_id)
    finally:
        conn.close()
    if spec is None:
        return ToolResult(False, f"No test point '{args.test_point_id}' in the database. Do not guess a spec.")
    sources: tuple[ToolSource, ...] = ()
    if spec["canonical_spec_min"] is None and spec["canonical_spec_max"] is None:
        payload: dict[str, Any] = {
            "test_point_id": spec["test_point_id"],
            "name": spec["name"],
            "spec_source": spec["spec_source"],
            "note": "No numeric spec is in the database. Do not estimate one; say the spec is not available.",
        }
    else:
        payload = {
            "test_point_id": spec["test_point_id"],
            "name": spec["name"],
            "spec_min": spec["spec_min"],
            "spec_max": spec["spec_max"],
            "expected_unit": spec["expected_unit"],
            "spec_source": spec["spec_source"],
            "canonical_unit": spec["canonical_unit"],
            "canonical_spec_min": spec["canonical_spec_min"],
            "canonical_spec_max": spec["canonical_spec_max"],
            "note": "Present the spec in expected_unit and cite spec_source.",
        }
        if not _is_todo(spec["spec_source"]):
            sources = (ToolSource(kind="manual-page", display_text=spec["spec_source"]),)
    return ToolResult(True, "Spec range: " + json.dumps(payload, ensure_ascii=False), sources)


GET_SPEC_RANGE = ToolSpec(
    name="get_spec_range",
    description=(
        "Expected range for a test point, with its citation or 'TODO: needs spec from manual'. "
        "Present the spec in the unit the manual uses and cite spec_source."
    ),
    args_model=GetSpecRangeArgs,
    handler=_get_spec_range,
)


# ---------------------------------------------------------------- get_safety_warnings

# Topic keyword -> safety categories. Electrical and hydraulic topics also pull in lockout/tagout: stored energy and
# isolation apply to both. Matching is case-insensitive substring; an unmatched topic falls back to `general`.
_TOPIC_RULES: tuple[tuple[tuple[str, ...], tuple[str, ...]], ...] = (
    (("hydraulic", "pressure", "relief", "cylinder", "hose", "fitting", "pump", "accumulator"),
     ("hydraulic_pressure", "lockout_tagout")),
    (("electric", "voltage", "volt", "battery", "batteries", "starter", "solenoid", "wiring", "harness", "charg", "alternator"),
     ("high_voltage", "lockout_tagout")),
    (("lockout", "tagout", "isolat", "energy", "loto"), ("lockout_tagout",)),
    (("aircraft", "ramp", "inlet", "propeller", "airport", "airline"), ("aircraft_proximity",)),
    (("hot", "burn", "coolant", "exhaust", "hot surface"), ("general",)),
    (("fuel", "lpg", "gasoline", "gas ", "propane"), ("general",)),
    (("moving", "conveyor", "pinch", "crush", "lift arm", "loading head", "rollertrack"), ("general",)),
    (("chock", "brake", "park", "level ground", "tire", "steer"), ("general",)),
)
_SEVERITY = {code: i for i, code in enumerate(tb.SEVERITY_ORDER)}


def categories_for_topic(topic: str) -> tuple[str, ...]:
    text = f" {topic.lower()} "
    found: list[str] = []
    for keywords, categories in _TOPIC_RULES:
        if any(k in text for k in keywords):
            found.extend(c for c in categories if c not in found)
    if not found:
        return ("general",)
    return tuple(sorted(found, key=lambda c: _SEVERITY.get(c, len(_SEVERITY))))


def _get_safety_warnings(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, GetSafetyWarningsArgs)
    conn = _open(ctx)
    if conn is None:
        return _no_database()
    categories = categories_for_topic(args.topic)
    try:
        marks = ", ".join("?" for _ in categories)
        rows = conn.execute(
            "SELECT id, category, text, source_citation FROM safety_warnings "
            f"WHERE deleted_at IS NULL AND category IN ({marks}) ORDER BY id",
            list(categories),
        ).fetchall()
    finally:
        conn.close()
    if not rows:
        return ToolResult(True, f"No safety warnings are stored for '{args.topic}'. Advise caution and the manual.")
    rows = sorted(rows, key=lambda r: (_SEVERITY.get(r["category"], len(_SEVERITY)), r["id"]))
    lines = [f"[{i}] ({r['category']}) {r['text']} Source: {r['source_citation'] or 'TODO: needs spec from manual'}"
             for i, r in enumerate(rows, start=1)]
    budget = MAX_TOOL_RESULT_CHARS - _HEADER_BUDGET
    kept, dropped = _fit_lines(lines, budget)
    header = f"Safety warnings for '{args.topic}' (show before the step):"
    body = [header, *kept]
    if dropped:
        body.append(f"{dropped} more warning(s) are not shown here. Show the full list on the guided screen before this step.")
    sources: list[ToolSource] = []
    for r in rows[: len(kept)]:
        citation = r["source_citation"]
        if citation and not _is_todo(citation):
            sources.append(ToolSource(kind="manual-page", display_text=citation))
    unique: dict[str, ToolSource] = {}
    for src in sources:
        unique.setdefault(src.display_text, src)
    return ToolResult(True, "\n".join(body), tuple(unique.values()))


GET_SAFETY_WARNINGS = ToolSpec(
    name="get_safety_warnings",
    description="Safety warnings for a step or test point (stored pressure, lockout, high voltage).",
    args_model=GetSafetyWarningsArgs,
    handler=_get_safety_warnings,
)


# ---------------------------------------------------------------- lookup_dtc

_J1939 = re.compile(r"^\s*SPN\s*(\d{1,7})\s*FMI\s*(\d{1,3})\s*$", re.IGNORECASE)
_J1587 = re.compile(r"^\s*MID\s*(\d{1,3})\s*(PID|SID)\s*(\d{1,3})\s*FMI\s*(\d{1,3})\s*$", re.IGNORECASE)
_OBD2 = re.compile(r"^\s*(P\d{4})\s*$", re.IGNORECASE)


def parse_dtc_code(raw: str) -> Optional[tuple[str, str]]:
    """(protocol, canonical code) as stored in dtc_library, or None when the format is not recognized.

    Canonical forms match docs/data/dtc/README.md: 'SPN n FMI m' (J1939), 'MID n PID|SID n FMI m' (J1587, stored
    under protocol J1708), and the uppercase 'Pnnnn' (OBD II, stored as OBD2). Leading zeros are dropped once here.
    """
    m = _J1939.match(raw)
    if m:
        return "J1939", f"SPN {int(m.group(1))} FMI {int(m.group(2))}"
    m = _J1587.match(raw)
    if m:
        return "J1708", f"MID {int(m.group(1))} {m.group(2).upper()} {int(m.group(3))} FMI {int(m.group(4))}"
    m = _OBD2.match(raw)
    if m:
        return "OBD2", m.group(1).upper()
    return None


def _lookup_dtc(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, LookupDtcArgs)
    parsed = parse_dtc_code(args.code)
    if parsed is None:
        return ToolResult(
            False,
            "Unrecognized code format. Use 'SPN n FMI m' (J1939), 'MID n PID n FMI m' (J1587), or 'Pnnnn' (OBD II).",
        )
    protocol, code = parsed
    conn = _open(ctx)
    if conn is None:
        return _no_database()
    try:
        row = conn.execute(
            "SELECT protocol, code, description, system, severity, likely_causes, source_citation "
            "FROM dtc_library WHERE protocol = ? AND code = ?",
            (protocol, code),
        ).fetchone()
    finally:
        conn.close()
    if row is None:
        return ToolResult(True, f"No exact match for '{code}' in the DTC library. Do not guess a meaning; check the OEM code list.")
    citation = row["source_citation"] or f"DTC library {row['protocol']} {row['code']}"
    text = (
        f"DTC {row['code']} ({row['protocol']}): {row['description'] or 'no description stored'}. "
        f"System: {row['system'] or 'not set'}. Severity: {row['severity'] or 'not set in the library'}. "
        f"Likely causes (inspection guidance, not a diagnosis): {row['likely_causes'] or 'not stored'}. "
        f"Source: {citation}."
    )
    return ToolResult(True, text, (ToolSource(kind="dtc", display_text=citation),))


LOOKUP_DTC = ToolSpec(
    name="lookup_dtc",
    description="Look up an SPN/FMI, OBD, or manufacturer code: description, severity, likely causes.",
    args_model=LookupDtcArgs,
    handler=_lookup_dtc,
)


# ---------------------------------------------------------------- start_guided_test


def _start_guided_test(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, StartGuidedTestArgs)
    conn = _open(ctx)
    if conn is None:
        return _no_database()
    try:
        found = tb.start_tree(conn, args.equipment_model_id, args.symptom_id)
        if found is None:
            return ToolResult(True, "No troubleshooting tree is available for this model and symptom yet. Do not guess a procedure.")
        tree, step = found
        resolved = tb.resolve_step(conn, "", step)
        title = tree["title"]
    finally:
        conn.close()
    reading_unit = units.canonical_unit_for_reading_type(resolved.reading_type)
    base = [f"Guided test '{title}' (tree {tree['id']}), step {resolved.step_order}.", f"Instruction: {resolved.instruction}"]
    if resolved.question:
        base.append(f"Question: {resolved.question}")
    if resolved.spec_min is not None or resolved.spec_max is not None:
        base.append(
            f"Spec ({resolved.reading_type}, canonical {reading_unit}): min {resolved.spec_min}, max {resolved.spec_max}. "
            f"Source: {resolved.spec_source}."
        )
    base.append("The technician runs this test in the guided walker, which starts the session and records readings.")
    budget = MAX_TOOL_RESULT_CHARS - sum(len(x) + 1 for x in base) - _HEADER_BUDGET
    kept, dropped = _fit_lines([f"- {w}" for w in resolved.safety_warnings], max(budget, 0))
    lines = [*base]
    if kept:
        lines.append("Safety warnings (show before the step):")
        lines.extend(kept)
    if dropped:
        lines.append(f"{dropped} more safety warning(s) not shown here. Show the full list in the guided walker.")
    sources: tuple[ToolSource, ...] = ()
    if resolved.spec_source and not _is_todo(resolved.spec_source):
        sources = (ToolSource(kind="manual-page", display_text=resolved.spec_source),)
    return ToolResult(True, "\n".join(lines), sources)


START_GUIDED_TEST = ToolSpec(
    name="start_guided_test",
    description="Start a guided troubleshooting tree for a model and symptom.",
    args_model=StartGuidedTestArgs,
    handler=_start_guided_test,
)


# ---------------------------------------------------------------- record_reading


def _record_reading(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, RecordReadingArgs)
    if not math.isfinite(args.value):
        return ToolResult(False, "The reading must be a finite number.")
    if not units.is_known_unit(args.unit):
        return ToolResult(False, f"Unknown unit '{args.unit}'. Use a unit such as psi, bar, kPa, V, A, °C, °F, or L/min.")
    conn = _open(ctx)
    if conn is None:
        return _no_database()
    try:
        tp = tb.get_test_point(conn, args.test_point_id)
    finally:
        conn.close()
    if tp is None:
        return ToolResult(False, f"No test point '{args.test_point_id}' in the database. The reading was not recorded.")
    canon_value = units.to_canonical(args.value, args.unit)
    canon_unit = units.canonical_unit(args.unit)
    expected_canon = units.canonical_unit_for_reading_type(tp["reading_type"])
    if expected_canon is not None and canon_unit != expected_canon:
        return ToolResult(
            False,
            f"Unit '{args.unit}' does not measure this test point ({tp['reading_type']}). Ask for the reading in the right unit.",
        )
    lo, hi = tb.canonical_spec(tp["expected_unit"], tp["reading_type"], tp["spec_min"], tp["spec_max"])
    pass_fail = tb.judge(canon_value, lo, hi) if expected_canon is not None else "na"
    payload = {
        "canonical_value": canon_value,
        "canonical_unit": canon_unit,
        "entered_value": args.value,
        "entered_unit": args.unit,
        "pass_fail": pass_fail,
        "spec_source": tp["spec_source"] or tb.SPEC_TODO,
        "persisted": False,
    }
    text = (
        "Reading proposal, NOT saved. The app must save it after the technician confirms: "
        + json.dumps(payload, ensure_ascii=False)
    )
    return ToolResult(True, text)


RECORD_READING = ToolSpec(
    name="record_reading",
    description="Save a measured reading for a test point (converted to canonical units).",
    args_model=RecordReadingArgs,
    handler=_record_reading,
)
