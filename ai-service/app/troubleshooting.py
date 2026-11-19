"""Troubleshooting-tree resolution shared by the /guided/* routes and the chat tools (ADR-009 §2-§3).

Pure over a SQLite connection: no writes, every query parameterized. The shared database holds the content
(equipment_models, symptoms, safety_warnings, test_points, troubleshooting_trees, troubleshooting_steps). The
service turns rows into `GuidedStep` wire objects and walks the tree. It never writes `readings` (the app records
readings, ADR-009 §3 "Who records the reading").

Spec units: test point specs are authored in `expected_unit`. They are converted to the canonical SI unit for the
reading type (app/units.py) before they leave this module. A step inherits the unit of its test point.
"""

from __future__ import annotations

import json
import logging
import re
import sqlite3
from typing import Any, Optional, Sequence

from app import units
from app.models import GuidedStep

log = logging.getLogger("gse.ai.troubleshooting")

SPEC_TODO = "TODO: needs spec from manual"
# ADR-009 §3 safety order: hydraulic, lockout, high voltage, aircraft proximity, then general.
SEVERITY_ORDER = ("hydraulic_pressure", "lockout_tagout", "high_voltage", "aircraft_proximity", "general")
_UNREADABLE_WARNINGS = "Safety warnings for this step could not be read. Stop and check the manual before continuing."
_MISSING_WARNING = "A safety warning referenced by this step is missing from the database. Check the manual before continuing."
_TERMINAL_STATUS_EXCLUDED = "retired"


class TreeIntegrityError(LookupError):
    """The content is internally inconsistent (for example a next-step id that does not exist in the tree)."""


# ---------------------------------------------------------------- lookups


_COMPLAINT_STOPWORDS = frozenset({"the", "and", "for", "with", "not", "does", "doesnt", "won", "when", "while", "its", "this"})


def _tokens(text: Optional[str]) -> set[str]:
    """Lowercase alphanumeric tokens of 3+ characters, without common filler words."""
    if not text:
        return set()
    return {tok for tok in re.findall(r"[a-z0-9]+", text.lower()) if len(tok) >= 3 and tok not in _COMPLAINT_STOPWORDS}


def find_tree(
    conn: sqlite3.Connection,
    equipment_model_id: str,
    symptom_id: Optional[str] = None,
    complaint_text: Optional[str] = None,
) -> Optional[sqlite3.Row]:
    """Best-matching tree for a model, or None when the choice would be a guess (ADR-009 §3).

    With a symptom: only trees for that symptom (no silent fallback to another complaint's tree, which would start
    the wrong diagnosis). Approved trees sort before drafts.

    Without a symptom (complaint-only start), the choice must be unambiguous:
    - no complaint text and exactly one non-retired tree for the model: that tree;
    - complaint text: candidates whose title, system, or linked symptom name/description share a token with the
      complaint. Exactly one candidate matching: that tree. Zero or several: None;
    - otherwise (several trees, no complaint): None, so the caller asks the tech for a symptom.

    `retired` trees are never returned.
    """
    if symptom_id is None:
        candidates = conn.execute(
            "SELECT t.*, s.name AS symptom_name, s.description AS symptom_description "
            "FROM troubleshooting_trees t LEFT JOIN symptoms s ON s.id = t.symptom_id "
            "WHERE t.equipment_model_id = ? AND t.status <> ? "
            "ORDER BY CASE t.status WHEN 'approved' THEN 0 ELSE 1 END, t.id",
            (equipment_model_id, _TERMINAL_STATUS_EXCLUDED),
        ).fetchall()
        if complaint_text is None or not complaint_text.strip():
            return candidates[0] if len(candidates) == 1 else None
        wanted = _tokens(complaint_text)
        if not wanted:
            return None
        matches = [
            row
            for row in candidates
            if wanted & _tokens(" ".join(str(row[k] or "") for k in ("title", "system", "symptom_name", "symptom_description")))
        ]
        return matches[0] if len(matches) == 1 else None
    return conn.execute(
        "SELECT * FROM troubleshooting_trees "
        "WHERE equipment_model_id = ? AND symptom_id = ? AND status <> ? "
        "ORDER BY CASE status WHEN 'approved' THEN 0 ELSE 1 END, id LIMIT 1",
        (equipment_model_id, symptom_id, _TERMINAL_STATUS_EXCLUDED),
    ).fetchone()


