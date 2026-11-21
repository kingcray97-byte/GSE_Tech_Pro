# Guided troubleshooting seed content

Phase 4 content for equipment without a readable ECU. The package separates the TUG 660-28 and Power Stow/TUG 600i configurations; they must never be collapsed into one model row or share a specification merely because the Delta 740-774 set contains both.

**Inventory: 2 equipment models, 11 reusable safety warnings, 19 symptoms, 17 test points, and 5 draft trees containing 83 steps** (Phase 4: 8 / 16 / 14 / 2 trees, 36 steps; Phase 10C.4 adds 3 / 3 / 3 / 3 trees, 47 steps — see §8). All five data files are flat JSON arrays. `trees.json` embeds each tree's ordered steps; all other cross-references use stable slugs.

| File | Rows | Scope |
|---|---:|---|
| `equipment_models.json` | 2 | `eqm-tug-660-28` and `eqm-tug-600i`, with unknown 600i powertrain fields omitted |
| `safety_warnings.json` | 8 | Stored hydraulic pressure, lockout/tagout, battery/high-energy electrical, aircraft proximity, hot surfaces, moving machinery, wheel chocks, and fuel/LPG |
| `symptoms.json` | 16 | Five engine, six hydraulic, and five electrical/starting complaints using operator language |
| `test_points.json` | 14 | Nine 660-28 points and five 600i points; 2 cited exact pressure settings and 12 explicit TODO range rows |
| `trees.json` | 2 | 660-28 no-crank, 19 steps; 600i no/slow lift, 17 steps |

The trees are deliberately `draft`. JSON validation catches broken structure, not an incorrect specification or unsafe shop procedure. Domain approval and the open items in §6 are still required before production use.

## 1. Database mapping

The data layer owns `created_at` and `updated_at` (and `deleted_at` where present); those fields are intentionally absent from source JSON. Import in dependency order: equipment models → safety warnings and symptoms → test points → trees and flattened steps. Wrap each package import in a transaction and upsert by the stable source `id`.

### `equipment_models.json` → `equipment_models`

| JSON field | Database column | Rule |
|---|---|---|
| `id` | `id` | Permanent slug; do not regenerate on import |
| `make` | `make` | `TUG` in this package |
| `model` | `model` | `660-28` or `600i` |
| `category` | `category` | Database enum string; `belt_loader` for both the 660-28 and 600i model rows |
| `engine_make` | `engine_make` | Optional; omitted when not verified |
| `engine_model` | `engine_model` | Optional; omitted when not verified |
| `transmission` | `transmission` | Optional; configuration-dependent text is preserved |
| `fuel_type` | `fuel_type` | Optional; configuration-dependent text is preserved |
| `system_notes` | `system_notes` | Applicability, citation, and configuration cautions |
| `default_unit_system` | `default_unit_system` | `imperial` or `metric`; controls display only, never stored reading canonicalization |

Optional engine/powertrain fields are omitted rather than filled with `unknown` or copied between configurations. The 660-28 configuration details were read from the published *TUG Model 660-28 Operation Manual CD397-Oper*, Operation-1-1 (PDF page 65) and Operation-3-8 (PDF page 100).

### `safety_warnings.json` → `safety_warnings`

| JSON field | Database column | Rule |
|---|---|---|
| `id` | `id` | Reused by test points and steps |
| `category` | `category` | `hydraulic_pressure`, `lockout_tagout`, `high_voltage`, `aircraft_proximity`, or `general` |
| `text` | `text` | Full warning rendered **before** the connected procedure or step |
| `source_citation` | `source_citation` | Manual + page, optionally followed by an explicit unresolved-procedure TODO |

The `high_voltage` category also covers battery/high-current hazards; its use does not claim that every battery on these machines is an EV/high-voltage traction pack. Do not attach a hydraulic warning after an instruction: the UI must resolve and present warnings first.

### `symptoms.json` → `symptoms`

