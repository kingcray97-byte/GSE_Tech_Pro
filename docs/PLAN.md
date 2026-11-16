# GSE Tech Pro — Build Plan

> Owned by `gse-lead`. This is the living roadmap: what's done, what's next, and risks.
> The architecture-level phase map lives in `docs/ARCHITECTURE.md` §8. This file tracks granular tasks.
> A clean session resumes from `AGENTS.md`, `docs/PROMPT.md`, this file, and `docs/ARCHITECTURE.md` alone.

## Status

- **Architecture: APPROVED** (2026-11-15). `docs/ARCHITECTURE.md` + 6 ADRs in `docs/decisions/`.
- **Approved decisions:** unpackaged + Inno Setup (001), BitLocker at-rest (002), `nomic-embed-text` (003), separate GSE Tech Pro icon — Option A (004), permissive PDF stack, no PyMuPDF (005), local-first AI + opt-in cloud fallback (006).
- **Feature inventory expanded** from the installed Shop Foreman Pro's help file + readme (clean-room; no binaries). User decisions: Mitchell1 repair-info → later, 5-gas → drop, NVH → drop, Bosch MTS hardware → drop.
- **Phase 1: DONE.**

## Phases

| # | Phase | Status |
|---|---|---|
| 0 | Architecture & ADRs | ✅ Done |
| 1 | Shell, data, build pipeline | ✅ Done |
| 2 | Diagnostics (device abstraction, J2534/RP1210, J1939/J1708/OBD II, Equipment Health, live data, recording) | ⬜ Next |
| 3 | Manuals & AI (ingestion, hybrid search, Ollama + model, Tech Chat, guided troubleshooting) | ⬜ |
| 4 | GSE troubleshooting (readings entry, decision trees, safety warnings — TUG 660-28 first) | ⬜ |
| 5 | Repair history learning (recommendation engine, outcomes, comebacks) | ⬜ |
| 6 | Oscilloscope (PicoSDK, then Hantek, shared interface) | ⬜ |
| 7 | Vision & voice, ECOM/CANCapture | ⬜ |
| 8 | Full installer (signing, silent multi-tablet, upgrades, updates) | ⬜ |
| 9 | Optional shop-server sync | ⬜ |

## Done (Phase 1)

**Solution:** `src/GseTechPro.sln` — 6 projects, builds clean (0 warnings/errors): Core, Data, Devices, Devices.Host32 (x86), App (WinUI 3), Tests.

- **Core** (`architect`): 34 entities, enums + `DbEnum` codec (now cached + `TryParse`), `IDiagnosticAdapter`/`IScopeDevice`/`IAiClient`, diagnostic/AI/scope DTOs, `UnitConverter`/`ReadingValue` (canonical SI + preserved entered unit), `Clock.UtcNow` (Z-suffixed).
- **Data** (`database`): SQLite schema (36 tables incl. FTS5), versioned migrations, 16 repositories, backup, composition root; sqlite-vec deferred (Phase 3).
- **Devices** (`device-comms`): `SimulatedDiagnosticAdapter` (J1939/J1708/OBD II, seeded/deterministic, canonical SI), `DiagnosticAdapterFactory`; `GseTechPro.Devices.Ipc` (PipeMessage/HostContracts); x86 host32 named-pipe shell (crash-hardened, `CurrentUserOnly`, per-session name).
- **App** (`ui-winui`): WinUI 3 shell matching screenshots 01/03/04 (menu bar, toolbar, tabs, nav rail, breadcrumb, status bar), theme resources (light/dark/sunlight, accent #2E3391/#8C93F0), simulator flow (DTCs red/blue, live data, freeze frame), work-order/asset lists; clear-DTCs confirmation + audit, unit canonicalization at display, stale-row clearing, async-void hardening.
- **Installer** (`installer-deploy`): Inno Setup skeleton (`GSE-Tech-Pro-Setup.iss`), `config-template.ini`, `docs/DEPLOYMENT.md`.
- **Tests** (`tester`): xUnit (133) + pytest (2) all passing.

## Next (Phase 2 — order of work)

1. **Device abstraction** (`device-comms`): real J2534 (TOPDON RLink X3) + RP1210 wrappers, J1939 (DM1/DM2, DM11 clear, SPN/FMI, PGN live data, TP), J1708/J1587, ISO 15765/UDS, OBD II (J1979); app↔host32 IPC client + per-session pipe-name handoff + token.
2. **Equipment Health + live data + recording/playback** (`ui-winui`): full screens (04, 13–18), VCR playback, triggers.
3. **DTC/SPN/FMI data files** (`gse-domain-expert`): `data/dtc/` (real published codes; unknown → marked).
4. **Tests** (`tester`) + **review** (`reviewer`) per change; real-hardware where present.

## Risks / open items (deferred or flagged)

- **Cold-start (<10 s) NOT yet measured** — needs the tablet + Windows App Runtime 1.6; measure on first hardware run.
- **Visual screen check vs screenshots NOT done** — lead's model can't view images; `ui-winui` reported layout matches 01/03/04 by construction, but a human/vision pass is needed.
- **x86 .NET 8 runtime** must be bundled (or preinstalled) for the 32-bit host to run on the tablet (`installer-deploy`, Phase 8).
- **App↔host32 pipe-name handoff** mechanism (per-session GUID name) needs a contract for the Phase 2 IPC client (`architect` + `device-comms`).
- **`lfm3-omni`** speed/RAM unmeasured; LFM license $10M/yr revenue threshold to confirm (Phase 3).
- **Hantek SDK license + unsigned driver**, **ECOM redistribution** — still "needs verification" (Phase 6/7/8).
- **`tools/brand` generator** not in repo — must be rebuilt before recoloring CTA assets (Option A icon + palette).

### Reviewer advisories intentionally deferred (revisit in the phase noted)
- `IsSimulated` provenance on `IDiagnosticAdapter`/`PidValue`/`DtcReadout` → Phase 2 (real adapters) / Phase 4 (`readings.source`).
- `DbValueConverter` graceful unknown-enum handling → Phase 2.
- `GetAllAsync` paging, virtualized data tables, simulator determinism-per-consumer, stream-loop allocation + dispatch coalescing → Phase 2 (perf).
- Sunlight-mode file I/O + theme restore, theme-resource dedup → Phase 2 (UI polish).
- Hardcoded "lfm3-omni" UI string → Phase 3 (read from AI config).
- FTS soft-delete filtering + vec0 availability probe → Phase 3 (RAG).
- Backup destination guard + `PRAGMA integrity_check` → Phase 8.
- Misc (`SqliteMigrator.LatestVersion` unused, technician-id attribution, host console-log noise) → Phase 2.
