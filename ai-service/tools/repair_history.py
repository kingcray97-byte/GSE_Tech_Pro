"""Phase 5 chat tools: similar repairs, asset lookup, repair and part history (ADR-010, ARCHITECTURE §4.4).

Every handler is read-only (SELECT only). The AI service never writes outcomes, comebacks, repairs, or work orders
(single-writer rule, ADR-008/010). `find_similar_repairs` calls the same `recommender` code as POST
/search/repair-history; the model explains the ranked rows and never computes a rate.

Results are short plain text (small context for a 2B model). Work orders are cited with a `work-order` ToolSource
so the chat turns them into citations. Missing data is reported as missing; nothing is inferred.
"""

from __future__ import annotations

import sqlite3
from typing import Any, Optional, Sequence

from pydantic import BaseModel, Field

from rag.db import connect
from recommender import UnknownWorkOrderError, run_repair_history_search
from recommender.scoring import wo_display_number
from tools.registry import MAX_TOOL_RESULT_CHARS, ToolContext, ToolResult, ToolSource, ToolSpec

FIND_TOP_K = 5
MAX_HISTORY_WORK_ORDERS = 6
MAX_PART_ROWS = 12
MAX_ASSET_ROWS = 3
_LINE_CAP = 220  # one history line never dominates the result
_DESCRIPTION_CAP = 120  # a repair description in a similar-repair line
_HEADER_BUDGET = 200  # room for a header and a "not shown" note so the registry clip never cuts a line
_NOT_RECORDED = "not recorded"


# ---------------------------------------------------------------- arguments


class GetAssetArgs(BaseModel):
    identifier: str = Field(min_length=1, max_length=64, description="Asset number, VIN, or serial number.")


class GetRepairHistoryArgs(BaseModel):
    asset_id: str = Field(min_length=1, max_length=64, description="Asset id (UUID) from get_asset.")


class GetPartHistoryArgs(BaseModel):
    part_number: Optional[str] = Field(default=None, max_length=64)
    asset_id: Optional[str] = Field(default=None, max_length=64, description="Asset id (UUID) from get_asset.")


class FindSimilarRepairsArgs(BaseModel):
    work_order_id: str = Field(min_length=1, max_length=64)


# ---------------------------------------------------------------- shared helpers


def _open(ctx: ToolContext) -> Optional[sqlite3.Connection]:
    if ctx.db_path is None:
        return None
    return connect(ctx.db_path, readonly=True, load_vec=False)


def _no_database() -> ToolResult:
    return ToolResult(False, "Repair history is unavailable: no database is configured.")


def _wo_number_expr(conn: sqlite3.Connection) -> str:
    """`work_orders.number` arrives in migration 5; detect it instead of assuming (same rule as the recommender)."""
    has_number = any(row["name"] == "number" for row in conn.execute("PRAGMA table_info(work_orders)").fetchall())
    return "w.number" if has_number else "NULL"


def _text(value: Any) -> str:
    """A recorded value, or the honest 'not recorded'. Never a guess."""
    if value is None or not str(value).strip():
        return _NOT_RECORDED
    return " ".join(str(value).split())


def _day(stamp: Any) -> str:
    return str(stamp)[:10] if stamp else _NOT_RECORDED


def _fit(lines: list[str], budget: int) -> tuple[list[str], int]:
    """Whole lines only, while they fit in `budget`. Returns (kept, dropped_count)."""
    kept: list[str] = []
    used = 0
    for line in lines:
        cost = len(line) + 1
        if used + cost > budget:
            break
        kept.append(line)
        used += cost
    return kept, len(lines) - len(kept)


def _wo_source(label: str) -> ToolSource:
    return ToolSource(kind="work-order", display_text=f"WO #{label}", work_order_number=label)


def _unique_sources(sources: Sequence[ToolSource]) -> tuple[ToolSource, ...]:
    seen: dict[str, ToolSource] = {}
    for src in sources:
        seen.setdefault(src.display_text, src)
    return tuple(seen.values())


def _placeholders(count: int) -> str:
    return ", ".join("?" for _ in range(count))


# ---------------------------------------------------------------- find_similar_repairs


