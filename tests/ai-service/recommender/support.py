"""Constants shared by recommender tests. Imported by name (`from support import ...`), never from conftest."""

from __future__ import annotations

from datetime import datetime, timedelta, timezone

# Fixed "today" so every relative date (and therefore every recency phrase) is deterministic.
NOW = datetime(2026, 11, 17, 12, 0, 0, tzinfo=timezone.utc)


def iso_days_ago(days: float) -> str:
    """ISO-8601 UTC with 7 fraction digits and Z, the same shape the C# data layer writes."""
    moment = NOW - timedelta(days=days)
    return f"{moment:%Y-%m-%dT%H:%M:%S}.0000000Z"


def make_models(seed) -> dict[str, str]:
    """Five equipment models that exercise every tier: m1 is the trigger's model (TUG MA-50, tug)."""
    return {
        "m1": seed.model("TUG", "MA-50", "tug"),  # trigger's model
        "m2": seed.model("TUG", "MA-60", "tug"),  # same make + category, other model -> family
        "m3": seed.model("Acme", "MA-50", "tug"),  # same category, other make -> all
        "m4": seed.model("Stark", "Loader", "loader"),  # other category, other make -> all
        "m5": seed.model("TUG", "MA-10", "forklift"),  # same make, other category -> all
    }


def open_trigger(seed, model_id, *, symptom_ids=(), dtc_codes=(), complaint_summary=None):
    """The work order a tech is diagnosing now: open, so it never contributes history. Returns (wo, asset)."""
    asset = seed.asset(model_id)
    wo = seed.work_order(asset, complaint_summary=complaint_summary, opened_days_ago=1)
    seed.link_symptoms(wo, *symptom_ids)
    for code in dtc_codes:
        seed.dtc(wo, code)
    return wo, asset
