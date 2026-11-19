# ADR-010 — Repair-history learning & recommendation engine: ownership, similarity, smoothing, comebacks, wire contract

- **Status:** Accepted
- **Date:** 2026-11-17
- **Owner:** architect

## Context

Phase 5 closes the learning loop: past outcomes feed a self-learning recommendation engine that,
on diagnosis start, surfaces similar past failures and the repairs that fixed them, with a
"Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, last done 3 weeks ago, WO #4471" style evidence string
(ARCHITECTURE.md §4.3). Four owners build in parallel against one contract — `recommender`
(engine, read-only on the DB), `database` (write repositories + comeback detection + the read-side
aggregate query), `ai-engineer` (HTTP route + chat tools), and `ui-winui` (recommendations panel).

Two prior decisions shape this and are **not** relitigated:

1. **Single-writer per table (ADR-008 §"DB sharing").** Python is the sole writer of the RAG tables;
   C# (`GseTechPro.Data`) is the sole writer of **all other tables**. That makes the app the single
   writer of `outcomes`, `comebacks`, `recommendations`, `diagnoses`, `repairs`, and
   `parts_replaced`, exactly as ADR-009 did for `readings`.
2. **Deterministic code does the exact work; the model only explains** (ARCHITECTURE.md §4.1). The
   matching/ranking math below is deterministic and testable. The `lfm3-omni` model is used only in
   the chat tool `find_similar_repairs` to *explain* the top candidates, never to compute a rate.

The schema for `work_orders`, `work_order_symptoms`, `symptoms`, `complaints`, `readings`, `dtcs`,
`repairs`, `outcomes`, `comebacks`, and `recommendations` already exists (ARCHITECTURE.md §2.2) and
is **not** redesigned here. Two gaps remain, which this ADR closes (documented as "data needs" in
§8, authored by `database` — not by me):

1. **No human-readable work-order number.** The evidence string and `Citation.WorkOrderNumber`
   reference "WO #4471", but `work_orders` has only a UUID `id`. A `number` column is required.
2. **No closed-WO index.** The engine filters to closed WOs and sorts by recency; `work_orders` has
   `idx_wo_opened` but no index on `closed_at`.

## Decision

### 1. Ownership boundary

- **`recommender` (Python, `ai-service/recommender/`) — read-only on the DB**, exactly like RAG
  search. Owns: the 3-tier candidate search (§2), the similarity signal (§3), the smoothing (§4),
  the final ranking, and evidence-string rendering (§5). Opens the shared SQLite in WAL, read-only,
  and never writes.