def first_step(conn: sqlite3.Connection, tree_id: str) -> Optional[sqlite3.Row]:
    """The step with the lowest step_order (the entry point of a tree)."""
    return conn.execute(
        "SELECT * FROM troubleshooting_steps WHERE tree_id = ? ORDER BY step_order, id LIMIT 1",
        (tree_id,),
    ).fetchone()


def load_step(conn: sqlite3.Connection, tree_id: str, step_id: str) -> Optional[sqlite3.Row]:
    return conn.execute(
        "SELECT * FROM troubleshooting_steps WHERE tree_id = ? AND id = ?",
        (tree_id, step_id),
    ).fetchone()


def start_tree(
    conn: sqlite3.Connection,
    equipment_model_id: str,
    symptom_id: Optional[str] = None,
    complaint_text: Optional[str] = None,
) -> Optional[tuple[sqlite3.Row, sqlite3.Row]]:
    """(tree_row, first_step_row) for a model/symptom, or None when no usable tree exists."""
    tree = find_tree(conn, equipment_model_id, symptom_id, complaint_text)
    if tree is None:
        return None
    step = first_step(conn, tree["id"])
    if step is None:
        return None
    return tree, step


def get_test_point(conn: sqlite3.Connection, test_point_id: str) -> Optional[sqlite3.Row]:
    return conn.execute(
        "SELECT * FROM test_points WHERE id = ? AND deleted_at IS NULL",
        (test_point_id,),
    ).fetchone()


# ---------------------------------------------------------------- spec and judgement


def canonical_spec(
    expected_unit: Optional[str], reading_type: Optional[str], spec_min: Any, spec_max: Any
) -> tuple[Optional[float], Optional[float]]:
    """Authored spec (in expected_unit) -> canonical SI bounds for the reading type.

    Returns (None, None) when there is no numeric spec, the unit is unknown, or the unit's dimension does not match
    the reading type (for example a voltage test point authored in psi). A mismatch is an authoring error: refusing
    to convert is safer than comparing a pressure against a voltage.
    """
    if spec_min is None and spec_max is None:
        return None, None
    if not units.is_known_unit(expected_unit):
        log.warning("test point spec has no known unit; spec withheld")
        return None, None
    if units.canonical_unit(expected_unit) != units.canonical_unit_for_reading_type(reading_type):
        log.warning("test point unit does not match its reading type; spec withheld")
        return None, None

    def _conv(value: Any) -> Optional[float]:
        return None if value is None else units.to_canonical(float(value), expected_unit)

    return _conv(spec_min), _conv(spec_max)


def judge(value: float, spec_min: Optional[float], spec_max: Optional[float]) -> str:
    """Pass/fail of a canonical value against canonical bounds: 'pass' | 'fail' | 'na'.

    'na' when there are no bounds, and when min == max (a single published setting). That gives no tolerance, so
    comparing for equality would call almost every real gauge reading a fail. The manual's tolerance is TODO.
    """
    if spec_min is None and spec_max is None:
        return "na"
    if spec_min is not None and spec_max is not None and spec_min == spec_max:
        return "na"
    if spec_min is not None and value < spec_min:
        return "fail"
    if spec_max is not None and value > spec_max:
        return "fail"
    return "pass"


