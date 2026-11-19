"""Synthetic repair-history fixtures for the Phase 5 route and chat tools (ADR-010). Illustrative data only.

Builds a database through the REAL migrations (app.bootstrap.ensure_schema), then inserts a small, fixed history:

- TUG MA-50 (trigger model) and TUG MA-60 (same make and category: family tier) and Acme MA-50 (all tier).
- Hydraulic pump group: 3 closed WOs (one on the trigger's model, two on the family) -> n=3; the confirmed comeback
  on WO 4471 demotes it, so s=1. Wilson lower bound is well below 1, and the group is `model` tier.
- Fuel filter group: 1 closed WO on the vehicle -> `all` tier.
- The trigger WO is open, so it never contributes history.
"""

from __future__ import annotations

import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import ai_support  # noqa: F401  (puts ai-service/ on sys.path)
from app.bootstrap import ensure_schema
from rag.db import connect


def iso_days_ago(days: float) -> str:
    moment = datetime.now(timezone.utc) - timedelta(days=days)
    return f"{moment:%Y-%m-%dT%H:%M:%S}.0000000Z"


def _id() -> str:
    return str(uuid.uuid4())


class Seed:
    def __init__(self, conn) -> None:
        self.conn = conn
        self.ids: dict[str, str] = {}

    def model(self, key: str, make: str, model: str, category: str) -> None:
        mid = _id()
        self.conn.execute(
            "INSERT INTO equipment_models (id, make, model, category, created_at, updated_at) VALUES (?,?,?,?,?,?)",
            (mid, make, model, category, iso_days_ago(900), iso_days_ago(900)),
        )
        self.ids[key] = mid

    def asset(self, key: str, number: str, model_key: str | None, *, kind: str = "equipment",
              serial: str | None = None, hours: float | None = None, location: str | None = None) -> None:
        aid = _id()
        self.conn.execute(
            "INSERT INTO assets (id, asset_kind, asset_number, model_id, serial_number, status, location_station, "
            "hour_meter, hour_meter_as_of, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
            (aid, kind, number, self.ids[model_key] if model_key else None, serial, "in_service", location,
             hours, iso_days_ago(2) if hours is not None else None, iso_days_ago(900), iso_days_ago(900)),
        )
        self.ids[key] = aid

    def vehicle(self, asset_key: str, vin: str) -> None:
        self.conn.execute(
            "INSERT INTO vehicles (asset_id, vin, created_at, updated_at) VALUES (?,?,?,?)",
            (self.ids[asset_key], vin, iso_days_ago(900), iso_days_ago(900)),
        )

    def work_order(self, key: str, asset_key: str, number: str, *, opened_days: float, status: str = "closed",
                   complaint: str | None = None, closed_days: float | None = None) -> None:
        wid = _id()
        self.conn.execute(
            "INSERT INTO work_orders (id, asset_id, number, status, opened_at, closed_at, complaint_summary, "
            "created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?)",
            (wid, self.ids[asset_key], number, status, iso_days_ago(opened_days),
             iso_days_ago(closed_days) if closed_days is not None else None, complaint,
             iso_days_ago(opened_days), iso_days_ago(opened_days)),
        )
        self.ids[key] = wid

    def repair(self, wo_key: str, description: str, system: str, *, days_ago: float, labor: float | None = 2.0) -> None:
        self.conn.execute(
            "INSERT INTO repairs (id, work_order_id, description, system, labor_hours, performed_at, created_at, "
            "updated_at) VALUES (?,?,?,?,?,?,?,?)",
            (_id(), self.ids[wo_key], description, system, labor, iso_days_ago(days_ago),
             iso_days_ago(days_ago), iso_days_ago(days_ago)),
        )

    def outcome(self, wo_key: str, result: str, *, days_ago: float) -> None:
        self.conn.execute(
            "INSERT INTO outcomes (id, work_order_id, result, closed_at, created_at, updated_at) VALUES (?,?,?,?,?,?)",
            (_id(), self.ids[wo_key], result, iso_days_ago(days_ago), iso_days_ago(days_ago), iso_days_ago(days_ago)),
        )

    def part(self, wo_key: str, part_number: str, description: str, *, days_ago: float, hours: float | None) -> None:
        self.conn.execute(
            "INSERT INTO parts_replaced (id, work_order_id, part_number, description, quantity, installed_at, "
            "hours_at_install, created_at) VALUES (?,?,?,?,?,?,?,?)",
            (_id(), self.ids[wo_key], part_number, description, 1, iso_days_ago(days_ago), hours,
             iso_days_ago(days_ago)),
        )

    def comeback(self, original_key: str, later_key: str, *, confirmed: bool, reason: str) -> None:
        self.conn.execute(
            "INSERT INTO comebacks (id, original_work_order_id, comeback_work_order_id, match_reason, auto, confirmed, "
            "detected_at, created_at) VALUES (?,?,?,?,?,?,?,?)",
            (_id(), self.ids[original_key], self.ids[later_key], reason, 1, 1 if confirmed else 0,
             iso_days_ago(15), iso_days_ago(15)),
        )