- **`database` (C#, `GseTechPro.Data`) — writes + detection + reference SQL.** Owns: the write
  repositories for `outcomes` / `comebacks` / `recommendations`; **comeback detection** (§6); the
  **authoritative read-side aggregate query** (the SQL in §8 that assembles candidate counts) that
  both the C# repositories and the Python recommender agree on. Owns migration `005` for the data
  needs in §8.
- **app (C#, `ui-winui` + data layer) — writes outcomes/comebacks/recommendations + renders.**
  Detects (via the data layer) and asks the tech to **confirm** comebacks when closing a WO; writes
  `comebacks` rows; calls `/search/repair-history` and renders the recommendations panel; writes
  `recommendations` rows (memoized audit) when a candidate is presented/accepted.
- **`ai-engineer` (Python, `ai-service/app` + `tools/`) — route + chat tools.** Implements
  `POST /search/repair-history` (§7) and the chat tool `find_similar_repairs` — both call the same
  `recommender` code; the tool returns candidates and lets the model explain them with citations.
  Mirrors the DTOs in `models.py`.

### 2. Three-tier search (model → family → all)

Search history in three widening tiers, with the tier recorded on each candidate (`MatchTier`):

1. **model** — the candidate's work order is on an asset whose `assets.model_id` equals the query
   asset's `assets.model_id`.
2. **family** — same `equipment_models.make` **and** same `equipment_models.category` (resolved
   `assets.model_id → equipment_models`), but a different `model_id`.
3. **all** — every other closed work order.

"Model" and "family" are defined strictly via `assets.model_id → equipment_models.make` +
`equipment_models.category`; no other attribute participates in tiering. **Tiers are assigned per
candidate group, not per WO:** a group is `model` if any of its contributing WOs is on the trigger
asset's `model_id`; `family` if any contributing WO shares make+category (and none is on the same
model); else `all` (§Implementation notes). Fill up to `top_k` in order: tier 1 first, then tier 2,
then tier 3; a tier is a **hard precedence** (a model-tier candidate always outranks a family-tier
candidate regardless of its score). Within a tier, order by the rank score in §4. Only **closed**
WOs (an `outcomes` row exists) contribute; WOs with a `deleted_at` on `work_orders` or `repairs` are
excluded.

### 3. Similarity signal (v1, deterministic-first)

The query is built from the trigger WO (`request.work_order_id`): its `work_order_symptoms.symptom_id`
list, its `dtcs` (code + protocol), its `readings.system` / `symptoms.system` values, and its
`complaints.text` + `work_orders.complaint_summary` + `work_orders.symptoms_summary`, plus
`request.query_text` (appended). Candidate signals are the candidate WO's matching fields plus its
`repairs.description` and `work_orders.repair_summary`.

Four per-signal similarities, each in [0,1]:

- **symptom_sim** = `|query symptoms ∩ candidate symptoms| / max(1, |query symptoms|)` over
  `work_order_symptoms.symptom_id`.
- **dtc_sim** = `|query DTCs ∩ candidate DTCs| / max(1, |query DTCs|)`, where a DTC's identity is
  `(lower(trim(code)), lower(trim(protocol)))` — case-folded **and trimmed** on both fields.
- **system_sim** = `1.0` if the candidate's `repairs.system` is non-null and equals **any** system
  value present on the query WO (its `symptoms.system`, `readings.system`, or `repairs.system`);
  otherwise `0.0`. (A system match is "shared `repairs.system` / `readings.system`" from the spec,
  judged against the query's system set.)
- **text_sim** = **token-overlap Jaccard** between the query text blob and the candidate text blob,
  where:
  - query blob = `complaints.text` + `complaint_summary` + `symptoms_summary` + `query_text`;
  - candidate blob = `complaint_summary` + `symptoms_summary` + `repair_summary` + `repairs.description`
    (`complaints.text` is query-side only and is **not** included in the candidate blob);
  - tokenization = Unicode lowercase, split on non-alphanumeric, drop a small fixed English stopword
    list, keep unigrams. **No stemming.**

Deterministic composite (the `SimilarityScore` when there is no embedder):

```
similarity = 0.45·symptom_sim + 0.25·dtc_sim + 0.20·system_sim + 0.10·text_sim
```

When a signal is undefined for the query (e.g. no DTCs), that term is dropped and the remaining
weights are renormalized to sum to 1. **Why Jaccard over BM25:** BM25 needs an inverted index (an
FTS5 virtual table) to be meaningful — a schema addition. On one tablet's history (hundreds to low
thousands of closed WOs, all in memory) Jaccard is O(tokens) per candidate, needs no index, and the
structured signals (symptoms/DTCs/system) already carry the semantic weight. Rejected alternative
recorded in §Alternatives.

**Optional vector path.** When the `ToolContext.embedder` probe (the same probe RAG uses) reports
the embedding model present, also embed the query blob and each candidate blob with `nomic-embed-text`
and compute cosine similarity. Merge into one ordering with **Reciprocal Rank Fusion (RRF, k=60)**
over two rank lists computed **per tier over a top pool of 100**: list A = the §4 rank formula using
the deterministic `similarity`; list B = the **same** §4 rank formula with cosine similarity in place
of `similarity`. This keeps `success_rate` dominant on both lists — the vector signal only re-orders
the pool, it never replaces the rate. When the embedder is absent, skip it and set the response
`fallback:true`. `SimilarityScore` is always the deterministic composite — the vector path only
changes *which* candidates surface and their *order*. This mirrors
`SearchManualsResponse.Fallback` (ADR-008).

### 4. Smoothing + ranking

**Primary: Wilson score interval lower bound, 95% confidence (z = 1.96).**

```
n = 0  →  success_rate = 0
n > 0  →  p = s / n
          success_rate = ( p + z²/(2n) − z·√((p·(1−p) + z²/(4n)) / n) ) / (1 + z²/n)
```

**Counts (explicit):** `n` = number of contributing WOs with a **non-`unsure`** outcome
(`outcomes.result IN ('fixed','not_fixed')`); `s` = number of those with `result='fixed'` **and no
confirmed comeback** against that WO (§6). `unsure` is excluded from both `n` and `s` (no evidence).
`not_fixed` counts toward `n` but not `s`. A **confirmed** comeback demotes a previously-`fixed`
outcome: it still counts toward `n`, not `s`. Candidates with `n = 0` are **dropped** (no rate →
not rankable, never shown as a "0%" rate).

**Worked example (all numbers illustrative, not real data):**

| candidate | n | s | Wilson lower bound (95%) | Laplace (s+1)/(n+2) |
|---|---|---|---|---|
| 1-for-1 | 1 | 1 | `(1 + 3.8416/2 − 1.96·√((0 + 3.8416/4)/1)) / (1 + 3.8416)` = **0.207** | 0.667 |
| 45-for-50 | 50 | 45 | `(0.9 + 3.8416/100 − 1.96·√((0.09 + 3.8416/200)/50)) / (1 + 3.8416/50)` = **0.786** | 0.885 |

Both formulas keep 45-for-50 above 1-for-1. Wilson is primary because it penalizes tiny samples
far more (0.207 vs 0.667), which is exactly the "1-for-1 must not outrank 45-for-50" requirement,
and it has a principled confidence-interval meaning. Laplace is the documented fallback if a future
owner wants a simpler smoother — but the default must stay Wilson.

**Final rank (within a tier):**

```
rank_score = success_rate + 0.10·similarity + 0.05·recency
```

For a candidate **group**, `similarity` is the **max** of the deterministic composite over the
group's contributing WO rows (a single close past case surfaces the repair; Wilson already penalizes
thin evidence — §Implementation notes). `recency` is a step function of the candidate's most recent
`repairs.performed_at` (age in whole days): ≤30 → 1.0; ≤90 → 0.75; ≤365 → 0.5; else → 0.25.
`success_rate` is the dominant term; the similarity and recency terms are small tiebreakers that
**cannot** let a low-sample candidate overtake a high-sample one (worst-case gap:
0.786+0.00+0.00 = 0.786 vs 0.207+0.10+0.05 = 0.357).

