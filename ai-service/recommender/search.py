"""Repair-history recommender: 3-tier candidate search, similarity, Wilson ranking, evidence (ADR-010 §2-§5, §8).

Read-only on the shared SQLite database (C# GseTechPro.Data is the only writer). The model is never involved.
Entry point: run_repair_history_search(). Pure math lives in recommender/scoring.py.

Candidate = a repair group: rows of `repairs` sharing the ADR §8 key
`COALESCE(lower(trim(system)),'') || '|' || lower(trim(description))`. Counts are distinct work orders.
"""

from __future__ import annotations

import json
from collections import Counter, defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Sequence

from rag.db import connect
from rag.embedding import Embedder
from rag.rrf import RRF_K, rrf_merge
from recommender.scoring import (
    CandidateSignals,
    QuerySignals,
    age_days,
    cosine,
    format_utc,
    normalize_text,
    parse_timestamp,
    rank_score,
    recency_weight,
    render_evidence,
    similarity_composite,
    tokenize,
    wilson_lower_bound,
    wo_display_number,
)

MAX_TOP_K = 50
DEFAULT_TOP_K = 5
MAX_QUERY_TEXT = 2000
# Hybrid mode embeds at most this many candidates per tier (one embedder call). Must stay >= MAX_TOP_K.
VECTOR_POOL_SIZE = 100
TIERS = ("model", "family", "all")

_EMPTY: frozenset = frozenset()


class UnknownWorkOrderError(ValueError):
    """No live work_orders row with this id (the route maps this to 404)."""


@dataclass(frozen=True)
class _Trigger:
    id: str
    complaint_summary: str | None
    symptoms_summary: str | None
    model_id: str | None
    make: str | None  # lower(trim(equipment_models.make))
    category: str | None  # lower(trim(equipment_models.category))


@dataclass(frozen=True)
class _GroupAgg:
    key: str
    n: int
    s: int
    avg_labor_hours: float | None
    tier: str
    last: datetime | None  # most recent repairs.performed_at in the group (drives recency for rank + evidence)


@dataclass
class _Scored:
    key: str
    tier: str
    n: int
    s: int
    success_rate: float
    similarity: float
    recency: float
    rank: float
    age: int | None
    last_done: datetime | None
    rep: dict[str, Any]
    avg_labor_hours: float | None
    model_label: str
    source_ids: list[str]
    vector_text: str


# ---------------------------------------------------------------- validation


def _validate_work_order_id(value: Any) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError("work_order_id must be a non-empty string")
    return value.strip()


def _validate_top_k(value: Any) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not (1 <= value <= MAX_TOP_K):
        raise ValueError(f"top_k must be an integer between 1 and {MAX_TOP_K}")
    return value


def _validate_query_text(value: Any) -> str:
    if value is None:
        return ""
    if not isinstance(value, str):
        raise ValueError("query_text must be a string or null")
    if len(value) > MAX_QUERY_TEXT:
        raise ValueError(f"query_text must be at most {MAX_QUERY_TEXT} characters")
    return value


def _as_utc(moment: datetime) -> datetime:
    if moment.tzinfo is None:
        return moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(timezone.utc)


def _ts_key(moment: datetime | None) -> float:
    """Sort key: larger = more recent; unknown timestamps sort last."""
    return moment.timestamp() if moment is not None else float("-inf")


# ---------------------------------------------------------------- SQL


def _work_orders_has_number(conn) -> bool:
    """`work_orders.number` arrives in a later migration (ADR-010 §8); detect it instead of assuming."""
    return any(row["name"] == "number" for row in conn.execute("PRAGMA table_info(work_orders)").fetchall())