def spec_for_test_point(conn: sqlite3.Connection, test_point_id: str) -> Optional[dict[str, Any]]:
    """Authored and canonical spec for one test point, or None when the id is unknown."""
    tp = get_test_point(conn, test_point_id)
    if tp is None:
        return None
    lo, hi = canonical_spec(tp["expected_unit"], tp["reading_type"], tp["spec_min"], tp["spec_max"])
    canon_unit = units.canonical_unit_for_reading_type(tp["reading_type"])
    return {
        "test_point_id": tp["id"],
        "name": tp["name"],
        "reading_type": tp["reading_type"],
        "expected_unit": tp["expected_unit"],
        "spec_min": tp["spec_min"],
        "spec_max": tp["spec_max"],
        "spec_source": tp["spec_source"] or SPEC_TODO,
        "canonical_unit": canon_unit if (lo is not None or hi is not None) else None,
        "canonical_spec_min": lo,
        "canonical_spec_max": hi,
    }


# ---------------------------------------------------------------- safety warnings


def _parse_ids(raw: Optional[str]) -> Optional[list[str]]:
    """A JSON array of slugs. None means the column is unreadable (the caller shows a safe placeholder)."""
    if raw is None or raw == "":
        return []
    try:
        data = json.loads(raw)
    except (TypeError, ValueError):
        return None
    if not isinstance(data, list) or not all(isinstance(item, str) for item in data):
        return None
    return data


def warning_texts(conn: sqlite3.Connection, ids: Sequence[str]) -> list[str]:
    """Display strings for safety-warning slugs, ordered by ADR-009 severity. Missing slugs get a visible placeholder."""
    unique = list(dict.fromkeys(ids))
    if not unique:
        return []
    marks = ", ".join("?" for _ in unique)
    rows = conn.execute(
        f"SELECT id, category, text FROM safety_warnings WHERE deleted_at IS NULL AND id IN ({marks})",
        unique,
    ).fetchall()
    found = {row["id"]: row for row in rows}

    def _rank(category: str) -> int:
        return SEVERITY_ORDER.index(category) if category in SEVERITY_ORDER else len(SEVERITY_ORDER)

    ordered = sorted(found.values(), key=lambda r: (_rank(r["category"]), r["id"]))
    out = [row["text"] for row in ordered]
    missing = [wid for wid in unique if wid not in found]
    out.extend(_MISSING_WARNING for _ in missing)
    if missing:
        log.warning("%d safety warning id(s) missing from the database", len(missing))
    return out


def _step_warning_texts(conn: sqlite3.Connection, step: sqlite3.Row, tp: Optional[sqlite3.Row]) -> list[str]:
    """Union of the step's and its test point's warnings. Safety-favouring: a test point's hazards always show."""
    step_ids = _parse_ids(step["safety_warning_ids"])
    tp_ids = _parse_ids(tp["safety_warning_ids"]) if tp is not None else []
    if step_ids is None or tp_ids is None:
        return [_UNREADABLE_WARNINGS]
    return warning_texts(conn, [*step_ids, *tp_ids])


# ---------------------------------------------------------------- step resolution


def step_spec(conn: sqlite3.Connection, step: sqlite3.Row) -> tuple[Optional[str], Optional[float], Optional[float], Optional[str], Optional[sqlite3.Row]]:
    """(reading_type, canonical_min, canonical_max, spec_source, test_point_row) for one step.

    With a test point, the test point governs the spec and unit (the step's own spec is in the test point's unit and
    is ignored). Without one, the step's spec is already canonical (ADR-009 §2).
    """
    tp = get_test_point(conn, step["test_point_id"]) if step["test_point_id"] else None
    if step["test_point_id"] and tp is None:
        log.warning("step references a missing or deleted test point; spec withheld")
    if tp is not None:
        reading_type = tp["reading_type"]
        if step["reading_type"] and step["reading_type"] != reading_type:
            log.warning("step reading_type disagrees with its test point; spec withheld")
            return reading_type, None, None, SPEC_TODO, tp
        lo, hi = canonical_spec(tp["expected_unit"], reading_type, tp["spec_min"], tp["spec_max"])
        source = tp["spec_source"] or step["spec_source"] or SPEC_TODO
        return reading_type, lo, hi, source, tp
    lo = step["spec_min"]
    hi = step["spec_max"]
    source = step["spec_source"]
    if (lo is not None or hi is not None) and not source:
        source = SPEC_TODO
    return step["reading_type"], lo, hi, source, None