### 5. Evidence string format

Exact template, rendered by the recommender into `RepairCandidate.Evidence`:

```
Fixed {s} of {n} on {model_label}, avg {labor} hrs, last done {recency}, WO #{wo_number}
```

Field mapping (all from recorded data — never invented):

| fragment | source |
|---|---|
| `{s}` / `{n}` | raw `SuccessCount` / `AttemptCount` (§4 counts; the **raw** ratio, not the smoothed rate) |
| `{model_label}` | `equipment_models.make + " " + equipment_models.model` of the candidate's group; if the group spans models, use the model with the most contributing repairs (tie-break: most recent) |
| `{labor}` | `AverageLaborHours` rounded to **1 decimal** (invariant culture); the whole "avg … hrs" clause is **omitted** when no labor hours were recorded |
| `{recency}` | from `LastDoneAt` vs now (UTC), whole-day floor: 0 → "today"; 1 → "yesterday"; 2–6 → "N days ago"; 7–55 → "N weeks ago" (`floor(days/7)`); ≥56 → "N months ago" (`floor(days/30)`); singular when N=1 ("1 week ago", "1 month ago"), plural otherwise |
| `{wo_number}` | `work_orders.number` (§8) of the most recent contributing WO |

The "last done {recency}" clause is **omitted** when `last_done_at` is unknown, just as the
"avg … hrs" clause is omitted when labor is unknown. On the wire, `average_labor_hours` is rounded to
**1 decimal** (invariant culture), matching the evidence string.

Canonical example (illustrative — 18/20, "MA-50", 2.1 hrs, and #4471 are placeholders, not real
data):

> `Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, last done 3 weeks ago, WO #4471`

The evidence shows raw counts; the smoothed `success_rate` is carried separately on the wire and may
be shown alongside it by the UI.

### 6. Comeback detection (C#, `database`; single-writer)

- **When:** the app detects candidate comebacks **when closing a work order** (before/while writing
  `outcomes`). The data layer scans prior **closed** WOs for the **same asset**.
- **"Same"** = same `work_orders.asset_id` **and** (at least one shared `repairs.system` value, or at
  least one shared `work_order_symptoms.symptom_id`, or at least one shared DTC `(code, protocol)`).