def build_seed(path: Path) -> dict[str, str]:
    """Migrate `path` with the real migrations and insert the fixed history. Returns the key -> id map."""
    ensure_schema(path)
    conn = connect(path, load_vec=False)
    try:
        conn.execute("BEGIN")
        s = Seed(conn)
        s.model("tug50", "TUG", "MA-50", "tug")
        s.model("tug60", "TUG", "MA-60", "tug")
        s.model("acme", "Acme", "MA-50", "forklift")
        s.asset("trig_asset", "TUG-01", "tug50", serial="SN-1001", hours=1234.5, location="Bay 3")
        s.asset("hist_asset", "TUG-02", "tug60", serial="SN-1002")
        s.asset("vehicle", "FL-07", "acme", kind="vehicle")
        s.vehicle("vehicle", "ACME1234567890XYZ")
        s.asset("empty_asset", "TUG-09", "tug50", serial="SN-1009")
        s.asset("big_asset", "TUG-10", "tug50", serial="SN-1010")

        s.work_order("trigger", "trig_asset", "5001", opened_days=1, status="open",
                     complaint="hydraulic pump whine on lift")
        s.asset("solo_asset", "TUG-11", "tug50", serial="SN-1011")
        s.work_order("lonely", "solo_asset", "5002", opened_days=1, status="open", complaint="no start")
        s.work_order("h1", "hist_asset", "4471", opened_days=21, closed_days=20)
        s.repair("h1", "Replace main hydraulic pump", "hydraulic", days_ago=20, labor=2.0)
        s.outcome("h1", "fixed", days_ago=20)
        s.part("h1", "HP-PUMP-100", "Main hydraulic pump", days_ago=20, hours=1200.0)
        s.work_order("h2", "hist_asset", "4480", opened_days=41, closed_days=40)
        s.repair("h2", "Replace main hydraulic pump", "hydraulic", days_ago=40, labor=2.0)
        s.outcome("h2", "fixed", days_ago=40)
        s.work_order("comeback", "hist_asset", "4485", opened_days=15, status="open", complaint="pump whine again")
        s.comeback("h1", "comeback", confirmed=True, reason="same system: hydraulic")
        s.work_order("h3", "trig_asset", "4300", opened_days=101, closed_days=100)
        s.repair("h3", "Replace main hydraulic pump", "hydraulic", days_ago=100, labor=2.0)
        s.outcome("h3", "not_fixed", days_ago=100)
        s.work_order("h4", "vehicle", "4390", opened_days=6, closed_days=5)
        s.repair("h4", "Replace fuel filter", "fuel", days_ago=5, labor=0.5)
        s.outcome("h4", "fixed", days_ago=5)
        # Many open work orders on one asset: history is shown, never closed, so no ranking group is created.
        for i in range(9):
            s.work_order(f"big{i}", "big_asset", f"60{i:02d}", opened_days=30 - i, status="open")
            s.repair(f"big{i}", f"Replace belt {i}", "drive", days_ago=30 - i)
        conn.execute("COMMIT")
        return dict(s.ids)
    except Exception:
        conn.execute("ROLLBACK")
        raise
    finally:
        conn.close()


def build_empty(path: Path) -> str:
    """Migrated database with NO closed work orders: returns the id of one open work order (no history to match)."""
    ensure_schema(path)
    conn = connect(path, load_vec=False)
    try:
        s = Seed(conn)
        s.model("m", "TUG", "MA-50", "tug")
        s.asset("a", "TUG-50", "m", serial="SN-5000")
        s.work_order("open", "a", "7001", opened_days=1, status="open", complaint="no start")
        return s.ids["open"]
    finally:
        conn.close()


def counts(path: Path) -> dict[str, int]:
    """Row counts of every table the AI service must never write (single-writer rule)."""
    conn = connect(path, readonly=True, load_vec=False)
    try:
        return {
            t: conn.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0]
            for t in ("work_orders", "repairs", "outcomes", "comebacks", "recommendations", "parts_replaced", "assets")
        }
    finally:
        conn.close()
