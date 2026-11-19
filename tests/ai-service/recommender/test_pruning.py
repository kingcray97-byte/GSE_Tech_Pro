"""The upper-bound pruning in search._rank_tier must return exactly the top-N of a full scoring pass.

Random but seeded histories (fixed RNG seed, so the test is deterministic). The reference is the same
scoring with need = every group, so no pruning happens. Any divergence means the bound is wrong.
"""

from __future__ import annotations

import random

from recommender import search as S
from rag.db import connect
from support import NOW, make_models, open_trigger


def _random_history(seed, rng: random.Random):
    models = make_models(seed)
    symptoms = [seed.symptom(f"Sym {i}", rng.choice(["hydraulic", "engine", None])) for i in range(8)]
    trigger, _ = open_trigger(seed, models["m1"], symptom_ids=tuple(symptoms[:4]), dtc_codes=("SPN9",))
    assets = {k: [seed.asset(v) for _ in range(3)] for k, v in models.items()}
    for _ in range(900):
        key = rng.choice(["m1", "m1", "m2", "m3", "m4", "m5"])  # trigger's model gets a third of the history
        asset = rng.choice(assets[key])
        desc = f"Repair {rng.randrange(400)}"  # enough groups that the model tier spans several scoring batches
        result = rng.choice(["fixed", "fixed", "not_fixed", "unsure"])
        syms = tuple(rng.sample(symptoms, rng.randrange(1, 5)))
        seed.closed_case(
            asset, desc, system=rng.choice(["hydraulic", "engine", None]), result=result,
            days_ago=rng.choice([3, 20, 45, 200, 400]) + rng.random(), labor=rng.choice([None, 1.0, 2.5]),
            symptoms=syms,
        )
    return trigger


def test_pruned_top_n_equals_exhaustive_top_n_per_tier(db):
    trigger = _random_history(db.seed, random.Random(20261117))
    conn = connect(db.path, readonly=True, load_vec=False)
    try:
        conn.execute("BEGIN")
        number_expr = "NULL"
        trig = S._load_trigger(conn, trigger)
        query, _ = S._load_query(conn, trig, "")
        aggs = S._load_aggregates(conn, trig, number_expr)
        assert sum(1 for g in aggs.values() if g.tier == "model") > 128  # forces more than one scoring batch
        for tier in S.TIERS:
            tier_aggs = [g for g in aggs.values() if g.tier == tier]
            if not tier_aggs:
                continue
            exhaustive = S._rank_tier(conn, tier_aggs, len(tier_aggs), trig.id, number_expr, query, NOW)
            assert len(exhaustive) == len(tier_aggs)
            for need in (1, 3, 5, 12):
                pruned = S._rank_tier(conn, tier_aggs, need, trig.id, number_expr, query, NOW)
                want = [(g.key, g.rank) for g in exhaustive[:need]]
                got = [(g.key, g.rank) for g in pruned[:need]]
                assert got == want, f"tier={tier} need={need}"
    finally:
        conn.close()