- **Window (concrete defaults, configurable in `app_settings`):** the earlier WO qualifies if its
  `closed_at` is within **14 days** **and** the asset accrued **≤ 200 hours** since that WO closed
  (hours from `assets.hour_meter` / `work_orders.hours_at_open`). When the hour meter is unavailable,
  use the 14-day bound alone; when only hours are known, use 200 hours alone. "Whichever is sooner"
  = the return happened before **either** bound was exceeded (both must hold when both are known).
  Config keys: `comeback.window_days` (default 14), `comeback.window_hours` (default 200).
- **Record:** a detected comeback inserts a `comebacks` row (`auto=1`, `confirmed=0`,
  `window_days`/`window_hours` populated, `match_reason` describing the shared signal), and the app
  prompts the tech to **confirm**. On confirm, the data layer sets `confirmed=1` **and** mirrors it
  to `outcomes.comeback_detected=1` on the original WO.
- **Effect on smoothing:** **only confirmed** comebacks (`comebacks.confirmed = 1`) demote the
  earlier repair to a failure (§4). Auto-detected-but-unconfirmed comebacks never change the rate
  (they avoid penalizing repairs on a false-positive auto-match), but the app may still show them as
  a "possible comeback" prompt.

### 7. Wire contract — `POST /search/repair-history`

**Additive** — `AiServiceProtocol.Version` stays **1** (no bump: the endpoint is additive and the
existing endpoints are unchanged). Requires `X-GSE-Token` like every endpoint; errors use the
ADR-008 envelope.

Request (`RepairHistorySearchRequest`):

```json
{ "work_order_id": "<uuid>", "query_text": null, "top_k": 5 }
```

- `work_order_id` — **required**, UUID of an existing work order. Blank → `422 validation_error`;
  unknown WO → `404 not_found`.
- `query_text` — optional, `null`/absent = none; max 2000 chars.
- `top_k` — default 5, min 1, max 50.

Response (`RepairHistorySearchResponse`):

```json
{
  "candidates": [
    {
      "candidate_repair_id": "<uuid>",
      "repair_description": "Replace main hydraulic pump",
      "system": "hydraulic",
      "match_tier": "model",
      "similarity_score": 0.83,
      "success_rate": 0.786,
      "success_count": 45,
      "attempt_count": 50,
      "average_labor_hours": 2.1,
      "last_done_at": "2026-10-27T12:00:00.0000000Z",
      "source_work_order_ids": ["<uuid>", "<uuid>"],
      "evidence": "Fixed 45 of 50 on TUG 660-28, avg 2.1 hrs, last done 3 weeks ago, WO #4471"
    }
  ],
  "fallback": false
}
```

Field notes (authoritative for both `ui-winui` and `ai-engineer`): `match_tier` ∈ `model` | `family`
| `all`; `similarity_score` ∈ [0,1]; `success_rate` ∈ [0,1] (smoothed); `success_count`/`attempt_count`
are raw ints; `average_labor_hours` and `last_done_at` are `null` when unknown; `source_work_order_ids`
lists the contributing WO UUIDs; `candidates` is **already ordered** (index 0 = best) — **the app
must not re-sort**. `fallback:true` when ranking was deterministic-only (no embedder), `false` for
hybrid. Python mirrors these exactly (snake_case) in `ai-service/app/models.py`.

### 8. Data needs (authored by `database`; do NOT write here)

Migration `005`, additive and non-destructive:

