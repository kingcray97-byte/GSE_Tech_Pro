# ADR-009 — GSE troubleshooting: content schema, spec-unit convention, guided resolution, reading provenance

- **Status:** Accepted
- **Date:** 2026-11-16
- **Owner:** architect

> **Change log (2026-11-16, lead decisions):**
> - **Decision 1 — tree matching serves draft trees.** §3 amended: matching serves any tree with `status != 'retired'` (i.e. `draft` OR `approved`), **preferring `approved`** when more than one tree matches the same model+symptom. The UI must visibly label a served `draft` tree ("DRAFT — not yet approved") and show its terminal results as suggestions. Deliberate for Phase 4 (the first handwritten trees ship as `draft`); `retired` stays excluded.
> - **Decision 2 — single-value spec → `na`.** New §5: when `spec_min == spec_max` (an exact setting with no documented tolerance), the verdict is `na`, never `pass`/`fail` by strict equality; the app must match the AI service's existing `judge` behavior.

## Context

Phase 4 adds the **no-ECU troubleshooting path**: a tech walks a decision tree, records typed readings against manual spec ranges, and sees safety warnings before each step (TUG 660-28 first). Four owners build in parallel against one contract — `gse-domain-expert` (content), `database` (migration + repositories + seeder), `ai-engineer` (`/guided/*` + tools), `ui-winui` (readings-entry screen + guided walker).

The schema for `test_points`, `readings`, `symptoms`, `troubleshooting_trees`, and `troubleshooting_steps` already exists (ARCHITECTURE.md §2.2) and is **not** redesigned here. Two gaps remain, which this ADR closes:

1. **No `safety_warnings` table/model**, yet `test_points.safety_warning_ids`, `troubleshooting_steps.safety_warning_ids`, and `guided_tests.safety_warning_ids` store "JSON array of safety warning ids" and `GuidedStep.SafetyWarnings` returns resolved display strings. There is no place those ids resolve *to*.
2. **No provenance flag on a reading.** `recordings.source` already records `simulated | real` (ADR-007), but a saved `reading` has no way to carry that forward, so a replayed/simulated reading could be mistaken for a real measurement.

The guided wire DTOs (`GuidedStartRequest`, `GuidedNextRequest`, `GuidedStep` in `AiServiceContracts.cs`) are already fixed and **unchanged** (ADR-008); only `Reading` gains one additive field.

## Decision

### 1. Content schema (verbatim, fixed by lead)

`data/troubleshooting/` holds five files. Each is a **flat JSON array**; trees embed their `steps` array. IDs are **stable kebab-case slugs** (`eqm-tug-660-28`, `sw-hydraulic-stored-pressure`, `sym-engine-no-crank`, `tp-tug66028-battery`, `tree-tug66028-no-crank`). Cross-references (`equipment_model_id`, `symptom_id`, `test_point_id`, `safety_warning_ids`, `next_on_pass`/`next_on_fail`) resolve **within the package** — there is no GUID indirection and no external file dependency.

Field lists (nothing here is optional unless marked `?`):

- **`equipment_models.json`** — `{ id, make, model, category, engine_make?, engine_model?, transmission?, fuel_type?, system_notes?, default_unit_system? }`. `category` uses `EquipmentCategory` codes (`tug`, `belt_loader`, `gpu`, `air_start`, `pushback`, `deicer`, `lavatory_truck`, `loader`, `other`). `default_unit_system?` uses `UnitSystem` codes (`imperial` | `metric`).
- **`safety_warnings.json`** — `{ id, category, text, source_citation }`. `category` uses `SafetyWarningCategory` codes (below).
- **`symptoms.json`** — `{ id, code, name, category, system, description }`. `category` uses `SymptomCategory` codes (`engine`, `hydraulic`, `electrical`, `drivetrain`, `brakes`, `hvac`).
- **`test_points.json`** — `{ id, equipment_model_id, name, system, location, reading_type, expected_unit, spec_min, spec_max, spec_source, procedure_ref, safety_warning_ids }`. `reading_type` uses `ReadingType` codes; `safety_warning_ids` is a JSON array of slugs.
- **`trees.json`** — `{ id, equipment_model_id, symptom_id, title, system, status, source, steps: [ … ] }`, where each step is `{ id, step_order, instruction, question?, reading_type?, test_point_id?, spec_min?, spec_max?, spec_source?, safety_warning_ids?, next_on_pass?, next_on_fail?, terminal_result? }`. `status` uses `TreeStatus` codes; `source` uses `TreeSource` codes.

