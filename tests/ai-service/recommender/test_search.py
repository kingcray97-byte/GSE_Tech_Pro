"""Database-level tests for run_repair_history_search (ADR-010 §2-§5, §8). Synthetic, illustrative histories only."""

from __future__ import annotations


import pytest

from recommender import UnknownWorkOrderError, run_repair_history_search
from recommender.scoring import wilson_lower_bound
from support import NOW, iso_days_ago, make_models, open_trigger


def run(db, wo, **kwargs):
    return run_repair_history_search(db.path, wo, now=NOW, **kwargs)


# ---------------------------------------------------------------- input validation


@pytest.mark.parametrize("bad", ["", "   ", None, 42])
def test_blank_or_non_string_work_order_id_is_value_error(db, bad):
    with pytest.raises(ValueError) as info:
        run(db, bad)
    assert not isinstance(info.value, UnknownWorkOrderError)


def test_unknown_work_order_raises_dedicated_error(db):
    make_models(db.seed)
    with pytest.raises(UnknownWorkOrderError) as info:
        run(db, "00000000-0000-4000-8000-000000000000")
    assert isinstance(info.value, ValueError)  # the route maps the subclass to 404


def test_soft_deleted_trigger_is_unknown(db):
    m = make_models(db.seed)
    wo, _ = open_trigger(db.seed, m["m1"])
    db.seed.soft_delete_wo(wo)
    with pytest.raises(UnknownWorkOrderError):
        run(db, wo)


@pytest.mark.parametrize("bad_k", [0, 51, True, "5", 2.0])
def test_top_k_must_be_an_int_between_1_and_50(db, bad_k):
    m = make_models(db.seed)
    wo, _ = open_trigger(db.seed, m["m1"])
    with pytest.raises(ValueError):
        run(db, wo, top_k=bad_k)


def test_query_text_over_2000_chars_is_rejected(db):
    m = make_models(db.seed)
    wo, _ = open_trigger(db.seed, m["m1"])
    with pytest.raises(ValueError):
        run(db, wo, query_text="x" * 2001)


def test_no_history_returns_empty_candidates_with_fallback(db):
    m = make_models(db.seed)
    wo, _ = open_trigger(db.seed, m["m1"])
    assert run(db, wo) == {"candidates": [], "fallback": True}


# ---------------------------------------------------------------- evidence string (ADR-010 §5)


def _seed_canonical(seed, m, *, newest_number):
    """20 closed WOs for 'Replace main hydraulic pump' on TUG MA-50: 18 fixed, 2 not_fixed, 2.1 hrs each."""
    symptom = seed.symptom("Low hydraulic pressure", "hydraulic")
    trigger, asset = open_trigger(seed, m["m1"], symptom_ids=(symptom,))
    for i in range(19):
        result = "not_fixed" if i < 2 else "fixed"
        seed.closed_case(
            asset, "Replace main hydraulic pump", system="hydraulic", result=result,
            days_ago=40 + i, labor=2.1, symptoms=(symptom,),
        )
    newest = seed.closed_case(
        asset, "Replace main hydraulic pump", system="hydraulic", result="fixed",
        days_ago=21, labor=2.1, symptoms=(symptom,), number=newest_number,
    )
    return trigger, newest


def test_canonical_evidence_with_work_order_number(db_numbered):
    m = make_models(db_numbered.seed)
    trigger, newest = _seed_canonical(db_numbered.seed, m, newest_number="4471")
    result = run(db_numbered, trigger)
    top = result["candidates"][0]
    assert top["evidence"] == "Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, last done 3 weeks ago, WO #4471"
    assert top["success_count"] == 18
    assert top["attempt_count"] == 20
    assert top["average_labor_hours"] == 2.1
    assert top["success_rate"] == pytest.approx(wilson_lower_bound(18, 20), abs=1e-4)
    assert top["match_tier"] == "model"
    assert top["repair_description"] == "Replace main hydraulic pump"
    assert top["system"] == "hydraulic"
    assert top["similarity_score"] == 1.0
    assert top["last_done_at"] == iso_days_ago(21)
    assert len(top["source_work_order_ids"]) == 20
    assert top["source_work_order_ids"][0] == newest  # most recent first
    assert result["fallback"] is True


def test_evidence_falls_back_to_id_prefix_when_number_column_absent(db):
    m = make_models(db.seed)
    trigger, newest = _seed_canonical(db.seed, m, newest_number=None)
    top = run(db, trigger)["candidates"][0]
    prefix = newest.replace("-", "")[:8]
    assert top["evidence"] == f"Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, last done 3 weeks ago, WO #{prefix}"