def _work_order_labels(ctx: ToolContext, work_order_ids: Sequence[str]) -> dict[str, str]:
    """Display label (WO number, or the temporary id prefix) for each work order id. Read-only."""
    if not work_order_ids:
        return {}
    conn = _open(ctx)
    if conn is None:
        return {}
    try:
        number = _wo_number_expr(conn)
        rows = conn.execute(
            f"SELECT w.id AS id, {number} AS number FROM work_orders AS w "
            f"WHERE w.id IN ({_placeholders(len(work_order_ids))})",
            list(work_order_ids),
        ).fetchall()
    finally:
        conn.close()
    return {row["id"]: wo_display_number(row["number"], row["id"]) for row in rows}


def _find_similar_repairs(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, FindSimilarRepairsArgs)
    if ctx.db_path is None:
        return _no_database()
    try:
        out = run_repair_history_search(
            ctx.db_path, args.work_order_id, top_k=FIND_TOP_K, query_embedder=ctx.embedder
        )
    except UnknownWorkOrderError:
        return ToolResult(False, f"No work order matches '{args.work_order_id}'. Do not guess a repair.")
    except ValueError:
        return ToolResult(False, "Invalid work order id. Do not guess a repair.")
    candidates = out["candidates"]
    if not candidates:
        return ToolResult(
            True, "No similar past repairs with a recorded outcome. Do not guess a repair; check the manuals."
        )
    first_ids = [c["source_work_order_ids"][0] for c in candidates if c["source_work_order_ids"]]
    labels = _work_order_labels(ctx, first_ids)
    lines: list[str] = []
    sources: list[ToolSource] = []
    for i, c in enumerate(candidates, start=1):
        pct = round(float(c["success_rate"]) * 100)
        description = _text(c.get("repair_description"))[:_DESCRIPTION_CAP]
        system = _text(c.get("system"))
        lines.append(
            f"[{i}] {description} ({system}): {c['evidence']} ({c['match_tier']} match, success rate {pct}%)"
        )
        label = labels.get(c["source_work_order_ids"][0], "") if c["source_work_order_ids"] else ""
        sources.append(
            ToolSource(kind="work-order", display_text=c["evidence"], work_order_number=label or None)
        )
    # Whole candidates only: the registry clips at a character count, which could cut a WO number mid-line.
    kept, dropped = _fit(lines, MAX_TOOL_RESULT_CHARS - _HEADER_BUDGET)
    note = " (text-only ranking: no vectors)" if out["fallback"] else ""
    header = (
        f"Similar past repairs{note}. These are suggestions from recorded outcomes; "
        "the technician decides. Cite the WO number for each:"
    )
    body = header + "\n" + "\n".join(kept)
    if dropped:
        body += f"\n{dropped} more candidate(s) not shown here."
    return ToolResult(True, body, _unique_sources(sources[: len(kept)]))


FIND_SIMILAR_REPAIRS = ToolSpec(
    name="find_similar_repairs",
    description=(
        "Past failures like this work order and the repairs that fixed them, with success counts and WO numbers. "
        "Use for 'what fixed this before'. Quote the WO number for each suggestion."
    ),
    args_model=FindSimilarRepairsArgs,
    handler=_find_similar_repairs,
)


# ---------------------------------------------------------------- get_asset


def _asset_line(row: sqlite3.Row) -> str:
    make_model = " ".join(p for p in (row["make"], row["model"]) if p and str(p).strip()) or _NOT_RECORDED
    return (
        f"Asset {row['asset_number']} (id {row['id']}): {make_model}, category {_text(row['category'])}. "
        f"Kind: {_text(row['asset_kind'])}. Status: {_text(row['status'])}. Name: {_text(row['name'])}. "
        f"Serial: {_text(row['serial_number'])}. VIN: {_text(row['vin'])}. "
        f"Location: {_text(row['location_station'])}. "
        f"Hour meter: {_text(row['hour_meter'])} as of {_day(row['hour_meter_as_of'])}."
    )