def _base_sql(number_expr: str) -> str:
    """Shared CTE: one row per (contributing repair, closed work order). Only fixed/not_fixed contribute."""
    return f"""
    WITH base AS (
      SELECT
        COALESCE(lower(trim(r.system)), '') || '|' || lower(trim(r.description)) AS repair_key,
        r.id AS repair_id,
        r.work_order_id AS wo_id,
        r.description AS description,
        r.system AS system,
        r.labor_hours AS labor_hours,
        r.performed_at AS performed_at,
        o.result AS result,
        CASE WHEN r.work_order_id IN (
               SELECT c.original_work_order_id FROM comebacks AS c WHERE c.confirmed = 1
             ) THEN 0 ELSE 1 END AS comeback_free,
        w.complaint_summary AS complaint_summary,
        w.symptoms_summary AS symptoms_summary,
        w.repair_summary AS repair_summary,
        {number_expr} AS wo_number,
        a.model_id AS model_id,
        em.make AS make,
        em.model AS model,
        lower(trim(em.make)) AS make_norm,
        lower(trim(em.category)) AS category_norm
      FROM repairs AS r
      JOIN outcomes AS o ON o.work_order_id = r.work_order_id
      JOIN work_orders AS w ON w.id = r.work_order_id
      JOIN assets AS a ON a.id = w.asset_id
      LEFT JOIN equipment_models AS em ON em.id = a.model_id
      WHERE r.deleted_at IS NULL
        AND w.deleted_at IS NULL
        AND o.result IN ('fixed', 'not_fixed')
        AND r.work_order_id <> :trigger_id
    )
    """


def _load_trigger(conn, work_order_id: str) -> _Trigger | None:
    row = conn.execute(
        """
        SELECT w.id AS id, w.complaint_summary AS complaint_summary,
               w.symptoms_summary AS symptoms_summary, a.model_id AS model_id,
               lower(trim(em.make)) AS make, lower(trim(em.category)) AS category
        FROM work_orders AS w
        JOIN assets AS a ON a.id = w.asset_id
        LEFT JOIN equipment_models AS em ON em.id = a.model_id
        WHERE w.id = ? AND w.deleted_at IS NULL
        """,
        (work_order_id,),
    ).fetchone()
    if row is None:
        return None
    return _Trigger(
        id=row["id"],
        complaint_summary=row["complaint_summary"],
        symptoms_summary=row["symptoms_summary"],
        model_id=row["model_id"],
        make=row["make"],
        category=row["category"],
    )


def _load_query(conn, trigger: _Trigger, query_text: str) -> tuple[QuerySignals, str]:
    """Build the query side (ADR-010 §3) from the trigger work order. Returns signals and the raw blob."""
    wo = trigger.id
    complaints = [
        r["text"]
        for r in conn.execute(
            "SELECT text FROM complaints WHERE work_order_id = ? ORDER BY recorded_at, id", (wo,)
        ).fetchall()
    ]
    symptoms = frozenset(
        r["symptom_id"]
        for r in conn.execute(
            "SELECT symptom_id FROM work_order_symptoms WHERE work_order_id = ?", (wo,)
        ).fetchall()
    )
    dtcs = frozenset(
        (normalize_text(r["code"]), normalize_text(r["protocol"]))
        for r in conn.execute(
            "SELECT code, protocol FROM dtcs WHERE work_order_id = ? AND deleted_at IS NULL", (wo,)
        ).fetchall()
    )
    system_values = [
        r[0]
        for r in conn.execute(
            """
            SELECT s.system FROM work_order_symptoms AS wos
            JOIN symptoms AS s ON s.id = wos.symptom_id WHERE wos.work_order_id = ?
            UNION ALL
            SELECT system FROM readings WHERE work_order_id = ? AND deleted_at IS NULL
            UNION ALL
            SELECT system FROM repairs WHERE work_order_id = ? AND deleted_at IS NULL
            """,
            (wo, wo, wo),
        ).fetchall()
    ]
    systems = frozenset(s for s in (normalize_text(v) for v in system_values) if s)
    blob = " ".join(
        part for part in [*complaints, trigger.complaint_summary, trigger.symptoms_summary, query_text] if part
    )
    return QuerySignals(symptoms, dtcs, systems, tokenize(blob)), blob