def test_evidence_falls_back_when_column_present_but_number_null(db_numbered):
    m = make_models(db_numbered.seed)
    trigger, newest = _seed_canonical(db_numbered.seed, m, newest_number=None)
    top = run(db_numbered, trigger)["candidates"][0]
    assert top["evidence"].endswith(f"WO #{newest.replace('-', '')[:8]}")


def test_evidence_omits_labor_clause_when_no_hours_recorded(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Coolant leak", None)
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    wo = db.seed.closed_case(asset, "Replace hose", system=None, labor=None, days_ago=10, symptoms=(symptom,))
    top = run(db, trigger)["candidates"][0]
    assert top["average_labor_hours"] is None
    assert top["evidence"] == f"Fixed 1 of 1 on TUG MA-50, last done 1 week ago, WO #{wo.replace('-', '')[:8]}"


# ---------------------------------------------------------------- smoothing + ranking (ADR-010 §4)


def test_wilson_ranks_45_for_50_above_1_for_1(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Hydraulic noise", "hydraulic")
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    for i in range(50):
        db.seed.closed_case(
            asset, "Replace hydraulic pump", system="hydraulic", result="not_fixed" if i < 5 else "fixed",
            days_ago=10, labor=2.0, symptoms=(symptom,),
        )
    db.seed.closed_case(asset, "Replace hose clamp", system="hydraulic", result="fixed", days_ago=10, labor=1.0, symptoms=(symptom,))
    cands = run(db, trigger)["candidates"]
    assert [c["repair_description"] for c in cands] == ["Replace hydraulic pump", "Replace hose clamp"]
    assert (cands[0]["success_count"], cands[0]["attempt_count"]) == (45, 50)
    assert (cands[1]["success_count"], cands[1]["attempt_count"]) == (1, 1)
    assert cands[1]["success_rate"] == pytest.approx(0.207, abs=1e-3)
    assert cands[0]["success_rate"] == pytest.approx(0.786, abs=1e-3)


def test_unsure_is_excluded_from_n_and_s(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Valve chatter", "hydraulic")
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    fixed_a = db.seed.closed_case(asset, "Replace valve", system="hydraulic", result="fixed", days_ago=10, symptoms=(symptom,))
    fixed_b = db.seed.closed_case(asset, "Replace valve", system="hydraulic", result="fixed", days_ago=12, symptoms=(symptom,))
    db.seed.closed_case(asset, "Replace valve", system="hydraulic", result="unsure", days_ago=14, symptoms=(symptom,))
    top = run(db, trigger)["candidates"][0]
    assert (top["success_count"], top["attempt_count"]) == (2, 2)
    assert set(top["source_work_order_ids"]) == {fixed_a, fixed_b}


def test_group_with_only_unsure_outcomes_is_dropped(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Valve chatter", "hydraulic")
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    db.seed.closed_case(asset, "Replace seal kit", system="hydraulic", result="unsure", days_ago=10, symptoms=(symptom,))
    assert run(db, trigger)["candidates"] == []


def test_not_fixed_counts_toward_n_but_not_s(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Engine hard start", None)
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    db.seed.closed_case(asset, "Clean fuel filter", system="fuel", result="fixed", days_ago=10, symptoms=(symptom,))
    db.seed.closed_case(asset, "Clean fuel filter", system="fuel", result="not_fixed", days_ago=11, symptoms=(symptom,))
    top = run(db, trigger)["candidates"][0]
    assert (top["success_count"], top["attempt_count"]) == (1, 2)
    assert top["evidence"].startswith("Fixed 1 of 2 on TUG MA-50")


def test_confirmed_comeback_demotes_fixed_to_failure_unconfirmed_does_not(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Solenoid no response", "electrical")
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    confirmed_wo = db.seed.closed_case(asset, "Replace solenoid A", system="electrical", days_ago=10, symptoms=(symptom,))
    db.seed.comeback(confirmed_wo, confirmed=True)
    unconfirmed_wo = db.seed.closed_case(asset, "Replace solenoid B", system="electrical", days_ago=10, symptoms=(symptom,))
    db.seed.comeback(unconfirmed_wo, confirmed=False)

    by_desc = {c["repair_description"]: c for c in run(db, trigger)["candidates"]}
    assert (by_desc["Replace solenoid A"]["success_count"], by_desc["Replace solenoid A"]["attempt_count"]) == (0, 1)
    assert by_desc["Replace solenoid A"]["evidence"].startswith("Fixed 0 of 1 on TUG MA-50")
    assert (by_desc["Replace solenoid B"]["success_count"], by_desc["Replace solenoid B"]["attempt_count"]) == (1, 1)


def test_soft_deleted_work_order_and_repairs_are_excluded(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Belt squeal", None)
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    belt = db.seed.closed_case(asset, "Replace belt", system="engine", days_ago=10, symptoms=(symptom,))
    db.seed.soft_delete_wo(belt)
    fan = db.seed.closed_case(asset, "Replace fan", system="engine", days_ago=10, symptoms=(symptom,))
    db.seed.soft_delete_repairs(fan)
    db.seed.closed_case(asset, "Replace pulley", system="engine", days_ago=10, symptoms=(symptom,))
    descriptions = [c["repair_description"] for c in run(db, trigger)["candidates"]]
    assert descriptions == ["Replace pulley"]


def test_open_work_order_contributes_nothing(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Gasket weep", None)
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    still_open = db.seed.work_order(asset)  # no outcomes row: not closed
    db.seed.link_symptoms(still_open, symptom)
    db.seed.repair(still_open, "Replace gasket", system="engine", labor=1.0, days_ago=2)
    assert run(db, trigger)["candidates"] == []


def test_trigger_never_recommends_its_own_repair(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Hard shift", None)
    asset = db.seed.asset(m["m1"])
    # The work order being looked at is itself closed with a fixed repair. It is the only history on this model.
    trigger = db.seed.closed_case(asset, "Replace shift solenoid", system="transmission", days_ago=2, symptoms=(symptom,))
    assert run(db, trigger)["candidates"] == []


def test_repair_key_is_case_and_whitespace_insensitive(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("No boom lift", "hydraulic")
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    db.seed.closed_case(asset, "Replace pump", system="Hydraulic", days_ago=10, symptoms=(symptom,))
    db.seed.closed_case(asset, "  replace PUMP", system="hydraulic ", days_ago=12, symptoms=(symptom,))
    cands = run(db, trigger)["candidates"]
    assert len(cands) == 1
    assert (cands[0]["success_count"], cands[0]["attempt_count"]) == (2, 2)


def test_top_k_limits_the_list(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Lamp out", "electrical")
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    for i in range(6):
        db.seed.closed_case(asset, f"Replace bulb {i}", system="electrical", days_ago=10, symptoms=(symptom,))
    assert len(run(db, trigger, top_k=3)["candidates"]) == 3


# ---------------------------------------------------------------- tiers (ADR-010 §2)


def _tier_history(seed, m):
    symptom = seed.symptom("Hydraulic pump whine", "hydraulic")
    trigger, _ = open_trigger(seed, m["m1"], symptom_ids=(symptom,))
    asset_m1 = seed.asset(m["m1"])
    asset_m2 = seed.asset(m["m2"])
    asset_m3 = seed.asset(m["m3"])
    asset_m4 = seed.asset(m["m4"])
    asset_m5 = seed.asset(m["m5"])
    # model tier: low score (1-for-1, labor 1.0)
    seed.closed_case(asset_m1, "Replace hose", system="hydraulic", days_ago=10, labor=1.0, symptoms=(symptom,))
    # family tier: much higher score (45-for-50)
    for i in range(50):
        seed.closed_case(
            asset_m2, "Replace main pump", system="hydraulic", result="not_fixed" if i < 5 else "fixed",
            days_ago=10, labor=2.0, symptoms=(symptom,),
        )
    # all tier: highest score of all (50-for-50), other make + category
    for _ in range(50):
        seed.closed_case(asset_m4, "Replace fuse", system="hydraulic", days_ago=10, symptoms=(symptom,))
    # all tier: same category other make, and same make other category
    seed.closed_case(asset_m3, "Replace relay", system="hydraulic", days_ago=10, symptoms=(symptom,))
    seed.closed_case(asset_m5, "Replace starter", system="hydraulic", days_ago=10, symptoms=(symptom,))
    return trigger


def test_model_tier_beats_family_beats_all_regardless_of_score(db):
    m = make_models(db.seed)
    trigger = _tier_history(db.seed, m)
    cands = run(db, trigger, top_k=5)["candidates"]
    assert [c["match_tier"] for c in cands] == ["model", "family", "all", "all", "all"]
    assert cands[0]["repair_description"] == "Replace hose"  # lowest rate, still first: tier is hard precedence
    assert cands[1]["repair_description"] == "Replace main pump"
    assert cands[2]["repair_description"] == "Replace fuse"  # best all-tier rate
    assert {c["repair_description"] for c in cands[3:]} == {"Replace relay", "Replace starter"}


def test_top_k_fills_in_tier_order(db):
    m = make_models(db.seed)
    trigger = _tier_history(db.seed, m)
    assert [c["match_tier"] for c in run(db, trigger, top_k=1)["candidates"]] == ["model"]
    assert [c["match_tier"] for c in run(db, trigger, top_k=2)["candidates"]] == ["model", "family"]


def test_group_with_any_work_order_on_trigger_model_is_model_tier(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Filter clog", None)
    trigger, _ = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,))
    on_family = db.seed.asset(m["m2"])
    on_model = db.seed.asset(m["m1"])
    db.seed.closed_case(on_family, "Replace filter", system="fuel", days_ago=10, symptoms=(symptom,))
    db.seed.closed_case(on_model, "Replace filter", system="fuel", days_ago=10, symptoms=(symptom,))
    db.seed.closed_case(on_family, "Replace gauge", system="fuel", days_ago=10, symptoms=(symptom,))
    db.seed.closed_case(on_family, "Replace gauge", system="fuel", days_ago=11, symptoms=(symptom,))
    cands = run(db, trigger)["candidates"]
    assert [(c["repair_description"], c["match_tier"]) for c in cands] == [
        ("Replace filter", "model"),
        ("Replace gauge", "family"),
    ]


def test_trigger_without_model_only_searches_all_tier(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Horn inop", "electrical")
    asset_no_model = db.seed.asset(None)
    trigger = db.seed.work_order(asset_no_model, opened_days_ago=1)
    db.seed.link_symptoms(trigger, symptom)
    db.seed.closed_case(db.seed.asset(m["m1"]), "Replace horn relay", system="electrical", days_ago=10, symptoms=(symptom,))
    cands = run(db, trigger)["candidates"]
    assert [c["match_tier"] for c in cands] == ["all"]


# ---------------------------------------------------------------- §3 similarity


def test_dtc_match_raises_similarity(db):
    m = make_models(db.seed)
    symptom = db.seed.symptom("Derate fault", "hydraulic")
    trigger, asset = open_trigger(db.seed, m["m1"], symptom_ids=(symptom,), dtc_codes=("SPN100",))
    db.seed.closed_case(asset, "Replace pressure sensor", system="hydraulic", days_ago=10,
                        symptoms=(symptom,), dtcs=("SPN100",))
    db.seed.closed_case(asset, "Replace hose", system="hydraulic", days_ago=10, symptoms=(symptom,))
    cands = {c["repair_description"]: c for c in run(db, trigger)["candidates"]}
    # DTC + symptom + system all match: (0.45+0.25+0.20)/0.90 = 1.0. Symptom + system only: (0.45+0.20)/0.90.
    assert cands["Replace pressure sensor"]["similarity_score"] == 1.0
    assert cands["Replace hose"]["similarity_score"] == pytest.approx(0.7222, abs=1e-4)


def test_text_similarity_is_jaccard_over_summaries(db):
    m = make_models(db.seed)
    trigger, asset = open_trigger(db.seed, m["m1"], complaint_summary="pump whine")
    db.seed.closed_case(asset, "Overhaul", system=None, days_ago=10, complaint_summary="pump whine at startup")
    top = run(db, trigger)["candidates"][0]
    # query {pump, whine}; candidate {pump, whine, startup, overhaul} ('at' is a stopword) -> 2/4
    assert top["similarity_score"] == pytest.approx(0.5, abs=1e-4)


def test_readings_and_repair_systems_on_the_trigger_count_as_system_matches(db):
    m = make_models(db.seed)
    trigger, asset = open_trigger(db.seed, m["m1"])
    db.seed.reading(trigger, "electrical")
    db.seed.closed_case(asset, "Replace alternator", system="electrical", days_ago=10)
    db.seed.closed_case(asset, "Replace pump", system="hydraulic", days_ago=10)
    cands = {c["repair_description"]: c for c in run(db, trigger)["candidates"]}
    assert cands["Replace alternator"]["similarity_score"] == 1.0  # only signal, matched -> renormalized to 1.0
    assert cands["Replace pump"]["similarity_score"] == 0.0


# ---------------------------------------------------------------- scale smoke


def test_several_thousand_closed_work_orders_still_return_quickly_and_correctly(db):
    seed = db.seed
    m = make_models(seed)
    symptom = seed.symptom("Generic fault", None)
    trigger, _ = open_trigger(seed, m["m1"], symptom_ids=(symptom,))
    seed.conn.execute("BEGIN")
    try:
        for i in range(3000):
            asset = seed.asset(m["m1"] if i % 3 == 0 else m["m2"])
            seed.closed_case(
                asset, f"Repair family {i % 30}", system="engine", result="fixed" if i % 4 else "not_fixed",
                days_ago=(i % 400) + 1, labor=1.0, symptoms=(symptom,),
            )
    finally:
        seed.conn.execute("COMMIT")
    result = run(db, trigger, top_k=10)
    assert len(result["candidates"]) == 10
    assert all(c["attempt_count"] >= 1 for c in result["candidates"])
    assert all(c["evidence"].startswith("Fixed ") for c in result["candidates"])
    tiers = [c["match_tier"] for c in result["candidates"]]
    assert tiers == sorted(tiers, key=["model", "family", "all"].index)