def resolve_step(conn: sqlite3.Connection, session_id: str, step: sqlite3.Row, *, note: Optional[str] = None) -> GuidedStep:
    """Build the wire GuidedStep for one tree step (ADR-009 §3, step 4).

    `note` (a service annotation such as a canonicalized reading echo) is appended to the leaf explanation only: it
    is ignored for non-terminal steps.
    """
    reading_type, lo, hi, source, tp = step_spec(conn, step)
    next_pass = step["next_on_pass"]
    next_fail = step["next_on_fail"]
    terminal = step["terminal_result"]
    if next_pass is None and next_fail is None and note:
        terminal = f"{terminal}\n\n{note}" if terminal else note
    return GuidedStep(
        session_id=session_id,
        available=True,
        tree_id=step["tree_id"],
        step_id=step["id"],
        step_order=int(step["step_order"]),
        instruction=step["instruction"],
        question=step["question"],
        safety_warnings=_step_warning_texts(conn, step, tp),
        reading_type=reading_type,
        test_point_id=step["test_point_id"],
        spec_min=lo,
        spec_max=hi,
        spec_source=source,
        terminal_result=terminal,
        next_on_pass=next_pass,
        next_on_fail=next_fail,
    )


def advance(conn: sqlite3.Connection, session: Any, step_id: str, verdict: Optional[str]) -> sqlite3.Row:
    """The next step row for a verdict on `step_id` (ADR-009 §3, step 3). `session` supplies `tree_id`.

    - terminal step (both branches null): returns the same step.
    - 'pass' -> next_on_pass; 'fail' -> next_on_fail.
    - 'na' -> the same step (no branch is taken without a decision; conservative, a tech must answer).
    - None -> next_on_pass only when there is no fail branch; otherwise the same step (a decision is still needed).
    Raises LookupError when the step is not in the session's tree and TreeIntegrityError on a dangling reference.
    """
    tree_id = session.tree_id
    current = load_step(conn, tree_id, step_id) if tree_id else None
    if current is None:
        raise LookupError("step is not part of this session's tree")
    on_pass = current["next_on_pass"]
    on_fail = current["next_on_fail"]
    if on_pass is None and on_fail is None:
        return current
    if verdict == "pass":
        target = on_pass
    elif verdict == "fail":
        target = on_fail
    elif verdict == "na":
        target = None
    elif verdict is None:
        target = on_pass if on_fail is None else None
    else:
        raise ValueError(f"unknown verdict: {verdict!r}")
    if target is None:
        return current
    nxt = load_step(conn, tree_id, target)
    if nxt is None:
        raise TreeIntegrityError(f"step {step_id} points at missing step {target}")
    return nxt


def reading_note(conn: sqlite3.Connection, step: sqlite3.Row, value: float, unit: str, verdict: Optional[str]) -> str:
    """A short annotation: the reading in canonical units, plus a warning if the service check disagrees with the app.

    The app's verdict stays authoritative (ADR-009 §3). This only makes the leaf explanation self-describing.
    """
    canon = units.to_canonical(value, unit)
    canon_unit = units.canonical_unit(unit)
    reading_type, lo, hi, _source, _tp = step_spec(conn, step)
    text = f"Reading at step {step['id']}: {value:g} {unit} = {canon:.6g} {canon_unit} (canonical)."
    if reading_type and canon_unit == units.canonical_unit_for_reading_type(reading_type):
        service = judge(canon, lo, hi)
        if verdict and service != "na" and verdict != service:
            log.warning("app verdict differs from the service spec check")
            text += (
                f" The app verdict ({verdict}) differs from the service check against the spec ({service});"
                " the app verdict was recorded."
            )
    return text