def _load_aggregates(conn, trigger: _Trigger, number_expr: str) -> dict[str, _GroupAgg]:
    """ADR-010 §8 aggregate, plus per-group tier flags (model, then family) resolved against the trigger."""
    sql = _base_sql(number_expr) + """
    SELECT repair_key,
           COUNT(DISTINCT CASE WHEN result IN ('fixed', 'not_fixed') THEN wo_id END) AS n,
           COUNT(DISTINCT CASE WHEN result = 'fixed' AND comeback_free = 1 THEN wo_id END) AS s,
           AVG(labor_hours) AS avg_labor_hours,
           MAX(performed_at) AS last_performed_at,
           MAX(CASE WHEN :model_id IS NOT NULL AND model_id = :model_id THEN 1 ELSE 0 END) AS in_model,
           MAX(CASE WHEN :category IS NOT NULL AND make_norm = :make AND category_norm = :category
                    THEN 1 ELSE 0 END) AS in_family
    FROM base
    GROUP BY repair_key
    """
    params = {"trigger_id": trigger.id, "model_id": trigger.model_id, "make": trigger.make, "category": trigger.category}
    out: dict[str, _GroupAgg] = {}
    for r in conn.execute(sql, params).fetchall():
        if r["in_model"]:
            tier = "model"
        elif r["in_family"]:
            tier = "family"
        else:
            tier = "all"
        n = int(r["n"])
        if n == 0:  # cannot happen (base only holds fixed/not_fixed), kept as the ADR rule
            continue
        out[r["repair_key"]] = _GroupAgg(
            key=r["repair_key"],
            n=n,
            s=int(r["s"]),
            avg_labor_hours=float(r["avg_labor_hours"]) if r["avg_labor_hours"] is not None else None,
            tier=tier,
            # performed_at is written in one UTC format by the data layer, so MAX() orders it correctly.
            last=parse_timestamp(r["last_performed_at"]),
        )
    return out


def _load_rows(conn, keys: Sequence[str], trigger_id: str, number_expr: str) -> dict[str, list[dict[str, Any]]]:
    """Every contributing row for the given group keys (all tiers: a group's whole history is shown)."""
    if not keys:
        return {}
    sql = _base_sql(number_expr) + " SELECT * FROM base WHERE repair_key IN (SELECT value FROM json_each(:keys))"
    rows = conn.execute(sql, {"trigger_id": trigger_id, "keys": json.dumps(list(keys))}).fetchall()
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for r in rows:
        grouped[r["repair_key"]].append(dict(r))
    return grouped


def _load_wo_features(conn, wo_ids: Sequence[str]):
    """Symptom ids and DTC identities per contributing work order (one query each, JSON key set)."""
    payload = json.dumps(sorted(set(wo_ids)))
    symptoms: dict[str, set[str]] = defaultdict(set)
    for r in conn.execute(
        "SELECT work_order_id, symptom_id FROM work_order_symptoms "
        "WHERE work_order_id IN (SELECT value FROM json_each(?))",
        (payload,),
    ).fetchall():
        symptoms[r["work_order_id"]].add(r["symptom_id"])
    dtcs: dict[str, set[tuple[str, str]]] = defaultdict(set)
    for r in conn.execute(
        "SELECT work_order_id, code, protocol FROM dtcs WHERE deleted_at IS NULL "
        "AND work_order_id IN (SELECT value FROM json_each(?))",
        (payload,),
    ).fetchall():
        dtcs[r["work_order_id"]].add((normalize_text(r["code"]), normalize_text(r["protocol"])))
    return symptoms, dtcs


# ---------------------------------------------------------------- scoring one candidate group


def _model_label(pairs: list[tuple[dict[str, Any], datetime | None]]) -> str:
    """ADR-010 §5: the model with the most contributing repairs; tie → most recent; none → 'unknown model'."""
    counts: Counter[str] = Counter()
    latest: dict[str, float] = {}
    names: dict[str, str] = {}
    for row, ts in pairs:
        model_id = row["model_id"]
        if model_id is None:
            continue
        counts[model_id] += 1
        latest[model_id] = max(latest.get(model_id, float("-inf")), _ts_key(ts))
        names[model_id] = " ".join(p.strip() for p in (row["make"], row["model"]) if p and p.strip())
    if not counts:
        return "unknown model"
    best = max(counts, key=lambda m: (counts[m], latest[m], m))
    return names[best] or "unknown model"


