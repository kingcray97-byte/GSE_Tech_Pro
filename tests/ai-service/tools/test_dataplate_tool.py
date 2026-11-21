"""Phase 7.3 `ocr_dataplate` (tools/dataplate.py): reads only the technician-confirmed data plate the app saved.

Synthetic, migrated database (real migrations). Values are illustrative test strings, not real equipment data.
"""

from __future__ import annotations

import json
import uuid

import ai_support  # noqa: F401  (puts ai-service/ on sys.path)
from ai_support import offline_config
from app.bootstrap import ensure_schema
from rag.db import connect
from repair_history_support import counts, iso_days_ago
from tools import ToolContext
from tools.catalog import default_registry


def _asset(conn, number: str, serial: str | None, config: dict | str | None) -> str:
    aid = str(uuid.uuid4())
    cfg = json.dumps(config) if isinstance(config, dict) else config
    conn.execute(
        "INSERT INTO assets (id, asset_kind, asset_number, serial_number, status, configuration_json, created_at, "
        "updated_at) VALUES (?,?,?,?,?,?,?,?)",
        (aid, "equipment", number, serial, "in_service", cfg, iso_days_ago(10), iso_days_ago(1)),
    )
    return aid


def _build(path):
    ensure_schema(path)
    conn = connect(path, load_vec=False)
    try:
        ids = {
            "read": _asset(conn, "TUG-701", "TEST-SN-77", {
                "other_key": 1,
                "data_plate": {
                    "version": 1, "manufacturer": "TEST WORKS", "model": "TM-1", "serial_number": "TEST-SN-77",
                    "part_numbers": ["TP-001", "TP-002"], "manufacture_date": "2019-04",
                    "raw_text": "MODEL TM-1 S/N TEST-SN-77", "engine": "Windows OCR (English (United States))",
                    "is_simulated": False, "previous_serial_number": "OLD-1", "technician_confirmed": True,
                    "confirmed_at": "2026-11-21T10:00:00.0000000Z",
                },
            }),
            "sim": _asset(conn, "SIM-ASSET-01", None, {
                "data_plate": {"serial_number": "SIM-0O1I7", "engine": "Simulator OCR", "is_simulated": True,
                               "confirmed_at": "2026-11-21T10:00:00.0000000Z", "part_numbers": []},
            }),
            "none": _asset(conn, "TUG-702", "SN-2", None),
            "bad": _asset(conn, "TUG-703", None, "{not json"),
        }
        conn.commit()
        return ids
    finally:
        conn.close()


def _invoke(args, db):
    return default_registry().invoke("ocr_dataplate", args, ToolContext(db_path=db, config=offline_config()))


def test_confirmed_reading_is_returned_exactly_with_provenance(tmp_path):
    db = tmp_path / "plate.db"
    ids = _build(db)
    before = counts(db)

    out = _invoke({"identifier": "tug-701"}, db)
    assert out.ok is True
    text = out.content
    assert "confirmed by the technician on 2026-11-21" in text
    assert "Windows OCR" in text
    assert "Quote these values exactly" in text
    assert "Manufacturer: TEST WORKS" in text
    assert "Model: TM-1" in text
    assert "Serial number: TEST-SN-77" in text
    assert "Part numbers: TP-001, TP-002" in text
    assert "Build date: 2019-04" in text
    assert "earlier asset serial OLD-1" in text
    assert "SIMULATED" not in text

    # Same asset by id and by serial number.
    assert "TEST-SN-77" in _invoke({"identifier": ids["read"]}, db).content
    assert "TUG-701" in _invoke({"identifier": "TEST-SN-77"}, db).content
    assert counts(db) == before  # read-only


def test_simulated_missing_and_unknown_are_said_plainly(tmp_path):
    db = tmp_path / "plate.db"
    _build(db)

    sim = _invoke({"identifier": "SIM-ASSET-01"}, db).content
    assert "SIMULATED test reading, not a real plate" in sim
    assert "Part numbers: not on the reading" in sim

    none = _invoke({"identifier": "TUG-702"}, db).content
    assert "no confirmed data-plate reading" in none
    assert "Do not read or guess" in none
    assert "Read data plate" in none

    bad = _invoke({"identifier": "TUG-703"}, db).content  # unreadable configuration JSON = no reading
    assert "no confirmed data-plate reading" in bad

    unknown = _invoke({"identifier": "NOPE-1"}, db)
    assert unknown.ok is True
    assert "No asset matches" in unknown.content


def test_no_database_is_an_honest_failure():
    out = default_registry().invoke("ocr_dataplate", {"identifier": "TUG-701"},
                                    ToolContext(db_path=None, config=offline_config()))
    assert out.ok is False
    assert "no database" in out.content
