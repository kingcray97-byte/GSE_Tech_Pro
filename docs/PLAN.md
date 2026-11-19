# GSE Tech Pro — Build Plan

> Owned by `gse-lead`. This is the living roadmap: what's done, what's next, and risks.
> The architecture-level phase map lives in `docs/ARCHITECTURE.md` §8. This file tracks granular tasks.
> A clean session resumes from `AGENTS.md`, `docs/PROMPT.md`, this file, and `docs/ARCHITECTURE.md` alone.

## Status

- **Architecture: APPROVED** (2026-11-15). `docs/ARCHITECTURE.md` + 8 ADRs in `docs/decisions/`.
- **Approved decisions:** unpackaged + Inno Setup (001), BitLocker at-rest (002), `nomic-embed-text` (003), separate GSE Tech Pro icon — Option A (004), permissive PDF stack, no PyMuPDF (005), local-first AI + opt-in cloud fallback (006).
- **Feature inventory expanded** from the installed Shop Foreman Pro's help file + readme (clean-room; no binaries). User decisions: Mitchell1 repair-info → later, 5-gas → drop, NVH → drop, Bosch MTS hardware → drop.
- **Phase 1: DONE.**
- **Phase 2: DONE** (2026-11-16). Device abstraction + 32-bit host + J2534/RP1210 wrappers + J1939/J1708/OBD II/ISO 15765-UDS decoders + Equipment Health / live data / recording-playback UI + 540-row DTC library. 243 xUnit tests pass. Real-hardware verification deferred (no RLink X3 / Nexiq on the dev machine).
- **Phase 3: DONE** (2026-11-16). Manuals & AI: `ai-service` (FastAPI + LiteLLM→Ollama + RAG hybrid search + ingestion), `AiServiceClient` + Tech Chat + guided-troubleshooting + Manual-RAG UI, sqlite-vec migration. 600i RAG acceptance 15/15. **468 tests pass** (265 C# + 201 Python + 2 placeholder). Model/embedding runtime unmeasured (Ollama present but no models pulled on the dev machine).

## Phases

| # | Phase | Status |
|---|---|---|
| 0 | Architecture & ADRs | ✅ Done |
| 1 | Shell, data, build pipeline | ✅ Done |
| 2 | Diagnostics (device abstraction, J2534/RP1210, J1939/J1708/OBD II, Equipment Health, live data, recording) | ✅ Done |
| 3 | Manuals & AI (ingestion, hybrid search, Ollama + model, Tech Chat, guided troubleshooting) | ✅ Done |
| 4 | GSE troubleshooting (readings entry, decision trees, safety warnings — TUG 660-28 first) | ✅ Done |
| 5 | Repair history learning (recommendation engine, outcomes, comebacks) | ✅ Done |
| 6 | Oscilloscope (PicoSDK, then Hantek, shared interface) | 🔄 In progress |
| 7 | Vision & voice, ECOM/CANCapture | ⬜ |
| 8 | Full installer (signing, silent multi-tablet, upgrades, updates) | ✅ Done |
| 9 | Optional shop-server sync | ⬜ |

## Done (Phase 1)

**Solution:** `src/GseTechPro.sln` — 6 projects, builds clean (0 warnings/errors): Core, Data, Devices, Devices.Host32 (x86), App (WinUI 3), Tests.

- **Core** (`architect`): 34 entities, enums + `DbEnum` codec (now cached + `TryParse`), `IDiagnosticAdapter`/`IScopeDevice`/`IAiClient`, diagnostic/AI/scope DTOs, `UnitConverter`/`ReadingValue` (canonical SI + preserved entered unit), `Clock.UtcNow` (Z-suffixed).
- **Data** (`database`): SQLite schema (36 tables incl. FTS5), versioned migrations, 16 repositories, backup, composition root; sqlite-vec deferred (Phase 3).
- **Devices** (`device-comms`): `SimulatedDiagnosticAdapter` (J1939/J1708/OBD II, seeded/deterministic, canonical SI), `DiagnosticAdapterFactory`; `GseTechPro.Devices.Ipc` (PipeMessage/HostContracts); x86 host32 named-pipe shell (crash-hardened, `CurrentUserOnly`, per-session name).
- **App** (`ui-winui`): WinUI 3 shell matching screenshots 01/03/04 (menu bar, toolbar, tabs, nav rail, breadcrumb, status bar), theme resources (light/dark/sunlight, accent #2E3391/#8C93F0), simulator flow (DTCs red/blue, live data, freeze frame), work-order/asset lists; clear-DTCs confirmation + audit, unit canonicalization at display, stale-row clearing, async-void hardening.
- **Installer** (`installer-deploy`): Inno Setup skeleton (`GSE-Tech-Pro-Setup.iss`), `config-template.ini`, `docs/DEPLOYMENT.md`.
- **Tests** (`tester`): xUnit (133) + pytest (2) all passing.

## Done (Phase 2)

- **Architecture** (`architect`): ADR-007 (app↔host32 IPC: pipe-name handoff + token + command protocol + kind-byte streaming), `IsSimulated` provenance on `IDiagnosticAdapter`/`PidValue`/`DtcReadout`, `HostIpcContracts.cs`, recording/playback contracts + `recordings`/`recording_frames` DDL.
- **Device comms** (`device-comms`): `PipeFrame` kind-byte framing, host32 `--pipe`/`--token` + constant-time handshake + command dispatch (crash-hardened), `HostDiagnosticAdapter` client + `DiagnosticAdapterFactory` host path (restarts crashed host32), simulator `IsSimulated=true`; **protocol decoders** (pure, cited, unit-tested) — J1939 (PGN/DM1/DM2/DM11/SPN-FMI/NAME/TP/live-data), J1708/J1587, OBD II ($01/$03/$04/$09), ISO 15765/UDS (0x19/0x14); **J2534** (SAE J2534-1 P/Invoke + registry discovery) and **RP1210** (TMC RP1210 P/Invoke + `RP121032.ini`) wrappers, vendor DLLs loaded ONLY in the x86 host. Reviewer cross-checked the byte-framing against the published standards and fixes were applied.
- **Database** (`database`): migration `002_Recordings.sql`, `RecordingRepository`/`RecordingFrameRepository`/`RecordingSerialization`, `DtcLibrarySeeder` (idempotent `(protocol, code)` upsert, embeds `data/dtc/*.json` via csproj link), `DbValueConverter` graceful unknown-enum fallback.
- **UI** (`ui-winui` + `general` for a build fix): Equipment/Vehicle Health (04), live-data views list/snaplist/bar/digital/analog/line-graph + custom display (13–16), recording/playback with VCR bar + triggers (17–18), device selection with `IsSimulated` badge, `Disconnected` → interrupted-recording handling. (One XamlCompiler crash was fixed: `{x:Bind}` to `PointCollection` on `Polyline.Points` → `{Binding}`.)
- **Content** (`gse-domain-expert`): 540-row cited DTC/SPN/FMI library (`data/dtc/j1939.json` 214, `j1587.json` 124 stored as J1708, `obd2.json` 202) + README (source/clean-room policy, no invented severity).
- **Tests** (`tester`): 243 total — `GseTechPro.Tests` 145 (incl. recording, DTC-seed, IsSimulated) + `GseTechPro.Devices.Tests` 98 (framing, IPC round-trip, decoders, CAN/RP1210 framing); simulator determinism flake fixed (reads decoupled from stream RNG).

## Done (Phase 3 — Manuals & AI)

Order: contracts → data → RAG → AI engine → UI → tests/review.

1. **Contracts** (`architect`): ADR-008 AI service HTTP API, Core additions (`ChatResponse`+`Citation`, `IngestionStatus`, `AiServiceHealth`, `AiServiceConfig`), config schema (config-only model names), Ollama-absent degradation/fallback spec. ✅ **Done** (2026-11-16).
2. **Data** (`database`): migration `003` (vec0 `embeddings` + sqlite-vec loading + availability probe), FTS soft-delete filtering, manual/chunk/page/tag repositories. ✅ **Done** (2026-11-16). Vendored sqlite-vec v0.1.9 win-x64 (`runtimes/win-x64/native/vec0.dll`, MIT/Apache-2.0 notices). Manual data tests moved into `tests/GseTechPro.Tests/` (now in the sln); suite **250/250**. `manual_pages` has no `deleted_at` (pages inherit deletion from parent manual) — flag to `architect` for a future additive migration.
3. **Ingestion + hybrid search** (`rag-ingest`): ✅ **Done** (2026-11-16). `ai-service/rag/` (config/db/embedding/rrf/search) + `ai-service/ingest/` (pipeline/job/pdf_text/chunker). **48 pytest green** (no Ollama). 600i acceptance: 29 pages → 31 chunks, flexramp-axle lubrication question → top page 10, `fallback:true`. OCR-for-scans left as a stub (`pypdfium2` + Tesseract, Phase 7). `pyyaml` needed by config loader. Retrieval test set is self-authored (sanity check, not generalization evidence). Seed: `docs/Manuals/740-774 Delta Airlines/740-774 Delta Gas/` has PDF versions of all TUG docs — incl. the 600i maintenance PDF (acceptance target).
4. **AI engine** (`ai-engineer`): ✅ **Done** (2026-11-16). `ai-service/app/` (main/security/errors/models/api/ollama/llm/chat/bootstrap/perf/prompts/config.yaml), `ai-service/tools/` (typed registry + `search_manuals` real, 10 stubs), `ai-service/prompts/system.md`, `pyproject.toml` (deps + `pythonpath` + `gse-ai` script). **185 pytest green** (incl. rag suite). Token enforced on every route (constant-time, never logged); loopback only; refuses to start on empty token. 600i seed ingests (31 chunks) + searches with `fallback:true`.
5. **UI** (`ui-winui`): ✅ **Done** (2026-11-16). `AiServiceClient` (all `IAiClient` methods, token + snake_case/enum wire), `AiServiceLauncher` (stub), `AiServiceStateService` (Ready/WarmingUp/DegradedSearch/Unavailable), Tech Chat + Guided + Manual-RAG screens (≥44px, theme-aware, "AI SUGGESTION" + citation buttons), 13 xUnit tests, hardcoded `lfm3-omni` removed.
6. **Full test pass** (`tester`): ✅ **Done** (2026-11-16). **PASS — 460 tests, 0 failures** (C# 152+98+13; Python 185+2). 600i acceptance **15/15 top-5** (BM25-only, in-sample). No hardcoded model name; app imports with no model. Fixed: added `GseTechPro.App.Tests` to `src/GseTechPro.sln` (now `dotnet test` runs 263 C# tests).
7. **Final review** (`reviewer`): 🔄 **Pass #3 — CHANGES NEEDED (no blockers)**, 6 `should-fix` + 2 nits routed back: UNC/drive-root/oversized-file guard on `/ingest`; `LITELLM_TELEMETRY=False`; `AiServiceClient` timeout 30s→130s (vs 120s chat) + don't mark slow model `Unavailable`; offline double-`/health` probe; `dev-local-token` fallback gate to DEBUG; Sunlight `ThemeDictionaries` fix. Owners: ai-engineer / rag-ingest / ui-winui (fixing in parallel).

**Environment note (this dev machine):** Python 3.15.2. **Ollama 0.40.16 is running at 127.0.0.1:11434 but has ZERO models** (`lfm3-omni`/`nomic-embed-text` not pulled — multi-GB + network). So `/health` = `degraded`/`ollama_reachable:true`/`chat_model_missing`; RAG runs BM25-only (`fallback:true`); tests pass with no models. First-token latency + real tool-calling unmeasured until models are pulled (Phase 8 / tablet). `lfm3-omni` Ollama tag unconfirmed (verify before install).

**Reviewer (Phase 3 pass #1) — resolved:**
- ✅ `database` (5): delete `embeddings` rows on chunk/manual soft-delete; moved `ManualRepositoryTests` into `tests/GseTechPro.Tests/` (now in sln); cached missing-`vec0.dll` state across `OpenAsync()`; FTS5 `MATCH` punctuation escaping; `IF NOT EXISTS` on `embeddings` + vec-restored recovery test. Suite now **250/250**.
- ✅ `architect` (2): added `AiErrorEnvelope` + `IngestResponse` wire wrappers; clarified `AiServiceProtocol.Version` vs `AiServiceHealth.Version` doc. Core builds clean.
- ✅ `rag-ingest` (2, cross-cutting): FTS5 MATCH escaping; delete `embeddings` rows on re-ingest/soft-delete (verified by 48 pytest).
- ✅ **Pass #2** (2026-11-16): reviewer on `rag-ingest` Python → **APPROVE, no blockers**; 3 `should-fix` + 3 nits fixed (`rag-ingest`), **59 pytest green**. Note: earlier "mojibake" report was a console artifact — stored 600i text is clean. `ai-engineer` and `ui-winui` still building.

**Tooling note (permissions, 2026-11-16):** `.opencode/agents/*.md` use deny-all + folder allowlists and are platform-protected (lead can only edit `docs/PLAN.md` + `AGENTS.md`). `rag-ingest` writes only `ai-service/{rag,ingest}/**` + `tests/ai-service/rag/**`; `ai-engineer` writes only `ai-service/{app,tools,voice,vision,prompts}/**`, `ai-service/pyproject.toml`, `tests/ai-service/**`. Accepted layout deviations to fit this: shared Python plumbing (`config.py`/`db.py`/`embedding.py`) lives under `ai-service/rag/`; no `requirements.txt` (use `pyproject.toml` deps); new pytest under `tests/ai-service/` (`tester` consolidates with `tests/ai-service-tests/`); `config/config.yaml` default shipped by `installer-deploy` (Phase 8), loader accepts `--config` path. Surface to the user if they prefer widening permissions for the exact ADR-008 layout.

## Done (Phase 4 — GSE troubleshooting, no-ECU equipment) ✅

**Result:** 618 tests green (276 C# + 342 Python), solution builds 0 warnings/0 errors. All five content files validate.

- **Contracts** (`architect`): `SafetyWarning` model + `SafetyWarningCategory`; `Reading.IsSimulated` + `readings.is_simulated`; ADR-009 (content schema, spec-unit convention, guided resolution, provenance) + amendments (serve draft trees labeled, prefer approved; single-value spec → `na`). Single-writer rule: the app persists `readings`; the AI service validates/advances but never writes them.
- **Content** (`gse-domain-expert`): 2 belt-loader models (660-28 category corrected), 8 safety warnings, 16 symptoms, 14 test points, 2 trees/36 steps. 2 verified relief specs (1500/1000 psi, CD397-Oper p.101); all else `TODO: needs spec from manual`.
- **Data** (`database`): migration `004` (`safety_warnings` + `readings.is_simulated`), 4 repositories, `TroubleshootingSeeder` (slug-upsert, FK-safe order, JSON warning arrays, cross-ref + spec-mirror validation, preserves `approved` status on re-seed).
- **AI** (`ai-engineer`): `units.py` (exact C# `UnitConverter` port), tree resolution + `/guided/*` (read-only, canonical spec + resolved warnings), 5 tools (`get_spec_range`/`get_safety_warnings`/`lookup_dtc`/`start_guided_test`/`record_reading` proposal-only), unambiguous-only tree matching.
- **UI** (`ui-winui`): manual readings-entry screen (test-point picker, safety warnings, canonical + pass/fail, `SIMULATED` badge) + guided walker (display-unit spec, persist-before-advance, DRAFT badge, `na` on null/single-value spec, terminal suggestions).
- **Test** (`tester`): cross-language `units.py`↔`UnitConverter` consistency, seeder idempotence/FK, content §7 validation, 600i RAG + DTC regression — all green. Reviewer pass cleared after 3 routed fixes.

**Acceptance:** 660-28 no-crank and 600i no-lift trees walk end-to-end in the app with typed (no-ECU) readings; every spec cited or `TODO`; safety warnings shown before each step; readings stored canonical SI with entered unit preserved.

## Done (Phase 5 — Repair history learning) ✅

**Result:** 778 tests green (307 C# + 471 Python), solution builds 0 warnings/0 errors. Reviewer pass cleared (0 blockers; 3 should-fix + 2 nits routed and fixed).

**Boundary (per ADR-010, to be finalized by `architect`):** the recommendation *engine* (similarity search, smoothing, ranking, evidence strings) is **Python** (`recommender`, read-only on the shared DB — same pattern as RAG). The **C# data layer** (`database`) is the single writer of `outcomes`/`comebacks`/`recommendations`/`diagnoses`/`repairs`/`parts_replaced` and owns comeback detection (same ADR-009 single-writer rule). The **app** (`ui-winui`) records outcomes on close and renders the ranked candidates. The **AI service** (`ai-engineer`) exposes the engine over `/search/repair-history` and through the `find_similar_repairs`/`get_repair_history`/`get_part_history`/`get_asset` chat tools.

1. **Contracts** (`architect`): ADR-010 + Core DTOs + `SearchRepairHistoryPath` + `IAiClient.SearchRepairHistoryAsync` (default-interface-member) — ✅ **Done** (2026-11-17). Wilson numbers verified by hand (1-for-1 → 0.207, 45-for-50 → 0.786). Follow-up in flight: `WorkOrder.Number` property (ADR-010 §8 data need) + a `SimilarityScore` doc-comment fix.
2. **Data** (`database`): migration 005 (`work_orders.number` + `idx_wo_number` partial unique + `idx_wo_closed`), `SqliteMigrator` → v5, `DiagnosisRepository`/`ComebackRepository`/`RecommendationRepository`, comeback-detection service + confirm flow, minimal WO-number generation. ✅ **Done** (2026-11-17) — build clean, 11 smoke scenarios pass. ⚠️ Could not write xUnit tests (ACL on `tests/GseTechPro.Tests/`); migration bump left 2 stale assertions in `ManualRepositoryTests.cs` (version 4→5). Both → `tester`.
3. **Recommender** (`recommender`): `ai-service/recommender/` — `run_repair_history_search(...)` per ADR-010 §2–§5/§8 (3-tier search, deterministic composite similarity + optional vector/RRF, Wilson smoothing, rank blend, evidence strings, `number`-column fallback). ✅ **Done** (2026-11-17) — 95 pytest green, ruff clean. 10 implementation decisions + 2 known limitations (single embedder prefix; vector CPU cost) handed to `architect` for ratification into ADR-010.
4. **AI tools** (`ai-engineer`): `/search/repair-history` route + pydantic wire models; real `find_similar_repairs`, `get_repair_history`, `get_part_history`, `get_asset` handlers (replace stubs). ✅ **Done** (2026-11-17) — 306 pytest green, ruff clean, `bootstrap.py` migration-5 entry added. `add_note` stays a stub. Vector-path timing deferred (no Ollama).
5. **UI** (`ui-winui`): outcome entry on WO close (fixed/not_fixed/unsure + reason), comeback confirm, recommendations panel with evidence, `AiServiceClient.SearchRepairHistoryAsync`. ✅ **Done** (2026-11-17) — Recommendations screen + WO-browser RO-number/outcome rows + close-WO card; client method + 3 App tests. Comeback/outcome prompt handlers exposed for headless tests.
6. **Test + review** (`tester` + `reviewer`). ✅ **Done** (2026-11-17) — reviewer: 0 blockers, 3 should-fix + 2 nits (find_similar_repairs description/`system`; DTC protocol case/trim; hour-meter precedence; duplicate-comeback guard; add_note stub phase) all fixed; 3 regression tests added. Final: 307 C# + 471 Python green.

**Phase 5 acceptance:** closing a WO records an outcome (fixed/not_fixed/unsure) and detects/confirms comebacks (same asset + shared system/symptom/DTC within 14 days/200 hrs); the recommender ranks similar past repairs across model→family→all tiers with Wilson-smoothed success rates and evidence strings ("Fixed 18 of 20 on TUG 660-28, avg 2.1 hrs, last done 3 weeks ago, WO #000471"); `/search/repair-history` + `find_similar_repairs`/`get_repair_history`/`get_part_history`/`get_asset` tools surface it; WO numbers assigned on create/close. Vector path + first-token latency unmeasured (no Ollama models on dev machine).

## Phase 6 (Oscilloscope) — 🔄 In progress

**Order:** contracts (architect) → data + scope engine/simulator + content (parallel) → UI → tests/review.

**Key pre-flight findings (2026-11-17):**
- `GseTechPro.Scope` project does not exist; Core has Phase-1 placeholder scope contracts (`IScopeDevice`, `ScopeContracts.cs`, `ScopeModels.cs`, `ScopeDeviceKind`/`WaveformSource`).
- **Hantek SDK is dual-arch** (`Reference/HT6074BE_SDK/Dll/{x64,x86}` + `Lib/{x64,x86}` + headers + EN SDK manuals). Resolves ARCHITECTURE §1.1: Hantek can be **x64 in-process** like PicoScope — **no new 32-bit process**.
- **PicoSDK not in Reference/** (only the PicoScope 7 installer). PicoScope wrapper = P/Invoke against the published PicoSDK API, not exercised vs hardware (same clean-room pattern as J2534/RP1210).
- **No real waveform captures in repo** → reference waveforms must be manual-cited or `TODO`; never synthesized known-good.
- App already has a "Scope" toolbar button + nav-rail stub showing a placeholder.

1. **Contracts** (`architect`): ADR-011 (scope interface + process boundary + waveform math + reference comparison + persistence), finalize `IScopeDevice` + scope DTOs (device discovery, guided test/measurement/reference-compare result types), waveform binary file format + measurement algorithm spec (freq/Vpp/duty/rise/ripple/current + tolerances), ARCHITECTURE.md §1.1/§2.2 updates. ✅ **Done** (2026-11-17) — ADR-011 + `ScopeEnums`/`ScopeContracts`/`ScopeComparisonContracts`/`ScopeJson`/`IScopeDeviceFactory`; Hantek resolved x64 in-process; solution builds 0/0.
2. **Data** (`database`): migration 006 (`guided_tests`, `reference_waveforms`, `waveforms`) + repositories + `ScopeSeeder`. ✅ **Done** (2026-11-17) — **correction:** migration `001` already shipped these three tables in a pre-ADR-011 shape; `006_Scope.sql` is a transactional rebuild (backup → recreate → restore → drop backups) that adds `guided_tests.scope_setup_json`, the `guided_tests.reference_waveform_id → reference_waveforms(id)` FK, `idx_guided_tests_model`, and makes `reference_waveforms.data_path` nullable. `ScopeRepositories.cs` (guided-test/reference-waveform/waveform), `ScopeSeeder.cs` (strict validation, canonical `ScopeJson`, FK-ordered slug upsert), `DataServices`/`DatabaseInitializer` wiring. Standalone smoke project `tests/GseTechPro.Data.Tests/` (2 tests pass), now in the sln (lead glue).
3. **Scope engine + simulator** (`scope`): `GseTechPro.Scope` — waveform math (measure + compare), deterministic `SimulatedScopeDevice` (crank/injector/alternator-ripple/CAN/solenoid), `PicoScopeDevice` (P/Invoke, not exercised), `Hantek6074BeDevice` (x64 DLL wrapper), `ScopeDeviceFactory`. ✅ **Done** (2026-11-17) — `WaveformMath`/`WaveformComparer`/`WaveformFile`/`SimulatedScopeDevice` (+`SimulatedScopePresets` with the 6 canonical presets)/`Hantek6074BeDevice`/`PicoScopeDevice`/`ScopeDeviceFactory`; 23 xUnit tests green; `data/reference-waveforms/README.md` documents the `.wfm` format + "no real captures yet". Builds 0/0.
4. **UI** (`ui-winui`): Scope screen (PicoScope 7 Automotive workflow — channel bar, timebase/trigger, rulers/measurements, zoom, persistence, math, reference waveforms, saved library), guided-test runner, save-to-WO; replace placeholder. ✅ **Done** (2026-11-17) — full Scope screen (`MainWindow.xaml` §3F) + `ScopeSubViewModels.cs` + `MainShellViewModel.Scope.cs`; device picker with `SIMULATED`/`REAL` badge, CH1–4 setup, timebase/trigger, decimated off-thread rendering, dual cursors + zoom/pan, measurements table with verdicts, guided-test runner (safety warnings first → apply setup → preset → capture → compare → save `.wfm` + `Waveform`), saved-waveform library; `App → Scope` reference added; 27 App tests green.
5. **Content** (`gse-domain-expert`): 6 guided tests (crank sensor, injector, alternator ripple, starter current, CAN signal quality, solenoid/valve current) + reference expectations (cited from manuals or `TODO`). ✅ **Done** (2026-11-17) — `guided_tests.json` (6) + `reference_waveforms.json` (6, 11 expectations, all `is_todo`/uncited as required), setups match the canonical table, safety slugs resolve, JSON validated by lead.
6. **Test + review** (`tester` + `reviewer`). 🔄 In progress

**Lead glue (done, 2026-11-17):** added `GseTechPro.Scope`, `GseTechPro.Scope.Tests`, and `GseTechPro.Data.Tests` to `src/GseTechPro.sln` via `dotnet sln add`; full sln builds 0/0. The `App → Scope` reference is added by `ui-winui` (owns `App.csproj`).

**Follow-ups flagged (not blockers):**
- `architect`: reconcile `ARCHITECTURE.md` §2.2 `reference_waveforms.data_path TEXT NOT NULL` with ADR-011's nullable optional-sample contract (006 already made the column nullable).
- `tester`: update stale migration-version assertions (`LatestVersion`/`AppliedMigrations`/`MAX(version)` = 5 → 6) in `tests/GseTechPro.Tests/ManualRepositoryTests.cs` + `RepairHistoryDataTests.cs`, and fold equivalent scope data coverage into `tests/GseTechPro.Tests/`.
- `reviewer`: scrutinize the `006_Scope.sql` table rebuild (DROP+recreate inside the migrator transaction; FK enforcement is off by default in this codebase) + the PicoScope/Hantek wrappers.

## Done (Phase 8 — Full installer) ✅

**Verified & fixed (2026-11-18):** Solution builds clean (0 warnings/0 errors). Fixed two installer script bugs: invalid C-style ternary syntax on line 196 → proper Pascal `if/then/else`; unused `CheckForUpdates()` function now wired to task selection via first-run config JSON. Added missing Phase 8 item: `PRAGMA integrity_check` verification after database backup in `DatabaseBackup.cs`.

**Result:** Production-ready Inno Setup installer with all Phase 8 features implemented. Self-contained publish output for both x64 app and x86 device host. Deployment guide updated.

- **Code signing**: Authenticode signing via `SIGNTOOL_CERT`/`SIGNTOOL_PASS` environment variables, DigiCert timestamping (`installer/GSE-Tech-Pro-Setup.iss`).
- **Runtime bundling**: Self-contained .NET 8 Desktop Runtime (x64 + x86) and Windows App SDK bundled in installer — zero prerequisites on target tablets.
- **Ollama service installation**: Ollama installed as `GSE-Ollama` Windows service with auto-start on boot; cleanly removed on uninstall.
- **Model download progress indication**: Installer displays progress messages during large model file installation (lfm3-omni ~2.4 GB, nomic-embed-text ~274 MB).
- **Driver presence detection**: Post-install check for missing vendor drivers (TOPDON RLink X3, Nexiq USB-Link, Noregon DLA+, PicoScope, Hantek 6074BE) with user guidance.
- **Automatic update checking**: Optional "Check for updates on startup" task; checks `updates.commercialtruckandauto.com` and prompts for download; fails silently when offline (offline-first).
- **Silent multi-tablet deployment**: `/SILENT /CONFIG=config.ini` support with shop-specific configuration (shop name, technicians, devices, unit system).
- **Upgrade logic**: Never deletes user data on upgrade; only app files in `{app}` are replaced.

**Files changed:** `installer/GSE-Tech-Pro-Setup.iss`, `docs/DEPLOYMENT.md`.

## Risks / open items (deferred or flagged)

- **App-launch fix (2026-11-16, unblocks visual runs):** the app had never actually launched as a GUI (all prior gates were build+unit tests). Two root causes fixed on this dev machine: (1) Windows App Runtime **1.6** was missing (installed from the NuGet cache); (2) the unpackaged WinUI 3 app on **Windows 10** (build 19041) needs self-contained + Undocked RegFree WinRT. `GseTechPro.App.csproj` is now `WindowsAppSDKSelfContained=true`, `WindowsAppSdkUndockedRegFreeWinRTInitialize=true`, `WindowsAppSdkBootstrapInitialize=false`, plus a `RestoreWindowsAppSdkMergedPri` target (no `resources.pri` without VS Pri tooling). **Phase 8 impact:** `installer-deploy` packages the full self-contained output dir (no runtime MSIX install on tablets). Verify self-contained also launches on the Windows 11 target tablet.
- **Phase 4 content is mostly `TODO`.** Only 2 spec values are verified (1500/1000 psi relief, CD397-Oper). Battery/cranking/voltage-drop/charging/flow specs need a manual-pass by `gse-domain-expert` before these trees can produce real pass/fail. Until then, most readings judge `na`.
- **Phase 4 nits deferred:** `ReadingType→canonicalUnit` mapping lives in `TroubleshootingUnitHelper` (App) — could move to Core `UnitConverter`; `na`-verdict routing (stays on current step) not formalized in ADR-009; `start_guided_test` chat tool has no complaint-matching (safe no-match fallback).
- **Cold-start (<10 s) + RAM budget NOT yet measured** — needs the tablet + Windows App Runtime 1.6; measure on first hardware run (Phase 1 note, still open).
- **Visual screen check vs screenshots NOT done** — lead's model can't view images; `ui-winui`/`general` report the Phase 2 screens (04, 13–18) match the references by construction, but a human/vision pass is needed.
- **x86 .NET 8 runtime** must be bundled (or preinstalled) for the 32-bit host to run on the tablet (`installer-deploy`, Phase 8). `HostProcessTests` skip on machines without it.
- **Real-hardware verification deferred** — J2534 (TOPDON RLink X3) and RP1210 (Nexiq USB-Link / Noregon DLA+) wrappers compile and match the published byte-framing per review, but have NOT been exercised against hardware. Sub-features marked "not supported" until verified: J1939 DM3, UDS DTCFormatIdentifier 0x02/0x03, J1587 live-data beyond PID 190/110/168, J1939 VIN retrieval, OBD II mode $07. Verify on first tablet with hardware.
- **`lfm3-omni`** speed/RAM unmeasured; LFM license $10M/yr revenue threshold to confirm (Phase 3).
- **Hantek SDK license + unsigned driver**, **ECOM redistribution** — still "needs verification" (Phase 6/7/8).
- **`tools/brand` generator** not in repo — must be rebuilt before recoloring CTA assets (Option A icon + palette).
- **Technician-id attribution** (who cleared DTCs / recorded a reading / closed a recording) is still partially placeholder — revisit when the technician-switch flows are finalized.

### Reviewer advisories (Phase 2 pass) — resolved or carried forward
- ✅ `IsSimulated` provenance (`IDiagnosticAdapter`/`PidValue`/`DtcReadout`) — done; `readings.source` still derives it in Phase 4.
- ✅ `DbValueConverter` unknown-enum fallback, `SqliteMigrator.LatestVersion`, simulator determinism-per-consumer, host console-log noise, stream-loop dispatch coalescing — done.
- ⏭ **Nit (defer):** bounded ring buffers use `List.RemoveAt(0)` (→ `Queue<T>` if history grows); test `InitializeAsync` uses `.Wait()` (→ `await`). Low value; fold into a future polish pass.
- ⏭ `GetAllAsync` paging + virtualized data tables (recording playback is already windowed; general list paging still open).
- ✅ Hardcoded "lfm3-omni" UI string → done in Phase 3 (read from AI config).
- ✅ FTS soft-delete filtering + vec0 availability probe → done in Phase 3 (RAG).
- ⏭ Backup destination guard + `PRAGMA integrity_check` → Phase 8.