def _get_asset(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, GetAssetArgs)
    conn = _open(ctx)
    if conn is None:
        return _no_database()
    ident = args.identifier.strip()
    try:
        rows = conn.execute(
            """
            SELECT a.id AS id, a.asset_number AS asset_number, a.asset_kind AS asset_kind, a.name AS name,
                   a.serial_number AS serial_number, a.status AS status, a.location_station AS location_station,
                   a.hour_meter AS hour_meter, a.hour_meter_as_of AS hour_meter_as_of,
                   em.make AS make, em.model AS model, em.category AS category, v.vin AS vin
            FROM assets AS a
            LEFT JOIN equipment_models AS em ON em.id = a.model_id AND em.deleted_at IS NULL
            LEFT JOIN vehicles AS v ON v.asset_id = a.id AND v.deleted_at IS NULL
            WHERE a.deleted_at IS NULL
              AND (lower(trim(a.asset_number)) = lower(?)
                   OR lower(trim(a.serial_number)) = lower(?)
                   OR lower(trim(v.vin)) = lower(?))
            ORDER BY a.asset_number
            LIMIT ?
            """,
            (ident, ident, ident, MAX_ASSET_ROWS),
        ).fetchall()
    finally:
        conn.close()
    if not rows:
        return ToolResult(True, f"No asset matches '{ident}'. Do not guess an asset.")
    return ToolResult(True, "\n".join(_asset_line(r) for r in rows))


GET_ASSET = ToolSpec(
    name="get_asset",
    description=(
        "Look up an asset by asset number, VIN, or serial number: make/model, status, location, hour meter, "
        "and its asset id (needed by get_repair_history and get_part_history)."
    ),
    args_model=GetAssetArgs,
    handler=_get_asset,
)


# ---------------------------------------------------------------- get_repair_history


def _history_line(wo: sqlite3.Row, repairs: list[sqlite3.Row], outcome: Optional[str],
                  comebacks: list[sqlite3.Row], label: str) -> str:
    if repairs:
        parts = []
        for r in repairs[:3]:
            desc = _text(r["description"])[:120]
            parts.append(f"{desc} ({_text(r['system'])})")
        repair_text = "; ".join(parts)
    else:
        repair_text = "no repair recorded"
    if any(c["confirmed"] for c in comebacks):
        comeback_text = "confirmed comeback"
    elif comebacks:
        reason = _text(comebacks[0]["match_reason"])[:80]
        comeback_text = f"possible comeback, not confirmed ({reason})"
    else:
        comeback_text = "no comeback"
    line = (
        f"WO #{label} opened {_day(wo['opened_at'])}, status {_text(wo['status'])}. "
        f"Repair: {repair_text}. Outcome: {outcome or 'not closed'}. Comeback: {comeback_text}."
    )
    return line[:_LINE_CAP]


def _get_repair_history(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, GetRepairHistoryArgs)
    conn = _open(ctx)
    if conn is None:
        return _no_database()
    try:
        number = _wo_number_expr(conn)
        wos = conn.execute(
            f"""
            SELECT w.id AS id, {number} AS number, w.opened_at AS opened_at, w.status AS status
            FROM work_orders AS w
            WHERE w.asset_id = ? AND w.deleted_at IS NULL
            ORDER BY w.opened_at DESC
            LIMIT ?
            """,
            (args.asset_id.strip(), MAX_HISTORY_WORK_ORDERS + 1),
        ).fetchall()
        if not wos:
            return ToolResult(True, "No repair history for this asset.")
        has_more = len(wos) > MAX_HISTORY_WORK_ORDERS  # one extra row was fetched only to know this
        wos = wos[:MAX_HISTORY_WORK_ORDERS]
        ids = [w["id"] for w in wos]
        marks = _placeholders(len(ids))
        repairs = conn.execute(
            f"SELECT work_order_id, description, system FROM repairs "
            f"WHERE deleted_at IS NULL AND work_order_id IN ({marks}) ORDER BY performed_at",
            ids,
        ).fetchall()
        outcomes = conn.execute(
            f"SELECT work_order_id, result FROM outcomes WHERE work_order_id IN ({marks})", ids
        ).fetchall()
        comebacks = conn.execute(
            f"SELECT original_work_order_id, confirmed, match_reason FROM comebacks "
            f"WHERE original_work_order_id IN ({marks})",
            ids,
        ).fetchall()
    finally:
        conn.close()
    by_repairs: dict[str, list[sqlite3.Row]] = {i: [] for i in ids}
    for r in repairs:
        by_repairs[r["work_order_id"]].append(r)
    outcome_of = {o["work_order_id"]: o["result"] for o in outcomes}
    comebacks_of: dict[str, list[sqlite3.Row]] = {i: [] for i in ids}
    for c in comebacks:
        comebacks_of[c["original_work_order_id"]].append(c)

    labels = {w["id"]: wo_display_number(w["number"], w["id"]) for w in wos}
    lines = [
        _history_line(w, by_repairs[w["id"]], outcome_of.get(w["id"]), comebacks_of[w["id"]], labels[w["id"]])
        for w in wos
    ]
    kept, dropped = _fit(lines, MAX_TOOL_RESULT_CHARS - _HEADER_BUDGET)
    body = ["Repair history for this asset, newest first:", *kept]
    if dropped or has_more:
        body.append("Older work orders are not shown here.")
    sources = _unique_sources([_wo_source(labels[w["id"]]) for w in wos[: len(kept)]])
    return ToolResult(True, "\n".join(body), sources)