| JSON field | Database column | Rule |
|---|---|---|
| `id` | `id` | Stable slug referenced by trees and work-order symptoms |
| `code` | `code` | Unique uppercase lookup/display code |
| `name` | `name` | Technician-facing symptom title |
| `category` | `category` | `engine`, `hydraulic`, or `electrical` here |
| `system` | `system` | Search/grouping taxonomy; not an assertion of root cause |
| `description` | `description` | Operator wording, boundaries, and information to capture |
| — | `is_standard` | Set to `1` for every shipped pick-list row |
| — | `created_at`, `updated_at` | Data layer timestamps |

`code` and `id` are both unique. A symptom is an intake description, not a diagnosis; for example, low hydraulic pressure must retain its measured conditions rather than becoming proof of a bad pump.

### `test_points.json` → `test_points`

| JSON field | Database column | Rule |
|---|---|---|
| `id` | `id` | Permanent test-point slug |
| `equipment_model_id` | `equipment_model_id` | Resolves to this package's model row; test points are not global across models |
| `name` | `name` | Named physical/conditional measurement |
| `system` | `system` | Search/grouping taxonomy |
| `location` | `location` | Connection point plus access/identification limitations |
| `reading_type` | `reading_type` | `voltage`, `hydraulic_pressure`, or `flow` in the initial package |
| `expected_unit` | `expected_unit` | Unit of **both** source range values: `V`, `psi`, or `L/min` |
| `spec_min`, `spec_max` | `spec_min`, `spec_max` | JSON numbers or `null`; never formatted strings |
| `spec_source` | `spec_source` | Exact manual + page, or exactly `TODO: needs spec from manual` |
| `procedure_ref` | `procedure_ref` | Tool connection, test conditions, source page when known, and unresolved steps |
| `safety_warning_ids` | `safety_warning_ids` | Serialize the JSON array losslessly as DB JSON text; do not flatten to a prose string |
| — | `created_at`, `updated_at`, `deleted_at` | Data layer metadata |

Two conditional measurements can name the same physical port only when the test procedure is materially different (normal lift versus relief check). Port identity and rated connections take precedence over convenience. A `TODO` location means identify the correct schematic/port before connecting anything; it is not permission to guess.

### `trees.json` → `troubleshooting_trees`

| JSON field | Database column | Rule |
|---|---|---|
| `id` | `id` | Stable tree slug |
| `equipment_model_id` | `equipment_model_id` | Exactly one applicable model |
| `symptom_id` | `symptom_id` | Intake symptom that selects the tree |
| `title` | `title` | Technician-facing title |
| `system` | `system` | System filter |
| `status` | `status` | `draft` in this package |
| `source` | `source` | `handwritten` in this package |
| `steps` | — | Flatten to `troubleshooting_steps`; not a DB column on the tree |

### Embedded `steps[]` → `troubleshooting_steps`

The loader supplies `tree_id` from the parent tree. Every embedded step has exactly the following source fields.

| JSON field | Database column | Rule |
|---|---|---|
| `id` | `id` | Stable step slug; branch targets use this value |
| `step_order` | `step_order` | Contiguous, unique per tree; order `1` is the entry step |
| `instruction` | `instruction` | Action and test conditions; safety warnings render first |
| `question` | `question` | Question answered by pass/fail, or `null` at a terminal result |
| `reading_type` | `reading_type` | Must match the referenced test point; `null` for inspection/result steps |
| `test_point_id` | `test_point_id` | Resolves within the package and belongs to the tree's model |
| `spec_min`, `spec_max` | `spec_min`, `spec_max` | Copied exactly from the test point during authoring/import |
| `spec_source` | `spec_source` | Copied exactly from the test point; `null` only when no reading/spec applies |
| `safety_warning_ids` | `safety_warning_ids` | Serialize as DB JSON text; every ID must resolve |
| `next_on_pass`, `next_on_fail` | `next_on_pass`, `next_on_fail` | Step IDs in the same tree; both `null` only at terminals |
| `terminal_result` | `terminal_result` | Nonempty diagnosis/repair suggestion at a terminal; otherwise `null` |
| — | `created_at`, `updated_at` | Data layer timestamps |

