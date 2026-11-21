"""Phase 7.3 chat tool: `ocr_dataplate` (ARCHITECTURE §4 tools, rule 5).

Serial and data-plate text is read by deterministic offline OCR in the app (Windows.Media.Ocr), never by the
model, and the technician confirms or corrects every value before it is saved on the asset
(`assets.configuration_json["data_plate"]`, written by the app's Camera page). This tool only *reads* that
confirmed record (SELECT only, single-writer rule): it returns the values exactly as confirmed, with where they came
from, so the model quotes them instead of reading or guessing IDs. When nothing is on file it says so and tells
the technician how to read the plate.
"""

from __future__ import annotations

import json
import sqlite3
from typing import Any, Optional

from pydantic import BaseModel, Field

from rag.db import connect
from tools.registry import ToolContext, ToolResult, ToolSpec

MAX_ASSET_ROWS = 3
_VALUE_CAP = 64
_RAW_CAP = 400
_HOW_TO = "To read it: Camera > take or import a photo of the plate > Save to work order > Read data plate."


class OcrDataplateArgs(BaseModel):
    identifier: str = Field(
        min_length=1, max_length=64, description="Asset number, asset id, serial number, or VIN."
    )


def _text(value: Any, cap: int = _VALUE_CAP) -> str:
    s = "" if value is None else " ".join(str(value).split())
    return s[:cap]


def _plate(config_json: Optional[str]) -> Optional[dict[str, Any]]:
    if not config_json:
        return None
    try:
        root = json.loads(config_json)
    except (ValueError, TypeError):
        return None
    plate = root.get("data_plate") if isinstance(root, dict) else None
    return plate if isinstance(plate, dict) else None


def _plate_lines(asset_number: str, asset_serial: str, plate: dict[str, Any]) -> list[str]:
    simulated = bool(plate.get("is_simulated"))
    when = _text(plate.get("confirmed_at"))[:10] or "unknown date"
    engine = _text(plate.get("engine")) or "offline OCR"
    head = (
        f"Data plate for asset {asset_number}"
        f"{' (SIMULATED test reading, not a real plate)' if simulated else ''}: read by {engine} and "
        f"{'confirmed by the technician' if plate.get('technician_confirmed', True) else 'NOT confirmed'} on {when}. "
        "Quote these values exactly; do not correct or extend them."
    )
    lines = [head]
    for label, key in (
        ("Manufacturer", "manufacturer"),
        ("Model", "model"),
        ("Serial number", "serial_number"),
        ("Build date", "manufacture_date"),
    ):
        value = _text(plate.get(key))
        lines.append(f"{label}: {value or 'not on the reading'}")
    parts = [p for p in (_text(x) for x in (plate.get("part_numbers") or [])) if p]
    lines.append(f"Part numbers: {', '.join(parts) if parts else 'not on the reading'}")
    if asset_serial and _text(plate.get("serial_number")) and asset_serial.lower() != _text(plate.get("serial_number")).lower():
        lines.append(f"Note: the asset record's serial number is {asset_serial}, which differs from the plate reading.")
    if plate.get("previous_serial_number"):
        lines.append(f"The reading replaced the earlier asset serial {_text(plate.get('previous_serial_number'))}.")
    return lines


def _ocr_dataplate(ctx: ToolContext, args: BaseModel) -> ToolResult:
    assert isinstance(args, OcrDataplateArgs)
    if ctx.db_path is None:
        return ToolResult(False, "Data-plate readings are unavailable: no database is configured.")
    ident = args.identifier.strip()
    conn = connect(ctx.db_path, readonly=True, load_vec=False)
    try:
        rows: list[sqlite3.Row] = conn.execute(
            """
            SELECT a.asset_number AS asset_number, a.serial_number AS serial_number,
                   a.configuration_json AS configuration_json
            FROM assets AS a
            LEFT JOIN vehicles AS v ON v.asset_id = a.id AND v.deleted_at IS NULL
            WHERE a.deleted_at IS NULL
              AND (lower(trim(a.asset_number)) = lower(?)
                   OR lower(a.id) = lower(?)
                   OR lower(trim(a.serial_number)) = lower(?)
                   OR lower(trim(v.vin)) = lower(?))
            ORDER BY a.asset_number
            LIMIT ?
            """,
            (ident, ident, ident, ident, MAX_ASSET_ROWS),
        ).fetchall()
    finally:
        conn.close()
    if not rows:
        return ToolResult(True, f"No asset matches '{_text(ident)}'. Do not guess an asset or its data plate.")

    out: list[str] = []
    for row in rows:
        plate = _plate(row["configuration_json"])
        if plate is None:
            out.append(
                f"Asset {row['asset_number']} has no confirmed data-plate reading. Do not read or guess serial or "
                f"part numbers. {_HOW_TO}"
            )
        else:
            out.extend(_plate_lines(str(row["asset_number"]), _text(row["serial_number"]), plate))
    return ToolResult(True, "\n".join(out))


OCR_DATAPLATE = ToolSpec(
    name="ocr_dataplate",
    description=(
        "Get an asset's data-plate values (manufacturer, model, serial, part numbers, build date) as read by "
        "offline OCR and confirmed by the technician. Never read IDs from a photo yourself; use this tool."
    ),
    args_model=OcrDataplateArgs,
    handler=_ocr_dataplate,
)