**Seeding rule (consequence, not a redesign):** the content slug is stored **verbatim as the primary key** (`safety_warnings.id = "sw-hydraulic-stored-pressure"`, `equipment_models.id = "eqm-tug-660-28"`, etc.). No slug→GUID mapping table. Cross-references stay slug-to-slug, so the seeder is a simple idempotent upsert keyed on `id`, and a re-import of an edited JSON package overwrites cleanly. The `EntityBase.Id` GUID default is only for *runtime-created* rows (a tech-authored symptom/tree, a new asset); seeded content overrides it with the slug. Because of this, the guided wire carries **slugs** in `equipment_model_id`, `symptom_id`, `tree_id`, `step_id`, and `test_point_id` (not UUIDs — the `<uuid>` examples in ADR-008 were Phase-3 placeholders).

### 2. Spec-unit convention (definitive)

- **`test_points.spec_min`/`spec_max` are expressed in `test_points.expected_unit`** — a *display/authoring* unit such as `psi`, `V`, `°C`, `L/min`, `kPa`. `expected_unit` is what the author typed from the manual, **independent of the shop's display preference**.
- **`troubleshooting_steps.spec_min`/`spec_max`** (when present) are the step's spec in the **same unit as its `test_point_id`'s `expected_unit`**. The step has **no unit column** — it inherits the unit from its test point. Steps **with a `test_point_id` and a numeric spec always point at a test point**; steps **without a `test_point_id` are question/observation steps with no numeric spec**.
- **`GuidedStep.SpecMin`/`SpecMax` are returned already canonicalized to SI**, with the unit implied by `ReadingType` (not carried on the wire — there is no field for it). Canonical mapping (identical to `UnitConverter`):

  | `ReadingType` code | canonical unit | display (imperial / metric) |
  |---|---|---|
  | `hydraulic_pressure`, `fuel_pressure` | `Pa` | `psi` / `bar` |
  | `temperature` | `°C` | `°F` / `°C` |
  | `flow` | `L/min` | `L/min` / `L/min` |
  | `voltage` | `V` | `V` / `V` |
  | `current` | `A` | `A` / `A` |
  | `other` | — (no numeric spec) | — |

  So canonical = **Pa / °C / L/min / V / A**. The service computes `spec_canonical = ToCanonical(spec_min, expected_unit)` (for a step, using the *test point's* `expected_unit`); the app compares the tech's reading by `ToCanonical(entered, enteredUnit)` against those canonical bounds, then renders via `ToDisplay(canonical, canonicalUnit, system)`.

- **One canonical mapping, two implementations.** C# `UnitConverter` (already in Core) is the reference; `ai-engineer`'s Python `units.py` mirrors it exactly (same unit strings, same linear factors, same `display = canonical * scale + offset` form for temperature). The canonical unit for a given `reading_type` is fixed by the table above in **both** languages, so a step's canonical spec and the app's canonical reading comparison derive the same value. `ai-engineer` must not introduce a different unit string or factor.

- **Authoring invariants (binding on `gse-domain-expert`):** every `spec_min`/`spec_max`/`spec_source` is either cited (`"TUG 660-28 Maintenance Manual, page 12"`) or `"TODO: needs spec from manual"`. A step that carries `spec_min`/`spec_max` **must** have a `test_point_id`; a step's `reading_type` (when present) must agree with its test point's `reading_type` (never "pressure step pointing at a voltage test point").

### 3. Guided resolution flow

`/guided/start` and `/guided/next` read the shared SQLite DB (the content seeded into `equipment_models`, `symptoms`, `safety_warnings`, `test_points`, `troubleshooting_trees`, `troubleshooting_steps`). The service holds only **ephemeral session state** (an in-memory `session_id → { tree_id, current_step_id }`); it persists nothing.

**`POST /guided/start`** (`{ equipment_model_id, symptom_id?, complaint_text? }`):

1. Resolve `equipment_models` by slug `equipment_model_id`.
2. Find the matching `troubleshooting_trees` row(s): `equipment_model_id == <model>` AND `symptom_id == <symptom>` AND `status != 'retired'` — i.e. **`draft` OR `approved` both match**. When more than one tree matches the same model+symptom, **prefer `approved`**; a `draft` tree is served when no `approved` tree exists. **`retired` is always excluded.** This is deliberate for Phase 4: every shipped tree starts `status='draft'` (the handwritten first set) and is promoted to `approved` only after a tech approves it over time, so matching `approved`-only would make the walker undemonstrable in simulator mode. (If `symptom_id` is null and only `complaint_text` is given, the service may match a tree by symptom/summary similarity, or return `available:false` when nothing matches — the "no tree yet" shape from ADR-008.)
3. Load the tree's first `troubleshooting_steps` row (minimum `step_order`).
4. Build the `GuidedStep`: resolve the step's `safety_warning_ids` (JSON array of slugs) against `safety_warnings` into **display strings** (order by `SafetyWarningCategory` severity: `hydraulic_pressure`, `lockout_tagout`, `high_voltage`, `aircraft_proximity`, then `general`); canonicalize the step's spec via §2; set `ReadingType`, `TestPointId`, `SpecMin`/`SpecMax` (canonical), `SpecSource`, `TerminalResult`, `NextOnPass`/`NextOnFail`.
5. Return `GuidedStep` with `available:true`, `tree_id`, `step_id`, `step_order`, and a fresh `session_id`.

**`POST /guided/next`** (`{ session_id, step_id, verdict?, reading_value?, reading_unit?, answer? }`):

1. Look up the session (unknown → `404 not_found`); confirm `step_id` is the current step.
2. If `reading_value`/`reading_unit` are present, **re-canonicalize** (`ToCanonical`) and **cross-check** the app's `verdict` against the step's canonical spec. The app's verdict is authoritative; the service uses the value only for validation/context (and to annotate terminal explanations).
3. Advance: `verdict == pass` → `next_on_pass`; `verdict == fail` → `next_on_fail`. A `null` target = terminal — return the current step with `TerminalResult` set and no next hop.
4. Return the next `GuidedStep` (same build as step 4 above).

**Who records the reading.** `readings` remains **C#-owned** (ADR-008: "C# is the sole writer of all non-RAG tables"). The **app** persists the reading row *before* calling `/guided/next`: it already holds the `work_order_id` (the guided request/response have no `work_order_id` field, and `readings.work_order_id` is `NOT NULL`), canonicalizes via `UnitConverter`, computes the verdict against the canonical `GuidedStep` spec, and writes with `source='manual'` and `is_simulated=false`. The service never opens a writer to `readings`. This **clarifies** the ADR-008 wording ("service canonicalizes + records") — the service canonicalizes + validates + advances; the app records. Likewise the chat `record_reading` tool returns a canonical reading proposal (value + canonical unit + preserved entry) that the app persists through its own data layer; it does not become a second writer.

**Draft-tree UI rule (normative, binding on `ui-winui`):** when the served tree has `status='draft'`, the walker must show a visible badge "DRAFT — not yet approved" (e.g. in the guided header / tree title), and the tree's terminal `terminal_result` is presented as a **suggestion** with the same "AI suggestion — the tech makes the final call" label already used for AI output (ARCHITECTURE.md §4.5 rule 3). An `approved` tree carries no such label.

### 4. Provenance — `readings.is_simulated` (additive)

Add the column additively (non-destructive, default 0):

```sql
ALTER TABLE readings ADD COLUMN is_simulated INTEGER NOT NULL DEFAULT 0;
```

Core model: `Reading.IsSimulated` (`bool`, default `false`) — the **only** change to an existing Core model.

Derivation rule (normative, binding on `database`):

- A reading saved from a **replay/scan** copies `recordings.source == 'simulated'` → `is_simulated = 1` when simulated, `0` when real. The `PidValue`/`DtcReadout` flags from ADR-007 carry the same signal per-value.
- A **manually typed** or **voice-entered** reading is `false` (real) — the tech measured it. This includes every guided-flow reading (`source='manual'`, `is_simulated=false`).
- `is_simulated` is a **provenance** flag, not a quality gate: a simulated reading still compares against spec, but the UI badges it "SIMULATED" so a replayed capture is never mistaken for a live measurement.

### 5. Single-value spec → verdict is `na` (no pass/fail by strict equality)

When a spec has **`spec_min == spec_max`** — an exact setting such as the 1500 psi relief, with **no documented tolerance** — neither the app nor the AI service may compute `pass`/`fail` by strict equality against that single value. The verdict is **`na`** ("cannot judge without a documented tolerance"), so a real gauge reading of 1498 psi or 1503 psi is not incorrectly marked `fail` against an exact 1500 psi.

This rule is **normative and must be implemented identically on both sides**: the AI service already returns `na` for single-value specs in its `judge` helper; the app **must match it** when it computes and records the `readings.pass_fail` verdict (ADR-008 single-writer — the app computes and records). It applies everywhere a reading is judged against a spec: the guided walker (§3), `record_reading`, and the app's own readings-entry comparison. `na` is already a valid `readings.pass_fail` value (§2.2 `readings`), so no schema change.

### Safety-warnings DDL (migration authored by `database`, documented here as the contract)

```sql
CREATE TABLE safety_warnings (
  id TEXT PRIMARY KEY,                  -- kebab-case slug from safety_warnings.json
  category TEXT NOT NULL,               -- SafetyWarningCategory code (§ below)
  text TEXT NOT NULL,                   -- the warning text shown before the step
  source_citation TEXT,                 -- manual+page / OSHA / 'TODO: needs spec from manual'
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_safety_warnings_category ON safety_warnings(category);
```

`category` is a `SafetyWarningCategory` code, seeded from `safety_warnings.json`. The `safety_warning_ids` JSON arrays on `test_points` / `troubleshooting_steps` / `guided_tests` store slugs that resolve to `safety_warnings.id`; the AI service resolves them to `SafetyWarning.Text` (display strings) for `GuidedStep.SafetyWarnings`.

### Core additions (this ADR)

- `src/GseTechPro.Core/Enums/DiagnosticEnums.cs` — `SafetyWarningCategory` enum with `[EnumMember]` codes `hydraulic_pressure`, `lockout_tagout`, `high_voltage`, `aircraft_proximity`, `general`.
- `src/GseTechPro.Core/Models/SafetyWarning.cs` — `SafetyWarning : EntityBase` (`Id`, `Category`, `Text`, `SourceCitation`, `CreatedAt`, `UpdatedAt`, `DeletedAt`).
- `src/GseTechPro.Core/Models/Readings.cs` — `Reading.IsSimulated` (`bool`, default `false`).

## Alternatives considered

- **slug → GUID mapping table for seeded content:** makes cross-references stable across re-seeds even if slugs change, but adds a join on every resolve and a second lookup key to keep in sync. The slugs are deliberately stable and human-auditable; storing them as PKs keeps the seeder a plain upsert and keeps the JSON package and the DB identical. Rejected.
- **Store `spec_unit` on `troubleshooting_steps`:** would let a step override the test point's unit, but adds a column to a schema the lead froze, and the inheritance rule (step = test point's unit) is unambiguous. Rejected; steps inherit the unit.
- **Put the canonical unit on the `GuidedStep` wire (new field):** would make the wire self-describing, but the lead fixed `GuidedStep` with no new fields; the canonical unit is a pure function of `ReadingType` (§2) so both sides derive it. Rejected.
- **Service writes `readings` rows directly:** matches the ADR-008 "service records" wording and keeps the guided walker thin, but violates ADR-008's single-writer invariant and has no `work_order_id` on the wire to satisfy `readings.work_order_id NOT NULL`. Rejected — app records.
- **`readings.is_simulated` via `notes` JSON instead of a column:** no rewrite and works, but it is unqueryable and easy to forget. An additive `INTEGER NOT NULL DEFAULT 0` column is the same one-line migration and is queryable for "show me simulated vs real readings". Chosen.

## Consequences

- `gse-domain-expert` authors the five JSON files with slug ids and the exact field lists above, all specs cited or `TODO`.
- `database` writes migration `004` (`safety_warnings` + `readings.is_simulated`), the `SafetyWarning`/`TestPoint`/`TroubleshootingTree`/`TroubleshootingStep` repositories, the idempotent `TroubleshootingSeeder` (csproj-linked content), and derives `readings.is_simulated` from `recordings.source` on replay/scan saves.
- `ai-engineer` writes `units.py` mirroring `UnitConverter`, implements `/guided/*` tree resolution + canonicalized spec + safety-warning display strings, and keeps `record_reading` a proposal-returning tool (single writer preserved).
- `ui-winui` builds the readings-entry screen and finishes the guided walker (canonical comparison, pass/fail, "SIMULATED" badge, safety warnings first).
- `GuidedStep`, `GuidedStartRequest`, `GuidedNextRequest`, and every other Core model are unchanged except the `Reading.IsSimulated` addition.

Referenced from `docs/ARCHITECTURE.md` §2.2 and the decision-records list.