All branch targets must be reachable from step `1`. A nonterminal must carry both branch pointers in this initial package. A terminal must not retain a hidden next pointer. Terminal suggestions identify the next justified test/repair, not a guaranteed root cause.

## 2. Units and pass/fail behavior

`test_points.spec_min/spec_max` are **always in `expected_unit`**. Steps inherit the unit through `test_point_id`; no separate step-unit field exists, and duplicating a converted range would invite disagreement.

| `reading_type` | Source `expected_unit` used here | Canonicalization owner |
|---|---|---|
| `voltage` | `V` | Phase 4 units layer; canonical V |
| `hydraulic_pressure` | `psi` | Phase 4 units layer; canonical Pa |
| `flow` | `L/min` | Phase 4 units layer; convert consistently on both C# and Python sides |

The AI service canonicalizes `GuidedStep.SpecMin/SpecMax` to the SI value implied by `ReadingType`; the app canonicalizes the entered value before comparison and converts only for display. Preserve both entered value and entered unit in `readings`.

A nominal battery voltage, alternator amp rating, hose pressure rating, or relief **setting** is not automatically an acceptable diagnostic range. For `tp-tug66028-main-relief` and `tp-tug66028-steering-relief`, equal min/max values represent a single setting printed by the manual, **not a measured tolerance**. The exact connection, operating condition, gauge tolerance, and acceptable deviation remain open before production use.

Where `spec_source` is TODO, software must not substitute a generic 12 V rule, a remembered hydraulic pressure, or an AI-generated threshold. Record the reading if desired, but require the technician to obtain the approved range before claiming spec-based pass/fail.

## 3. Citation and clean-room policy

Content uses published equipment manuals and ordinary diagnostic logic only. No OEM proprietary database, diagnostic-tool binary, reverse-engineered data, or unapproved Shop Foreman Pro material was used. This is authored JSON, not redistributed vendor artwork or tooling.

The local source set is:

- `docs/Manuals/660-28_Maintenance&Parts_052019C.pdf`
- `docs/Manuals/740-774 Delta Airlines/740-774 Delta Gas/2  740-774 General description 600i (US).pdf`
- `docs/Manuals/740-774 Delta Airlines/740-774 Delta Gas/3  740-774 User manual TUG 660-28 (US).pdf`
- `docs/Manuals/740-774 Delta Airlines/740-774 Delta Gas/4  740-774 Maintenance manual 600i  (Delta).pdf`

