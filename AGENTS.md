# GSE Tech Pro — Project Rules

These rules apply to every agent working in this repo. The full product spec is in `docs/PROMPT.md`. Reference screenshots of Shop Foreman Pro (the app we are remaking) are in `docs/reference-screenshots/`.

## What we're building

An offline-first Windows 11 diagnostic and troubleshooting app for Ground Support Equipment (GSE) and heavy-duty/fleet vehicles. Shop Foreman Pro's layout with Windows 11 styling, a local AI (Ollama + `lfm3-omni`), PDF manual search (RAG), J2534/RP1210 diagnostics, oscilloscopes, fleet repair history, and a self-learning repair recommendation engine. Target device: Dell Latitude 7220 Rugged Tablet (8th-gen Intel CPU, 8–16 GB RAM, no GPU, touch, gloves).

## Tech stack

| Part | Tech |
|---|---|
| UI | WinUI 3 (Windows App SDK), .NET 8+, C#, MVVM (CommunityToolkit.Mvvm) |
| Device services | C# (.NET 8), plus a **32-bit** helper process for J2534/RP1210 DLLs |
| AI service | Python 3.12, FastAPI, Ollama, LiteLLM, local only |
| Database | SQLite + FTS5 + sqlite-vec |
| Installer | Inno Setup (or WiX), producing `GSE-Tech-Pro-Setup.exe` |
| Tests | xUnit (C#), pytest (Python) |

## Folder structure and owners

Each folder has one owning agent. Only the owner edits it, unless `gse-lead` assigns otherwise.

| Path | Owner |
|---|---|
| `docs/ARCHITECTURE.md`, `docs/decisions/` | `architect` |
| `docs/PLAN.md`, `AGENTS.md` | `gse-lead` |
| `src/GseTechPro.App/` (WinUI screens, styles, view models) | `ui-winui` |
| `src/GseTechPro.Core/` (shared models, interfaces) | `architect` (others propose changes) |
| `src/GseTechPro.Data/` (SQLite, migrations, repositories) | `database` |
| `src/GseTechPro.Devices/`, `src/GseTechPro.Devices.Host32/` | `device-comms` |
| `src/GseTechPro.Scope/` | `scope` |
| `ai-service/app/`, `ai-service/tools/`, `ai-service/voice/`, `ai-service/vision/` | `ai-engineer` |
| `ai-service/rag/`, `ai-service/ingest/` | `rag-ingest` |
| `ai-service/recommender/` | `recommender` |
| `data/troubleshooting/`, `data/dtc/`, `data/specs/` | `gse-domain-expert` |
| `installer/`, `docs/DEPLOYMENT.md` | `installer-deploy` |
| `tests/` | `tester` (owners may add tests for their own code) |

## Rules for all agents

1. **Offline first.** Nothing may require the internet at runtime except optional sync and updates. No cloud APIs, no telemetry.
2. **Read before you write.** Check `docs/ARCHITECTURE.md`, `docs/PLAN.md`, and `src/GseTechPro.Core/` contracts before starting.
3. **Stay in your folders.** If you need a change elsewhere, say so in your handoff instead of making it.
4. **Simulator first.** Every device feature must work against the simulator before real hardware.
5. **No made-up specs.** Never invent pressures, voltages, torque values, part numbers, or procedures. Real values come from a manual (cite manual + page) or are marked `TODO: needs spec from manual`.
6. **Safety.** The AI and troubleshooting steps must show safety warnings (stored hydraulic pressure, lockout/tagout, high voltage, aircraft proximity) and never skip manufacturer procedures.
7. **Tablet limits.** Assume CPU only and 8 GB RAM. Keep memory use low and never block the UI thread.
8. **Touch.** All controls at least 44x44 px, usable with gloves. Keyboard and mouse must still work.
9. **Small changes.** One feature per change, building and passing tests.
10. **No git push**, no deleting user data, no changes to installer signing keys.
11. **Clean-room only.** Use published standards and licensed vendor SDKs. Never decompile or reverse-engineer vendor binaries (Bosch, NEXIQ, Pico, Hantek, TOPDON, OEMs). `Reference/` is for reading only and is never bundled.
12. **No third-party assets.** Don't ship Shop Foreman Pro, Pico, or vendor icons/artwork, or the images in `docs/logos/`. Use Segoe Fluent Icons, our own icons, and `brand/`.
13. **Ignore `docs/PROMPT.original.md`.** It is an old draft. The spec is `docs/PROMPT.md`.

## Handoff format (every subagent ends with this)

```
## Done
- what changed (files)
## How to check it
- commands to build/test, or screens to open
## Not done / risks
- anything left, assumptions, TODOs
## Needs from other agents
- requests for other owners (or "none")
```