GET_REPAIR_HISTORY = ToolSpec(
    name="get_repair_history",
    description=(
        "Recent work orders for an asset with their repairs, recorded outcomes, and comebacks. "
        "Use the asset id from get_asset. Cite each WO number you mention."
    ),
    args_model=GetRepairHistoryArgs,
    handler=_get_repair_history,
)


# ---------------------------------------------------------------- get_part_history


def _get_part_history(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, GetPartHistoryArgs)
    part = (args.part_number or "").strip()
    asset = (args.asset_id or "").strip()
    if not part and not asset:
        return ToolResult(False, "Give a part number or an asset id to search part history.")
    conn = _open(ctx)
    if conn is None:
        return _no_database()
    try:
        number = _wo_number_expr(conn)
        rows = conn.execute(
            f"""
            SELECT p.part_number AS part_number, p.description AS description, p.quantity AS quantity,
                   p.installed_at AS installed_at, p.hours_at_install AS hours_at_install,
                   w.id AS wo_id, {number} AS number
            FROM parts_replaced AS p
            JOIN work_orders AS w ON w.id = p.work_order_id
            WHERE w.deleted_at IS NULL
              AND (? = '' OR lower(trim(p.part_number)) = lower(?))
              AND (? = '' OR w.asset_id = ?)
            ORDER BY p.installed_at DESC
            LIMIT ?
            """,
            (part, part, asset, asset, MAX_PART_ROWS + 1),
        ).fetchall()
    finally:
        conn.close()
    if not rows:
        return ToolResult(True, "No part replacement history.")
    has_more = len(rows) > MAX_PART_ROWS
    rows = rows[:MAX_PART_ROWS]
    labels = {r["wo_id"]: wo_display_number(r["number"], r["wo_id"]) for r in rows}
    lines = []
    for r in rows:
        hours = f"{float(r['hours_at_install']):g} hrs" if r["hours_at_install"] is not None else _NOT_RECORDED
        line = (
            f"{_text(r['part_number'])} {_text(r['description'])} x{r['quantity']}, "
            f"installed {_day(r['installed_at'])}, at {hours} (WO #{labels[r['wo_id']]})."
        )
        lines.append(line[:_LINE_CAP])
    kept, dropped = _fit(lines, MAX_TOOL_RESULT_CHARS - _HEADER_BUDGET)
    body = ["Part replacements, newest first:", *kept]
    if dropped or has_more:
        body.append("More replacements are not shown here.")
    sources = _unique_sources([_wo_source(labels[r["wo_id"]]) for r in rows[: len(kept)]])
    return ToolResult(True, "\n".join(body), sources)


GET_PART_HISTORY = ToolSpec(
    name="get_part_history",
    description="Parts replaced, by part number or by asset id: when, at what hours, and on which work order.",
    args_model=GetPartHistoryArgs,
    handler=_get_part_history,
)
