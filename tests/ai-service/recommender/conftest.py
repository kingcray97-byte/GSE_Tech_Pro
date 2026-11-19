"""Shared fixtures for recommender tests (ADR-010). Synthetic, illustrative histories only; no real shop data.

- Puts ai-service/ on sys.path so `import recommender` / `import rag` work without installing the package.
- Builds a temp database by running the REAL 001_Initial.sql (never a hand-written schema).
- `db` has NO work_orders.number column (ADR-010 §8 "absent" path); `db_numbered` adds it the way migration 005
  will, so both evidence paths are exercised.
- `hashing_test_embedder` is a TEST DOUBLE used only to exercise the vector path. Never used for fallback asserts.
"""

from __future__ import annotations

import hashlib
import math
import re
import sys
import uuid
from dataclasses import dataclass
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[3]
AI_SERVICE = REPO / "ai-service"
if str(AI_SERVICE) not in sys.path:
    sys.path.insert(0, str(AI_SERVICE))

from rag.db import connect  # noqa: E402
from support import iso_days_ago  # noqa: E402

MIGRATIONS = REPO / "src" / "GseTechPro.Data" / "Migrations"


class Seed:
    """Tiny builder over the real schema. Every insert is autocommit unless wrapped in BEGIN/COMMIT."""

    def __init__(self, conn, numbered: bool):
        self.conn = conn
        self.numbered = numbered

    @staticmethod
    def _id() -> str:
        return str(uuid.uuid4())

    def model(self, make: str, model: str, category: str | None) -> str:
        mid = self._id()
        self.conn.execute(
            "INSERT INTO equipment_models (id, make, model, category, created_at, updated_at) VALUES (?,?,?,?,?,?)",
            (mid, make, model, category, iso_days_ago(900), iso_days_ago(900)),
        )
        return mid

    def asset(self, model_id: str | None) -> str:
        aid = self._id()
        self.conn.execute(
            "INSERT INTO assets (id, asset_number, model_id, created_at, updated_at) VALUES (?,?,?,?,?)",
            # Full UUID: guaranteed unique (first-8-hex prefixes collide at ~3000 rows).
            (aid, aid, model_id, iso_days_ago(900), iso_days_ago(900)),
        )
        return aid

    def symptom(self, name: str, system: str | None) -> str:
        sid = self._id()
        self.conn.execute(
            "INSERT INTO symptoms (id, name, system, created_at, updated_at) VALUES (?,?,?,?,?)",
            (sid, name, system, iso_days_ago(900), iso_days_ago(900)),
        )
        return sid

    def work_order(
        self,
        asset_id: str,
        *,
        number: str | None = None,
        complaint_summary: str | None = None,
        symptoms_summary: str | None = None,
        repair_summary: str | None = None,
        opened_days_ago: float = 60,
    ) -> str:
        if number is not None and not self.numbered:
            raise AssertionError("work order number requested but work_orders.number is absent in this fixture")
        wid = self._id()
        base_cols = "id, asset_id, status, opened_at, complaint_summary, symptoms_summary, repair_summary, created_at, updated_at"
        values: list = [
            wid, asset_id, "closed", iso_days_ago(opened_days_ago), complaint_summary, symptoms_summary,
            repair_summary, iso_days_ago(opened_days_ago), iso_days_ago(opened_days_ago),
        ]
        if self.numbered:
            base_cols += ", number"
            values.append(number)
        marks = ", ".join("?" for _ in values)
        self.conn.execute(f"INSERT INTO work_orders ({base_cols}) VALUES ({marks})", values)
        return wid

    def link_symptoms(self, wo: str, *symptom_ids: str) -> None:
        for sid in symptom_ids:
            self.conn.execute(
                "INSERT INTO work_order_symptoms (work_order_id, symptom_id, noted_at) VALUES (?,?,?)",
                (wo, sid, iso_days_ago(60)),
            )

    def dtc(self, wo: str, code: str, protocol: str = "J1939") -> None:
        self.conn.execute(
            "INSERT INTO dtcs (id, work_order_id, code, protocol, observed_at, created_at, updated_at) "
            "VALUES (?,?,?,?,?,?,?)",
            (self._id(), wo, code, protocol, iso_days_ago(60), iso_days_ago(60), iso_days_ago(60)),
        )

    def reading(self, wo: str, system: str) -> None:
        self.conn.execute(
            "INSERT INTO readings (id, work_order_id, reading_type, system, value_canonical, unit_canonical, "
            "entered_at, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?)",
            (self._id(), wo, "voltage", system, 12.0, "V", iso_days_ago(60), iso_days_ago(60), iso_days_ago(60)),
        )

    def repair(
        self,
        wo: str,
        description: str,
        *,
        system: str | None,
        labor: float | None = None,
        days_ago: float = 10,
    ) -> str:
        rid = self._id()
        self.conn.execute(
            "INSERT INTO repairs (id, work_order_id, description, system, labor_hours, performed_at, "
            "created_at, updated_at) VALUES (?,?,?,?,?,?,?,?)",
            (rid, wo, description, system, labor, iso_days_ago(days_ago), iso_days_ago(days_ago), iso_days_ago(days_ago)),
        )
        return rid

    def outcome(self, wo: str, result: str, days_ago: float = 5) -> None:
        self.conn.execute(
            "INSERT INTO outcomes (id, work_order_id, result, closed_at, created_at, updated_at) VALUES (?,?,?,?,?,?)",
            (self._id(), wo, result, iso_days_ago(days_ago), iso_days_ago(days_ago), iso_days_ago(days_ago)),
        )

    def comeback(self, original_wo: str, *, confirmed: bool) -> None:
        asset = self.conn.execute("SELECT asset_id FROM work_orders WHERE id = ?", (original_wo,)).fetchone()[0]
        later = self.work_order(asset, complaint_summary="came back")
        self.conn.execute(
            "INSERT INTO comebacks (id, original_work_order_id, comeback_work_order_id, auto, confirmed, "
            "detected_at, created_at) VALUES (?,?,?,?,?,?,?)",
            (self._id(), original_wo, later, 1, 1 if confirmed else 0, iso_days_ago(2), iso_days_ago(2)),
        )

    def closed_case(
        self,
        asset_id: str,
        description: str,
        *,
        system: str | None = "hydraulic",
        result: str = "fixed",
        days_ago: float = 10,
        labor: float | None = 2.0,
        symptoms: tuple[str, ...] = (),
        dtcs: tuple[str, ...] = (),
        number: str | None = None,
        complaint_summary: str | None = None,
        symptoms_summary: str | None = None,
        repair_summary: str | None = None,
    ) -> str:
        """One closed work order: WO + symptoms + DTCs + one repair + outcome. Returns the WO id."""
        wo = self.work_order(
            asset_id,
            number=number,
            complaint_summary=complaint_summary,
            symptoms_summary=symptoms_summary,
            repair_summary=repair_summary,
            opened_days_ago=days_ago + 1,
        )
        self.link_symptoms(wo, *symptoms)
        for code in dtcs:
            self.dtc(wo, code)
        self.repair(wo, description, system=system, labor=labor, days_ago=days_ago)
        self.outcome(wo, result, days_ago=days_ago)
        return wo

    def soft_delete_wo(self, wo: str) -> None:
        self.conn.execute("UPDATE work_orders SET deleted_at = ? WHERE id = ?", (iso_days_ago(1), wo))

    def soft_delete_repairs(self, wo: str) -> None:
        self.conn.execute("UPDATE repairs SET deleted_at = ? WHERE work_order_id = ?", (iso_days_ago(1), wo))