def _score_group(
    agg: _GroupAgg,
    rows: list[dict[str, Any]],
    query: QuerySignals,
    wo_symptoms: dict[str, set[str]],
    wo_dtcs: dict[str, set[tuple[str, str]]],
    now: datetime,
) -> _Scored:
    stamped = [(row, parse_timestamp(row["performed_at"])) for row in rows]
    # Most recent first; ties by repair id so the representative row is deterministic.
    stamped.sort(key=lambda pair: (-_ts_key(pair[1]), pair[0]["repair_id"]))

    best_sim = -1.0
    best_text = ""
    for row, _ in stamped:  # most recent first, so ties keep the most recent case
        text = " ".join(
            part
            for part in (row["complaint_summary"], row["symptoms_summary"], row["repair_summary"], row["description"])
            if part
        )
        cand = CandidateSignals(
            symptoms=frozenset(wo_symptoms.get(row["wo_id"], _EMPTY)),
            dtcs=frozenset(wo_dtcs.get(row["wo_id"], _EMPTY)),
            system=normalize_text(row["system"]) or None,
            tokens=tokenize(text),
        )
        sim = similarity_composite(query, cand)
        if sim > best_sim:
            best_sim, best_text = sim, text

    rep = stamped[0][0]
    last = agg.last  # same value the rank upper bound used, so pruning stays exact
    age = age_days(last, now) if last is not None else None
    success_rate = wilson_lower_bound(agg.s, agg.n)
    similarity = max(best_sim, 0.0)
    recency = recency_weight(age)

    latest_per_wo: dict[str, datetime | None] = {}
    for row, ts in stamped:
        prev = latest_per_wo.get(row["wo_id"])
        if row["wo_id"] not in latest_per_wo or _ts_key(ts) > _ts_key(prev):
            latest_per_wo[row["wo_id"]] = ts
    source_ids = sorted(latest_per_wo, key=lambda w: (-_ts_key(latest_per_wo[w]), w))

    avg = round(agg.avg_labor_hours, 1) if agg.avg_labor_hours is not None else None
    return _Scored(
        key=agg.key,
        tier=agg.tier,
        n=agg.n,
        s=agg.s,
        success_rate=success_rate,
        similarity=similarity,
        recency=recency,
        rank=rank_score(success_rate, similarity, recency),
        age=age,
        last_done=last,
        rep=rep,
        avg_labor_hours=avg,
        model_label=_model_label(stamped),
        source_ids=source_ids,
        vector_text=best_text,
    )


# ---------------------------------------------------------------- optional vector path (§3)


def _hybrid_order(
    ranked: list[_Scored], query_blob: str, embedder: Embedder
) -> tuple[list[_Scored], bool]:
    """RRF-merge the deterministic rank order with a cosine rank order over the top pool of one tier.

    List A: rank (deterministic). List B: the same rank formula with cosine in place of similarity.
    SimilarityScore is never touched; only the order changes. Returns (ordered, used_vectors).
    """
    pool = ranked[:VECTOR_POOL_SIZE]
    texts = [query_blob, *(g.vector_text for g in pool)]
    vectors = embedder(texts)
    if not vectors or len(vectors) != len(texts) or any(v is None for v in vectors):
        return ranked, False
    query_vec = vectors[0]
    by_key = {g.key: g for g in pool}
    vector_rank: dict[str, float] = {}
    for g, vec in zip(pool, vectors[1:], strict=True):
        cos = max(0.0, min(1.0, cosine(query_vec, vec)))
        vector_rank[g.key] = rank_score(g.success_rate, cos, g.recency)
    list_a = [g.key for g in pool]  # already sorted by deterministic rank
    list_b = sorted(list_a, key=lambda k: (-vector_rank[k], k))
    fused = rrf_merge([list_a, list_b], k=RRF_K)
    ordered = [by_key[k] for k, _ in fused] + ranked[VECTOR_POOL_SIZE:]
    return ordered, True


# ---------------------------------------------------------------- candidate output


def _to_candidate(g: _Scored) -> dict[str, Any]:
    rep = g.rep
    wo_label = wo_display_number(rep.get("wo_number"), rep["wo_id"])
    evidence = render_evidence(
        success_count=g.s,
        attempt_count=g.n,
        model_label=g.model_label,
        average_labor_hours=g.avg_labor_hours,
        last_done_days=g.age,
        wo_number=wo_label,
    )
    return {
        "candidate_repair_id": rep["repair_id"],
        "repair_description": rep["description"],
        "system": rep["system"],
        "match_tier": g.tier,
        "similarity_score": round(g.similarity, 4),
        "success_rate": round(g.success_rate, 4),
        "success_count": g.s,
        "attempt_count": g.n,
        "average_labor_hours": g.avg_labor_hours,
        "last_done_at": format_utc(g.last_done) if g.last_done is not None else None,
        "source_work_order_ids": list(g.source_ids),
        "evidence": evidence,
    }


# ---------------------------------------------------------------- entry point