In this subagent environment the local PDFs could not be rendered or text-extracted with the available reader/process tools. To avoid an entirely invented package, two 660-28 pressure settings and the configuration metadata were checked in a published HTML copy of *TUG Model 660-28 Operation Manual CD397-Oper*, including [PDF page 101, Table Operation-3-13](https://www.manualslib.com/manual/1645405/Textron-Gse-660-28.html?page=101). The exact citation is preserved in each applicable row. The public copy identifies its overall document as June 2019 Rev C; the cited specification sheet itself is labeled August 2018 Rev B.

The two numeric entries are:

- Main hydraulic relief pressure setting: **1500 psi**, Table Operation-3-13, PDF page 101.
- Steering hydraulic relief pressure setting: **1000 psi**, same table/page, in a separate steering row.

They are **not** carried over to `eqm-tug-600i`. The public page favors manual identification over web listings or similar-model guesses. Exact local-PDF applicability still needs a reviewer with working PDF extraction.

## 4. Model identity — resolved

The 660-28 is a belt loader, as identified in the *TUG Model 660-28 Operation Manual CD397-Oper* cover and §1.2 (PDF pages 1 and 65). Its `belt_loader` category is consistent with the app's existing **TUG 660-28 Belt Loader [BeltLoader]** label; the separate 600i row remains `belt_loader` as well.

## 5. Cross-reference and editing rules

1. Keep IDs kebab-case and stable: `eqm-`, `sw-`, `sym-`, `tp-`, `tree-`, `st-` prefixes by entity kind. Never derive identity from display wording.
2. New test points and trees reference only an existing model; new steps reference only points belonging to the same model.
3. Keep symptom `code` unique. Do not reuse `sym-engine-no-crank` for an engine that cranks normally but will not fire.
4. Quote a numeral only after reading its exact manual context. Record manual title, section/table where useful, and PDF page. Use `TODO: needs spec from manual` rather than a plausible placeholder range.
5. Put every relevant warning ID on the step **before** the hazardous action. Live-test steps must not imply that lockout remains applied while the machine is intentionally energized; describe isolation for connection/repair and the controlled exception separately.
6. Safety-terminal results should stop operation and name the isolation/repair/retest action. Do not recommend jumpering neutral, seat, emergency-stop, or hydraulic safety devices.
7. Do not mark a tree `approved` merely because validation passes. Obtain a domain review of the manual pages, model applicability, procedure, and missing tolerances.
8. Keep all content offline and free of runtime URLs/API dependencies. Links in this README are re-check aids only.

## 6. Explicit TODO register

The following could not be verified to the required standard and therefore have `null` range fields plus the exact TODO string in the affected test points/steps:

- All 660-28 electrical acceptance values: resting battery voltage; minimum cranking voltage/duration; starter positive-feed and ground-side voltage-drop maxima; solenoid S-terminal acceptance; neutral-start output acceptance; charging-voltage range and engine/load conditions.
- All 600i measurement acceptance values: each auxiliary battery's resting/loaded range and pair-matching criterion; main lift pressure; cap-end cylinder pressure; rod-end/back-pressure; relief setting/test limit; pump flow and pressure/load/temperature conditions.
- Port IDs, precise physical access, and detailed gauge/flow-meter connection procedures for the 600i hydraulic points.
- Detailed pressure-test conditions, gauge accuracy/tolerance, allowable deviation, and permitted relief-hold duration for the two cited 660-28 relief **settings**.
- 600i engine make/model, transmission, and fuel type. They are intentionally omitted, not inferred from the base chassis.

Additional limits:

- The 660-28 electrical tree can guide physical measurements but cannot make an automatic spec verdict while its ranges remain TODO.
- The 600i tree likewise records the diagnostic sequence but needs the exact Power Stow pressure/flow values and procedures before unattended pass/fail use.
- No separate 660-28 crank-no-start, charging-ripple, hydraulic-drift, or drivetrain/brake/HVAC tree is included in this bounded package.

## 7. Validation

From the **repository root**, this PowerShell wrapper runs a Python structural check. It checks JSON syntax and exact row shape, expected counts, unique IDs/codes, model/symptom/test-point/warning/branch references, model-unit consistency, mirrored specs, reachable DAG structure, terminal invariants, and that every numeric range has either a manual-page citation or the exact TODO marker. It also requires unresolved test-point ranges to use the TODO marker instead of silently disappearing.

```powershell
@'
import json
import math
import re
from pathlib import Path

root = Path("data/troubleshooting")
TODO = "TODO: needs spec from manual"
expected = {
    "equipment_models.json": 2,
    "safety_warnings.json": 8,
    "symptoms.json": 16,
    "test_points.json": 14,
    "trees.json": 2,
}
data = {}
for filename, count in expected.items():
    rows = json.loads((root / filename).read_text(encoding="utf-8"))
    assert isinstance(rows, list), (filename, "root must be an array")
    assert len(rows) == count, (filename, len(rows), count)
    data[filename] = rows

models = data["equipment_models.json"]
warnings = data["safety_warnings.json"]
symptoms = data["symptoms.json"]
points = data["test_points.json"]
trees = data["trees.json"]

model_fields = {"id", "make", "model", "category", "engine_make", "engine_model",
                "transmission", "fuel_type", "system_notes", "default_unit_system"}
model_required = {"id", "make", "model", "category", "system_notes", "default_unit_system"}
warning_fields = {"id", "category", "text", "source_citation"}
symptom_fields = {"id", "code", "name", "category", "system", "description"}
point_fields = {"id", "equipment_model_id", "name", "system", "location", "reading_type",
                "expected_unit", "spec_min", "spec_max", "spec_source", "procedure_ref",
                "safety_warning_ids"}
tree_fields = {"id", "equipment_model_id", "symptom_id", "title", "system", "status", "source", "steps"}
step_fields = {"id", "step_order", "instruction", "question", "reading_type", "test_point_id",
               "spec_min", "spec_max", "spec_source", "safety_warning_ids", "next_on_pass",
               "next_on_fail", "terminal_result"}
slug = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
citation = re.compile(r"\bpages?\s+\d+", re.I)
reading_types = {"hydraulic_pressure", "flow", "temperature", "voltage", "fuel_pressure", "current", "other"}
warning_categories = {"hydraulic_pressure", "lockout_tagout", "high_voltage", "aircraft_proximity", "general"}
symptom_categories = {"engine", "hydraulic", "electrical", "drivetrain", "brakes", "hvac"}
unit_for_type = {
    "voltage": {"V"},
    "hydraulic_pressure": {"psi", "bar"},
    "fuel_pressure": {"psi", "bar"},
    "temperature": {"°C"},
    "flow": {"L/min"},
    "current": {"A"},
    "other": {"V", "psi", "bar", "°C", "L/min", "A"},
}

def is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)

def has_citation(value):
    return value == TODO or (isinstance(value, str) and bool(citation.search(value)))

def unique_ids(rows, kind):
    result = {row["id"] for row in rows}
    assert len(result) == len(rows), (kind, "duplicate id")
    for row in rows:
        assert isinstance(row["id"], str) and slug.fullmatch(row["id"]), (kind, row["id"])
    return result

for model in models:
    assert model_required <= set(model) and set(model) <= model_fields, model.get("id")
    assert model["make"] and model["model"] and model["default_unit_system"] in {"imperial", "metric"}
model_ids = unique_ids(models, "equipment model")
assert {m["id"]: (m["make"], m["model"], m["category"]) for m in models} == {
    "eqm-tug-660-28": ("TUG", "660-28", "belt_loader"),
    "eqm-tug-600i": ("TUG", "600i", "belt_loader"),
}

for warning in warnings:
    assert set(warning) == warning_fields, warning.get("id")
    assert warning["category"] in warning_categories, warning["id"]
    assert warning["text"].strip() and has_citation(warning["source_citation"]), warning["id"]
warning_ids = unique_ids(warnings, "warning")
assert {"hydraulic_pressure", "lockout_tagout", "high_voltage", "aircraft_proximity", "general"} <= {w["category"] for w in warnings}

for symptom in symptoms:
    assert set(symptom) == symptom_fields, symptom.get("id")
    assert symptom["category"] in symptom_categories, symptom["id"]
    assert symptom["name"].strip() and symptom["description"].strip(), symptom["id"]
symptom_ids = unique_ids(symptoms, "symptom")
codes = [s["code"] for s in symptoms]
assert len(codes) == len(set(codes)) and all(codes), "symptom code uniqueness"
assert {"sym-engine-no-crank", "sym-hydraulic-no-lift", "sym-charging-ripple"} <= symptom_ids

point_by_id = {p["id"]: p for p in points}
unique_ids(points, "test point")
todo_point_count = 0
for point in points:
    assert set(point) == point_fields, point.get("id")
    assert point["equipment_model_id"] in model_ids, point["id"]
    assert point["reading_type"] in reading_types, point["id"]
    assert point["expected_unit"] in unit_for_type[point["reading_type"]], point["id"]
    assert point["location"].strip() and point["procedure_ref"].strip(), point["id"]
    lo, hi = point["spec_min"], point["spec_max"]
    assert is_number(lo) or lo is None, (point["id"], "spec_min")
    assert is_number(hi) or hi is None, (point["id"], "spec_max")
    assert has_citation(point["spec_source"]), point["id"]
    if is_number(lo) or is_number(hi):
        assert point["spec_source"] != TODO, (point["id"], "numeric spec needs a real source")
        if is_number(lo) and is_number(hi):
            assert lo <= hi, point["id"]
    else:
        todo_point_count += 1
        assert point["spec_source"] == TODO, (point["id"], "unresolved range must be TODO")
    ids = point["safety_warning_ids"]
    assert isinstance(ids, list) and ids and len(ids) == len(set(ids)), point["id"]
    assert set(ids) <= warning_ids, point["id"]
assert todo_point_count == 12, todo_point_count

assert unique_ids(trees, "tree")
step_count = 0
terminal_count = 0
for tree in trees:
    assert set(tree) == tree_fields, tree.get("id")
    assert tree["equipment_model_id"] in model_ids and tree["symptom_id"] in symptom_ids, tree["id"]
    steps = tree["steps"]
    assert isinstance(steps, list) and steps, tree["id"]
    by_id = {step["id"]: step for step in steps}
    unique_ids(steps, f"step in {tree['id']}")
    orders = sorted(step["step_order"] for step in steps)
    assert orders == list(range(1, len(steps) + 1)), (tree["id"], orders)
    root_step = next(step for step in steps if step["step_order"] == 1)
    for step in steps:
        step_count += 1
        assert set(step) == step_fields, step.get("id")
        assert step["instruction"].strip(), step["id"]
        ids = step["safety_warning_ids"]
        assert isinstance(ids, list) and len(ids) == len(set(ids)) and set(ids) <= warning_ids, step["id"]
        point_id = step["test_point_id"]
        if point_id is not None:
            assert point_id in point_by_id, step["id"]
            point = point_by_id[point_id]
            assert point["equipment_model_id"] == tree["equipment_model_id"], step["id"]
            assert step["reading_type"] == point["reading_type"], step["id"]
            for field in ("spec_min", "spec_max", "spec_source"):
                assert step[field] == point[field], (step["id"], field)
        else:
            assert step["reading_type"] is None, step["id"]
            assert all(step[field] is None for field in ("spec_min", "spec_max", "spec_source")), step["id"]
        on_pass, on_fail = step["next_on_pass"], step["next_on_fail"]
        assert (on_pass is None) == (on_fail is None), step["id"]
        terminal = on_pass is None
        if terminal:
            terminal_count += 1
            assert isinstance(step["terminal_result"], str) and step["terminal_result"].strip(), step["id"]
        else:
            assert step["terminal_result"] is None, step["id"]
            assert on_pass != step["id"] and on_fail != step["id"], step["id"]
            assert on_pass in by_id and on_fail in by_id, step["id"]

    reachable = set()
    def visit(step_id):
        if step_id in reachable:
            return
        reachable.add(step_id)
        for branch in ("next_on_pass", "next_on_fail"):
            target = by_id[step_id][branch]
            if target is not None:
                visit(target)
    visit(root_step["id"])
    assert reachable == set(by_id), (tree["id"], "unreachable", set(by_id) - reachable)

    active = set()
    def acyclic(step_id):
        assert step_id not in active, (tree["id"], "cycle", step_id)
        if step_id in reached:
            return
        active.add(step_id)
        for branch in ("next_on_pass", "next_on_fail"):
            target = by_id[step_id][branch]
            if target is not None:
                acyclic(target)
        active.remove(step_id)
        reached.add(step_id)
    reached = set()
    acyclic(root_step["id"])

assert step_count == 36, step_count
assert terminal_count > 0
print(f"PASS: {len(models)} models, {len(warnings)} warnings, {len(symptoms)} symptoms, " +
      f"{len(points)} test points, {len(trees)} trees/{step_count} steps; " +
      f"{terminal_count} terminal leaves; {todo_point_count} explicit test-point TODO rows")
'@ | python -
```

For a parse-only check, also run:

```powershell
Get-ChildItem data/troubleshooting/*.json | ForEach-Object {
  Get-Content -Raw $_.FullName | python -m json.tool > $null
}
```

A loader/integration test should additionally import twice into a clean Phase 4 SQLite schema and assert idempotent stable IDs, valid foreign keys, and identical JSON warning arrays. Domain review against the cited PDF pages remains mandatory even after both checks pass.

## 8. Phase 10C.4 — PCS transmission, PCS disc parking brake, Ford MSG-425 (TUG 660-28)

All three trees are `draft` and belong to `eqm-tug-660-28` (Ford MSG-425 2.5 L, GM 4L70 / PCS 4LHD transmission, per the existing model row). Sources are the owner-supplied PDFs in `Reference/GSE diagnostic apps/` (read only, never bundled):

| Tree | Symptom | Steps | Sources |
|---|---|---:|---|
| `tree-tug66028-trans-no-engage` — will not shift out of Neutral (codes, TCM power/comms, lever, TPS, RPM, shift-inhibit input, fluid, line pressure, forward clutch solenoid) | `sym-trans-no-engage` (drivetrain) | 27 | *PCS 4-Speed Transmission Quick Reference Guide v1.3* pp.1–4; *PCS 4LHD/4LHDX Application Guide v2.0* §2.5.7–2.5.8 pp.64–65; *PCS 4LHD/4LHDX Technician's Guide v2.0* §4 |
| `tree-tug66028-park-brake-burnish` — parking brake does not hold (inspect, burnish, adjust) | `sym-park-brake-weak` (brakes) | 11 | *MICO Form 81-950-016* pp.1–2 (burnishing per SAE J360 §7.3; fibre-dust precautions); PCS kit drawing *4LHD_BRK2050* |
| `tree-tug66028-ford-mil` — engine MIL on (read and look up codes) | `sym-engine-mil-on` (engine) | 9 | *Ford/EDI GCP Diagnostic Support Manual and DTC Definitions* (EDI 1080031) pp.2–3, Section 3 pp.16–22 |

New safety warnings: `sw-trans-direct-control` (TDD direct control, QRG p.4), `sw-trans-line-pressure` (>300 psi, QRG p.2), `sw-brake-dust` (MICO p.1).

New test points: `tp-tug66028-trans-line-pressure-4lhd` **170–193 psi** and `-4lhdx` **198–227 psi** (QRG p.2 table, 0 A commanded, Neutral, 1200 RPM, fluid 100–200 °F); `tp-tug66028-tcm-ignition-feed` (TCM pins 19/20 to 42/9, range TODO).

**10C.4 TODO register / limits**

- No resistance reading type exists (`ReadingType` has no ohms), so the cited resistance checks (CAN 60 Ω between CAN H/L, QRG p.2; valve-body generation by solenoid resistance, QRG p.1) are written as inspection text, not recorded readings. Adding a resistance reading type is a Core/units change for `architect`.
- TCM feed voltage acceptance, the parking-brake hold-test method, and MICO adjustment values: `TODO: needs spec from manual`.
- The shift-inhibit pin (5, grey/white) is from PCS's **example** bulkhead (Application Guide p.64); the TUG harness pin must be confirmed on the vehicle's harness drawing (TODO in the terminal text).
- The Ford manual's Section 4 diagnostic procedures are scanned images with no text layer, so the Ford tree stops at "look up the DTC and follow its Section 4 page". The Ford Section 3 SPN/FMI → DTC table (pp.16–22) could later be added to the DTC library (fuel type matters).
- The MICO burnish figures are SAE guidelines; the tree tells the technician to stop and get an approved procedure if the unit cannot do them safely (airside speed limits, aircraft proximity).