@dataclass
class Db:
    path: Path
    seed: Seed


def _build(tmp_path: Path, numbered: bool):
    """Create the DB from the real 001 migration. Yields a Db; the seed connection is closed afterwards."""
    path = tmp_path / "gse-tech-pro.db"
    setup = connect(path, load_vec=False)
    try:
        setup.executescript((MIGRATIONS / "001_Initial.sql").read_text(encoding="utf-8"))
        if numbered:  # ADR-010 §8 item 1, as migration 005 will add it
            setup.execute("ALTER TABLE work_orders ADD COLUMN number TEXT")
            setup.execute("CREATE UNIQUE INDEX idx_wo_number ON work_orders(number) WHERE number IS NOT NULL")
    finally:
        setup.close()
    seed_conn = connect(path, load_vec=False)
    try:
        yield Db(path=path, seed=Seed(seed_conn, numbered))
    finally:
        seed_conn.close()


@pytest.fixture
def db(tmp_path: Path):
    """Schema from 001 only: work_orders has NO number column."""
    yield from _build(tmp_path, numbered=False)


@pytest.fixture
def db_numbered(tmp_path: Path):
    """Schema from 001 plus work_orders.number (migration 005 shape)."""
    yield from _build(tmp_path, numbered=True)


_TOKEN = re.compile(r"[a-z0-9]+")


def hashing_test_embedder(texts: list[str]) -> list[list[float]]:
    """TEST DOUBLE ONLY: deterministic 768-d bag-of-words hash vectors (same trick as the RAG conftest)."""
    out = []
    for text in texts:
        vec = [0.0] * 768
        for tok in _TOKEN.findall(text.lower()):
            h = int(hashlib.md5(tok.encode()).hexdigest(), 16)
            vec[h % 768] += 1.0
        norm = math.sqrt(sum(x * x for x in vec)) or 1.0
        out.append([x / norm for x in vec])
    return out


@pytest.fixture
def unavailable_embedder():
    """Stands in for 'Ollama absent' at the callable boundary: always returns None."""

    def _none(texts):
        return None

    return _none