1. **`work_orders.number`** — human-readable WO number, required for evidence + citations:
   ```sql
   ALTER TABLE work_orders ADD COLUMN number TEXT;
   CREATE UNIQUE INDEX idx_wo_number ON work_orders(number) WHERE number IS NOT NULL;
   ```
   The app (C# single-writer) generates it. Until the column exists, evidence falls back to
   `WO #<first 8 hex of id>` — temporary and marked in code.
2. **`idx_wo_closed`** on `work_orders.closed_at` (closed-WO filter + recency sort):
   ```sql
   CREATE INDEX idx_wo_closed ON work_orders(closed_at);
   ```
3. (Optional, small data) `CREATE INDEX idx_outcomes_result ON outcomes(result);` — only if profiling
   warrants it.

**Reference aggregate (normative shape; `database` owns the exact SQL, Python runs the same
read-only SELECT):** the candidate grouping key is `COALESCE(lower(trim(repairs.system)), '') || '|' ||
lower(trim(repairs.description))` (case-fold + trim only, no punctuation stripping, so SQL
`GROUP BY` and Python match). `COALESCE(…, '')` keeps the key non-NULL so rows with a NULL `system`
do not all collapse into one group (§Implementation notes). Core counts — `n` and `s` count
**distinct work orders**, not repair rows:

```sql
SELECT
  COALESCE(lower(trim(r.system)), '') || '|' || lower(trim(r.description)) AS repair_key,
  COUNT(DISTINCT r.work_order_id) FILTER (WHERE o.result = 'fixed'
       AND NOT EXISTS (SELECT 1 FROM comebacks c
                       WHERE c.original_work_order_id = r.work_order_id AND c.confirmed = 1)) AS s,
  COUNT(DISTINCT r.work_order_id) FILTER (WHERE o.result IN ('fixed','not_fixed')) AS n
FROM repairs r
JOIN outcomes o ON o.work_order_id = r.work_order_id
WHERE r.deleted_at IS NULL
GROUP BY COALESCE(lower(trim(r.system)), ''), lower(trim(r.description));
```

The normative shape is "distinct WOs; a confirmed comeback demotes a `fixed` outcome to a failure";
`database` owns the exact syntax (e.g. a subquery `COUNT(DISTINCT …)` + condition if a FILTER clause
isn't available on its SQLite build).

No denormalization of `system` onto `work_order_symptoms` is needed: a symptom's system resolves via
`symptoms.system`, and `repairs.system`/`readings.system` already exist (considered, rejected in
§Alternatives).

### Safety / citation rules (mirror ARCHITECTURE.md §4.5)

1. Every candidate carries `SourceWorkOrderIds` and cites its WO number in `Evidence`; the model,
   when explaining, cites the WO number — never a bare "history says".
2. Success rates, counts, and labor hours come only from recorded `outcomes`/`repairs` — **never
   invented**; a candidate with `n = 0` is dropped, not shown as "0%".
3. Recommendations are labeled suggestions; the tech makes the final call (existing §4.5 #3).
4. The engine never contradicts or skips manufacturer procedures or safety warnings.

## Implementation notes (ratified from recommender)

These are decisions `recommender` made while implementing §2–§8 where this ADR was silent. They are
**ratified as normative** and recorded here so the reference SQL and the engine stay in lockstep.
Where one refines an earlier section, that section has been patched to match.

1. **Group similarity = max.** A candidate group's `similarity` (used in the §4 rank formula) is the
   **max** of the deterministic composite over the group's contributing WO rows. Rationale: a single
   close past case should surface the repair; Wilson smoothing already penalizes the thin evidence
   that would otherwise make the group unrankable. (§4 patched.)
2. **`n` and `s` count distinct work orders**, not repair rows — one WO contributes at most once to a
   group's counts, even if it logged multiple identical `repairs` rows. (§8 SQL patched to
   `COUNT(DISTINCT …)`.)
3. **NULL `system` → `''` in the group key** via `COALESCE`, so
   `lower(trim(system)) || '|' || lower(trim(description))` is never NULL — a literal NULL would
   collapse all NULL-system rows into one bucket. (§8 patched.)
4. **Hybrid order (refinement of §3).** The vector path RRF-merges (k=60) two rank lists within each
   tier's top pool (100): list A = the §4 rank formula with deterministic `similarity`; list B = the
   **same** §4 rank formula with cosine similarity in place of `similarity`. `success_rate` stays
   dominant on both lists; `SimilarityScore` stays the deterministic composite. (§3 patched.)
5. **Pruning.** Scoring stops once the top-N is provably settled via an exact upper bound; the output
   is identical to exhaustive ranking. A pure CPU-savings optimization — no ranking change.
6. **Tiers are per group.** A group is `model` if any of its WOs is on the trigger's model; `family`
   if any shares make+category; else `all`. Counts, labor, and recency use **all** the group's WOs
   regardless of tier. (§2 patched.)
7. **Recency grammar.** "1 week ago" / "1 month ago" singular; plural otherwise ("2 weeks ago",
   "3 months ago"). (§5 table patched.)
8. **Evidence / `average_labor_hours`.** The "last done …" clause is omitted when `last_done_at` is
   unknown (the "avg … hrs" clause is already omitted when labor is unknown, §5).
   `average_labor_hours` on the wire is rounded to **1 decimal**, the same rounding as the evidence
   string. (§5 patched.)
9. **DTC identity** is case-folded **and trimmed on both code and protocol**:
   `(lower(trim(code)), lower(trim(protocol)))`. (§3 patched.)
10. **Candidate text blob** is the ADR's literal blob (`complaint_summary` + `symptoms_summary` +
    `repair_summary` + `repairs.description`); it **excludes** `complaints.text`, which is query-side
    only. (§3 clarified.)

### Known limitations (deferred, not bugs)

- **Vector path uses one embedder** (`search_query` prefix) for both the query and candidate blobs;
  nomic's `search_document` prefix for the candidate side is not applied in v1 (the injected
  `Embedder` is a single callable). Deterministic matching is unaffected; the document-side prefix is
  a follow-up.