def _run(
    conn,
    work_order_id: str,
    query_text: str,
    top_k: int,
    query_embedder: Embedder | None,
    now: datetime,
) -> dict[str, Any]:
    trigger = _load_trigger(conn, work_order_id)
    if trigger is None:
        raise UnknownWorkOrderError(f"work order not found: {work_order_id}")
    has_number = _work_orders_has_number(conn)
    number_expr = "w.number" if has_number else "NULL"

    query, query_blob = _load_query(conn, trigger, query_text)
    aggs = _load_aggregates(conn, trigger, number_expr)

    results: list[dict[str, Any]] = []
    fallback = True
    remaining = top_k
    for tier in TIERS:
        if remaining <= 0:
            break
        tier_aggs = [g for g in aggs.values() if g.tier == tier]
        if not tier_aggs:
            continue
        # Hybrid needs the whole top pool to reorder; deterministic only needs what will be shown.
        need = min(len(tier_aggs), max(remaining, VECTOR_POOL_SIZE) if query_embedder is not None else remaining)
        scored = _rank_tier(conn, tier_aggs, need, trigger.id, number_expr, query, now)
        if query_embedder is not None and scored and query_blob.strip():
            scored, used = _hybrid_order(scored, query_blob, query_embedder)
            if used:
                fallback = False
        for g in scored[:remaining]:
            results.append(_to_candidate(g))
        remaining = top_k - len(results)

    return {"candidates": results, "fallback": fallback}


def _rank_tier(
    conn,
    tier_aggs: list[_GroupAgg],
    need: int,
    trigger_id: str,
    number_expr: str,
    query: QuerySignals,
    now: datetime,
) -> list[_Scored]:
    """Exact top-`need` groups of one tier, scoring only what can still reach the top.

    rank = success_rate + 0.10*sim + 0.05*recency, with sim <= 1. Success rate and recency come from the cheap
    aggregate, so each group has an exact upper bound. Groups are scored in upper-bound order, in growing batches,
    and scoring stops once the need-th exact rank is strictly above every unscored bound. The result is the same
    top-`need` the full computation would give (strict '>' keeps ties exact).
    """
    if need <= 0:
        return []

    def upper_bound(g: _GroupAgg) -> float:
        age = age_days(g.last, now) if g.last is not None else None
        return rank_score(wilson_lower_bound(g.s, g.n), 1.0, recency_weight(age))

    order = sorted(tier_aggs, key=lambda g: (-upper_bound(g), g.key))
    bounds = {g.key: upper_bound(g) for g in order}
    scored: dict[str, _Scored] = {}
    pos = 0
    # Each batch re-reads the closed-repair base once, so start big enough that a typical search needs one pass.
    batch = max(need * 8, 128)
    while True:
        chunk = order[pos:pos + batch]
        pos += len(chunk)
        if chunk:
            keys = [g.key for g in chunk]
            rows_by_key = _load_rows(conn, keys, trigger_id, number_expr)
            wo_ids = {row["wo_id"] for rows in rows_by_key.values() for row in rows}
            wo_symptoms, wo_dtcs = _load_wo_features(conn, list(wo_ids))
            for g in chunk:
                rows = rows_by_key.get(g.key)
                if rows:
                    scored[g.key] = _score_group(g, rows, query, wo_symptoms, wo_dtcs, now)
        ranked = sorted(scored.values(), key=lambda s: (-s.rank, s.key))
        if pos >= len(order):
            return ranked
        if len(ranked) >= need and ranked[need - 1].rank > bounds[order[pos].key]:
            return ranked[:need] if len(ranked) > need else ranked
        batch *= 4


def run_repair_history_search(
    db_path,
    work_order_id: str,
    query_text: str | None = None,
    top_k: int = DEFAULT_TOP_K,
    *,
    query_embedder: Embedder | None = None,
    now: datetime | None = None,
) -> dict:
    """Find similar past repairs for a work order (ADR-010 §2-§5, §8). Read-only.

    Returns {"candidates": [...], "fallback": bool}; candidates are already ordered (index 0 = best).
    Raises ValueError for invalid input and UnknownWorkOrderError (a ValueError) when the work order is not live.
    `query_embedder` is optional; without it (or when it returns None) ranking is deterministic and fallback=True.
    `now` is injectable for deterministic recency; default is the current UTC time.
    """
    wo_id = _validate_work_order_id(work_order_id)
    k = _validate_top_k(top_k)
    text = _validate_query_text(query_text)
    when = _as_utc(now) if now is not None else datetime.now(timezone.utc)
    conn = connect(db_path, readonly=True, load_vec=False)
    try:
        conn.execute("BEGIN")  # one read snapshot for every query in this search
        return _run(conn, wo_id, text, k, query_embedder, when)
    finally:
        conn.close()