- **Vector cost.** The hybrid path can make up to 3 embedder calls (one per tier reached) with up to
  ~101 texts each; on CPU this can take seconds. Caching / pool-limiting is a follow-up. The
  deterministic-only (`fallback`) path is cheap.

## Alternatives considered

- **BM25 over a repair-history FTS5 table instead of Jaccard:** higher precision on free text, but
  needs a new FTS virtual table + triggers (schema + migration work for `database`) for a signal
  that is only 10% of the composite and secondary to structured matching. Jaccard is deterministic,
  index-free, and CPU-cheap on one tablet's history. Rejected for v1; a future FTS table is additive.
- **Laplace (add-1) as primary:** simpler and bounded, but 1-for-1 → 0.667 is a weak penalty; Wilson
  (0.207) enforces the "1-for-1 must not outrank 45-for-50" rule far more strongly. Kept as a
  documented alternative only.
- **Unsure/comeback counted differently:** counted `unsure` as a half-attempt (in `n` only) — no
  principled basis; excluded `unsure` entirely. Confirmed comebacks demote `fixed`→failure; unconfirmed
  comebacks never change the rate (avoids false-positive auto-matches penalizing good repairs).
- **Denormalize `system` onto `work_order_symptoms`:** saves a join to `symptoms` for the system
  signal, but `symptoms.system` is stable and the join is trivial; adds a column to keep in sync.
  Rejected.
- **Bump `AiServiceProtocol.Version` to 2:** the endpoint is purely additive and no existing field
  changes; a bump would force clients to re-check compatibility for no benefit. Kept at 1.
- **`SimilarityScore` = RRF value in hybrid mode:** keeps one number, but makes it non-interpretable
  and hard to test. Keeping `SimilarityScore` as the deterministic composite (vector only re-orders)
  is simpler and stays honest when the embedder is absent.

## Consequences

- `recommender` implements §2–§5 as pure functions over the §8 aggregate: 3-tier search, composite
  similarity (Jaccard tokenizer), optional vector+RRF behind the `ToolContext.embedder` probe, Wilson
  smoothing, rank blend, and evidence rendering.
- `database` writes migration `005` (`work_orders.number`, `idx_wo_closed`, optional
  `idx_outcomes_result`), the `Outcome`/`Comeback`/`Recommendation` write repositories, comeback
  detection (§6), and publishes the reference aggregate SQL (§8).
- `ai-engineer` implements `POST /search/repair-history` (calling `recommender`) and the
  `find_similar_repairs` chat tool; mirrors the DTOs in `models.py` (snake_case, field-for-field).
- `ui-winui` calls `IAiClient.SearchRepairHistoryAsync`, renders the recommendations panel with the
  `fallback` badge and "suggestion" label, writes `recommendations` on present/accept, and never
  re-sorts `candidates`.
- Core additions (this ADR): `Contracts/RepairHistoryContracts.cs` (`RepairHistorySearchRequest`,
  `RepairCandidate`, `RepairHistorySearchResponse`), `Enums/RecommendationEnums.cs` (`MatchTier`),
  `AiServiceProtocol.SearchRepairHistoryPath`, and `IAiClient.SearchRepairHistoryAsync` — the latter
  is a **default interface member that throws** (so adding it does not break the existing
  `AiServiceClient`); `ui-winui` implements the real HTTP call in Phase 5. Existing models/contracts
  are otherwise unchanged.

Referenced from `docs/ARCHITECTURE.md` §4.3 and the decision-records list.
