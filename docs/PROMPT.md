# GSE Tech Pro — Product Spec

> How to start a build session with this spec is in `README.md`. This file is the spec only.
> Reference screenshots of Shop Foreman Pro: `docs/reference-screenshots/` (`01`–`29`).

I want to build **GSE Tech Pro**, an offline-first diagnostic and troubleshooting app for **Ground Support Equipment (GSE)** and heavy-duty/fleet vehicles. It's a modern remake of **Shop Foreman Pro** (Bosch, version 5.10, about 2012), with built-in local AI, oscilloscope support, and fleet repair history.

Before writing any code, give me your recommendations for:
1. Software architecture
2. Database design
3. Folder structure
4. User interface
5. AI integration
6. Future scalability

Use the defaults I suggest below unless you have a better reason. If you change one, tell me why.

## Ground rules

- **Offline first.** Every feature except optional sync, updates, and queued email works with no internet. No cloud APIs, no telemetry.
- **No made-up specs.** Never invent pressures, voltages, torque values, part numbers, or procedures. Real values come from a manual (cite manual + page) or are marked `TODO: needs spec from manual`. This applies to code, seed data, decision trees, and AI answers.
- **Clean-room only.** Build on published standards (SAE J2534, TMC RP1210, SAE J1939, J1708/J1587, J1979, ISO 15765, ISO 14229) and vendor SDKs we are licensed to use. Do **not** decompile, disassemble, or reverse-engineer vendor binaries (Bosch, NEXIQ, Pico, Hantek, TOPDON, or any OEM). Installed vendor apps are references for workflow and layout only.
- **No third-party assets.** Copy Shop Foreman Pro's layout and workflow, not its artwork, icons, names, or text. Do not use Pico's or Bosch's icons. The images in `docs/logos/` (vehicle-make logos, `controller_*.png`) are for reference only and must not ship. Vehicle-make logos are trademarks.
- **License check.** Record the license of every third-party component (Ollama, the AI model, embedding model, PicoSDK, Hantek SDK, RP1210/J2534 vendor drivers, OCR engine, Python packages) in `docs/decisions/`, with a note on whether it may be bundled in the installer or must be installed separately by the user. Check the LFM model license in particular (Liquid AI's open license has commercial/revenue limits).

## Target device and deployment

- **Device:** Dell Latitude 7220 Rugged Tablet running **Windows 11**. Plan for: Intel 8th-gen Core i5/i7 CPU, **8–16 GB RAM, no dedicated GPU**, ~12" 1920x1080 touchscreen, used outdoors on the ramp, often with gloves, sometimes with a keyboard dock.
- **Performance budget (8 GB tablet):**
  - App + AI service + loaded model together under about **5 GB RAM**; the model unloads after an idle timeout
  - Never block the UI thread; cold start to the main window in under about 10 seconds (propose a number and measure it)
- **Deployment:** a single signed Windows installer, `GSE-Tech-Pro-Setup.exe`, that installs everything on a fresh tablet: app, runtimes, local AI engine and model, database, and the default manual library. It must support **silent install** (e.g. `/SILENT` with a config file) so we can deploy to many tablets by USB or Intune. Include upgrade (keeping user data), uninstall, and a first-run setup wizard (shop name, technicians, connected devices).
- Suggested installer: **Inno Setup** or **WiX**, plus a short deployment guide for IT.
- Decide early whether the WinUI 3 app is **packaged (MSIX) or unpackaged**, because it affects the installer, updates, and Mica/notifications. Record the decision.

## Look and feel

Copy Shop Foreman Pro's **layout and workflow**, restyled with **Windows 11 colors and icons**. Screenshots of the original are in `docs/reference-screenshots/` (`01`–`29`). Shop Foreman Pro is also installed on this system (program files copied to `Reference/Shop Foreman Pro/`) for additional reference.

**Keep from the original** (see `01-main-window-layout.jpg`, `03-ro-browser.jpg`, `04-vehicle-health.jpg`):
- Menu bar, then a main toolbar with icon + label buttons: RO List / Work Orders, New, Open, Search, Save, Print, Home, Notes, **Current Technician** drop-down, then tool buttons (J2534, Heavy Duty, OBD II, Scope, CAN Capture, Camera, **AI Tech Chat**)
- Tabs for open documents (one per work order/equipment visit)
- Vertical toolbar on the left whose buttons change with the active tool (Back, Live Data, Refresh, DTCs, Freeze Frames, Current Data, Readiness, Monitors, Data Format…)
- Breadcrumb navigation at the top of the content area (`Equipment Selection >> Main Menu >> Equipment Health`)
- Dense data tables with Value/Units columns. DTCs in red for active/confirmed and blue for pending/inactive.
- Status bar at the bottom with device, protocol, and connection state
- Work order browser: technician tree on the left, sortable work order grid, preview pane below with labeled rows (Work Order, Equipment, Symptoms, Service Notes, Operator/Customer, Technician, Shop Info)

**Feature inventory (deliverable):** after examining Shop Foreman Pro and the screenshots, put a table in `docs/ARCHITECTURE.md` of every Shop Foreman Pro feature **not** listed in this spec, each marked *keep / drop / later* with your recommendation. Wait for my answers before building them.

**Change to Windows 11 style:**
- Fluent Design: Mica/Acrylic backgrounds, Segoe UI Variable font, **Segoe Fluent Icons** (plus our own icons where none fits), rounded corners
- **Branding:**
  - **Product name:** **GSE Tech Pro**. Use it in the title bar, splash, installer, Start menu, and About box.
  - **Company:** **Commercial Truck and Auto** (CTA). It is the publisher and appears as "by Commercial Truck and Auto" on the splash, the About box, the installer publisher field, and code-signing details. (The customer's shop name from the first-run wizard is separate and appears on work orders and printouts.)
  - **Colors:** GSE Tech Pro's own Windows 11 palette comes first. Propose an app accent color (light, dark, and sunlight/high-contrast variants) that suits Fluent Design and reads well outdoors. The CTA logo's colors (currently blue **#2E3391**, from `brand/`) **may be changed to match the app**; recolor the logo, `cta.ico`, and splash to the chosen palette rather than bending the app to the logo.
  - **Assets:** the CTA logo source is `brand/source/logo.jpg`. The files in `brand/` are generated (see `brand/README.md`); regenerate them instead of hand-editing. The `tools/brand` generator mentioned there is not in this repo yet, so recoloring needs it rebuilt or added first.
  - Decide whether the app icon is the CTA mark or a separate GSE Tech Pro icon (with the CTA logo on the splash/About only), and show me both options.
- Light and dark mode that follow Windows settings, plus a **high-contrast sunlight mode** for outdoor use
- The left vertical toolbar becomes a Fluent navigation rail but keeps the same buttons and order
- **Touch-first:** minimum 44x44 px tap targets, glove-friendly, pen support for annotating, on-screen keyboard friendly. Everything must also work with keyboard and mouse.
- **Units:** per-shop default of imperial or metric (psi/bar, °F/°C, miles/km), switchable per reading. Store values in one canonical unit and keep the unit the tech entered.

## Hardware and protocols

**Interfaces to support:**
- **SAE J2534** pass-thru (04.04, and 05.00 if available), tested with the **TOPDON RLink X3** (driver: `Reference/Drivers/RLink_Platform_V3.60.007.exe`)
- **TMC RP1210** adapters for heavy duty (e.g. Nexiq USB-Link, Noregon DLA+, and similar; Nexiq driver: `Reference/Drivers/USB-Link3_v3_6_0_5.exe`)
- **Enovation Controls ECOM v2 cable** through its published API, to talk to EControls engine controllers on GSE/industrial engines. API docs and C#/C++ examples: `Reference/GSE diagnostic apps/ECOM/`. Import logs from **CANCapture** where possible (`CanCapture-V2.55.zip` in the same folder).
- OEM/manufacturer protocols Reverse-engineer proprietary DLLs and protocols to work with j2534. If its not possible mark them as not supported.

**Protocols:**
- SAE **J1939** (CAN 2.0B, 29-bit), including DM1/DM2 active and stored faults, DM11 clear, SPN/FMI decoding, and PGN live data
- SAE **J1708 / J1587** (RS-485 physical layer) for older heavy-duty equipment. This needs an RP1210 adapter that supports J1708; most J2534 devices (including the RLink X3) do not.
- **ISO 15765** (CAN for OBD/UDS) and ISO 14229 UDS where supported
- **HD-OBD** (SAE J1939-13 connector / J1939-73 diagnostics) and light-duty **OBD II** (SAE J1979). NEXIQ eTechnician (`C:\Program Files (x86)\NEXIQ\eTechnician`, installer in `Reference/`) is a workflow reference only.
- A device-abstraction layer, so every screen works the same regardless of which adapter is connected

**Note:** many J2534 and RP1210 vendor drivers are 32-bit only. Plan for a separate 32-bit helper process that loads those DLLs and talks to the main app over local IPC (e.g. named pipes).

**Simulator mode:** fake J1939, J1708, OBD II, and scope data so everything can be built and demoed without equipment. Support replaying recorded CAN logs as a simulator source for tests.

## Oscilloscope

- Workflow reference: PicoScope 7 Automotive (`C:\Program Files\Pico Technology\PicoScope 7 Automotive Early Access`, installer in `Reference/`).
- Supported hardware: **PicoScope automotive scopes** (via Pico's official PicoSDK) and **Hantek** USB scopes (via Hantek's SDK in `Reference/HT6074BE_SDK/`; device files in `Reference/Hantek 6074BE/`)
- **One scope interface for both brands.** Lay it out like the PicoScope 7 Automotive workflow techs already know: channel setup bar, timebase/sample controls, trigger controls, rulers/measurements, zoom, persistence mode, math channels, reference waveforms, saved waveform library. Use GSE Tech Pro's own name, Windows 11 styling, and our own icons.
- Guided tests library (e.g. crank sensor, injector, alternator ripple, starter current, CAN bus signal quality, solenoid/valve current) with known-good reference waveforms. Reference waveforms come from real captures or manuals, never synthesized "known good" data.
- Save waveforms into the work order.
- **Waveform comparison:** compute measurements in code (frequency, peak/peak-to-peak, duty cycle, rise time, ripple, current draw) and compare them numerically to the reference. The AI only explains the result; it does not judge the raw image.

## Features carried over from Shop Foreman Pro

- **Event file per visit** (now a Work Order) with a summary tab and an **Integrated Diagnostics** tab holding all records: snapshots, DTCs, recordings, waveforms, photos, notes
- **Equipment/Vehicle Health** screen: DTCs, freeze frames, current data, readiness/monitors, all on one screen
- Live data views: list, custom list, bar, digital meter, analog gauge, line graph, with a custom display builder, cursors, and ranges
- **Recording and playback** with triggers and a VCR-style playback bar
- Clear codes with confirmation and warnings
- Notes: work order notes, record notes, frame notes, technician notes
- Camera capture and **photo annotation** (pen-friendly)
- Search, print, export, email (email queues until online)
- Users/technicians, switchable from the toolbar, with an optional per-technician PIN

## Fleet features

- **Vehicle database** and **Equipment database** (tugs, belt loaders, GPUs, air start units, pushback tractors, deicers, lavatory/water trucks, loaders, etc.): make, model, serial, engine, year, configuration
- **Asset tracking:** asset number, location/station, status (in service / down / awaiting parts), hour meter, mileage
- **Repair history** per asset
- **Part replacement history:** part number, description, date, hours/miles at install, vendor, warranty
- **Technician notes** per asset and per work order
- PM/maintenance schedules by hours, miles, or calendar (nice-to-have)

## GSE troubleshooting (equipment with no diagnostic port)

Much GSE has no ECU, or the ECU can't be read. The app must still troubleshoot using:
- **Symptoms** (pick lists plus free text)
- **Operator complaints** (in the operator's own words, typed or spoken)
- **Hydraulic readings:** pressure (psi/bar), flow, temperature, at named test ports
- **Voltage readings:** battery, charging, sensor, ground drop
- **Fuel pressure readings**
- Scope captures, DTCs (when available), and **maintenance history**

**Areas:** engine, hydraulic, electrical (plus drivetrain/brakes/HVAC later).

**Guided troubleshooting:** step-by-step decision trees per equipment model and symptom. At each step the app asks the tech for a reading, shows the expected spec range from the manual (with citation, or `TODO: needs spec from manual`), marks it pass/fail, and picks the next step. Show the relevant safety warnings (stored hydraulic pressure, lockout/tagout, high voltage, aircraft proximity) before the step. Trees can be written by hand, generated by the AI from manuals (with a tech approving them), and improved from repair history.

**First target equipment:** the **TUG 660-28 / 600i** (Delta Airlines units). Its full manual set is in `docs/Manuals/` and is the first equipment model, the first decision trees, and the RAG acceptance test.

## Local AI

**Engine and model:**
- **Ollama**, running locally and installed by the setup program
- Model: **`lfm3-omni`** (Liquid AI LFM3-Omni-2B, ~2.4 GB): text, image, and audio input; text or speech output. **Before committing to it**, confirm it is available for Ollama, check its license, and measure speed and RAM on an 8th-gen i5 with 8 GB. Name a fallback model in case it fails any of these.
- Access through **LiteLLM** (or the Ollama Python SDK) so the model can be swapped later (e.g. a bigger model on a shop server) by changing config only. No model names hardcoded in code.
- A local embedding model for search (pick one that runs well on CPU via Ollama)
- Be honest about the 2B model's limits: keep prompts short, lean on retrieval, and design tools so the model looks things up instead of guessing. Use deterministic code (OCR, parsers, measurements, lookups) for anything that must be exact; the model explains and connects.

**AI features:**
- **Technician Chat button** on the main toolbar, always available and aware of the open work order and equipment
- Guided troubleshooting and **repair recommendations**
- **Previous failure lookup** and **similar repair** matching
- **Service procedure retrieval** from stored manuals, with page references
- **DTC analysis** (J1939 SPN/FMI, OBD II, manufacturer codes)

**Skills/tools to build for the model:**
1. **Local RAG & Hybrid Vector Search:** search manuals and repair history with full-text search (BM25) plus vector search, merged and re-ranked
2. **Multimodal Vision & Manual Visual Inspection:** the tech takes a photo (leak, worn part, damaged harness, gauge reading, data plate) and the model describes what it sees and matches it to manual diagrams. **Serial numbers and data-plate text are read with offline OCR** (e.g. Windows.Media.Ocr or Tesseract), not by the model, and the tech confirms them.
3. **Voice-First / Hands-Free Tech:** push-to-talk (big button, plus a configurable hardware button on the 7220) for gloved hands. Speech in, spoken answers out. Use lfm3-omni's audio if quality and speed are good enough on CPU; otherwise local STT (e.g. whisper.cpp) and TTS (e.g. Piper). Voice commands like "next step", "reading is 2,300 psi", "add note", "what's the spec for…". Readings heard by voice are read back for confirmation before they are saved.
4. Tools for: look up the asset, get repair history, get part history, get spec/expected range, record a reading, create a note, start a guided test, search DTC library
- Build any other skills/tools the model needs, and describe each one

**AI safety rules:**
- Every recommendation **cites its source** (manual + page, or past work order number)
- Never contradict or skip manufacturer procedures or safety warnings. Show safety warnings (stored hydraulic pressure, lockout/tagout, high voltage, aircraft proximity) before related steps.
- Clearly mark AI output as suggestions. The tech makes the final call.
- If the answer isn't in the manuals or history, say so instead of guessing. Never invent a spec value.

## Repair manuals (ingestion)

- Import repair manuals (single files or whole folders), including **scanned PDFs (OCR)**
- Supported formats: **PDF** first. Also import `.doc`/`.docx`/`.ppt`/`.pptx` by converting them offline to PDF (e.g. bundled LibreOffice headless, or skip them when a PDF version of the same document exists). Ignore `Thumbs.db`, `~$*` lock files, and other non-documents.
- Automatically: extract text, tables, and diagrams; detect sections (specs, troubleshooting, procedures, wiring, torque values); split into chunks with page numbers; create embeddings; tag by manufacturer/model/system
- Store everything in the database, searchable by keyword and by meaning
- Manual viewer that opens the PDF to the exact cited page
- Bulk import on the shop computer, then push to tablets (works with the multi-tablet installer)
- **Seed library:** `docs/Manuals/` (TUG 660-28 / 600i maintenance, user, parts, and wiring manuals plus the `DRW/` drawings; Textron GSE parts catalog; MSG425 schematic; Ford 2.5L DTCs; 4L70E TCM; how to read schematics). Use it for ingestion tests: e.g. a question about the 600i maintenance manual must return the right page.

## Repair history learning (recommendation engine)

Design a self-learning repair history database:
- Every closed work order records: equipment, symptoms, complaint, readings, DTCs, diagnosis, repair done, parts replaced, labor time, technician, and **outcome**
- **Outcome tracking:** at close the tech marks "fixed / not fixed / unsure". The system also detects **comebacks** automatically (same asset, same system or symptom, within a set number of days or hours) and marks the earlier repair as failed.
- When a new diagnosis starts, the system:
  1. Searches for similar past failures (same model first, then the same equipment family, then everything), matching on symptoms, readings, DTCs, and text similarity
  2. Suggests the repairs that worked before
  3. **Ranks repairs by success rate**, weighted by similarity and recency, with smoothing so a 1-for-1 record doesn't outrank 45-for-50
  4. Shows the evidence ("Fixed 18 of 20 times on TUG MA-50, avg 2.1 hrs, last done 3 weeks ago, WO #4471")
- Provide the **database tables** and the **AI workflow** (diagram + steps) for this

## Data, logging, and security

- **Backup and export:** one-click database backup to USB/folder and restore; export a work order or an asset's history (PDF and a data format such as JSON/CSV). Upgrades never delete user data.
- **Logging and crash reports** kept locally with a size/age limit; an "export logs" button for IT.
- Local data stays on the tablet unless sync is turned on. Propose whether the database needs encryption at rest (e.g. BitLocker is enough vs. SQLCipher).

## Suggested architecture (change it if you have a better idea)

- **UI:** WinUI 3 (Windows App SDK) with .NET 8+ for a native Windows 11 look, touch, and pen
- **Device services:** C# services with a 32-bit helper process for J2534/RP1210 DLLs, plus the ECOM, PicoSDK, and Hantek SDK wrappers
- **AI service:** local Python service (FastAPI) running RAG, ingestion, LiteLLM/Ollama, speech, and tools, bundled with an embedded Python runtime
- **Database:** **SQLite** per tablet, with FTS5 for full-text and **sqlite-vec** for vector search. Manual files stored on disk and referenced from the database.
- **Multi-tablet (later):** optional sync to a shop server (e.g. PostgreSQL + pgvector) when on Wi-Fi, so repair history learning uses the whole fleet's data. Tablets must work fully without it.
- Auto-update channel that also works from a USB stick

## What I want from you first

1. Your recommendations for the 6 areas at the top
2. Database design: tables and fields for assets, equipment models, work orders, symptoms, readings, DTCs, repairs, parts, outcomes, technicians, manuals, chunks, and embeddings
3. Folder structure for the whole solution (UI, device services, 32-bit helper, AI service, installer, tests, docs)
4. The AI workflow for troubleshooting and the recommendation engine
5. The Shop Foreman Pro feature inventory (keep / drop / later)
6. The third-party license table and the AI model check (availability, license, speed, RAM, fallback)
7. A phased build plan, written to `docs/PLAN.md`

## Phased build plan (suggested)

1. **Shell, data, and build pipeline:** main window with the Shop Foreman Pro layout in Windows 11 style, database, work orders, equipment/asset database, simulator mode. Also a **skeleton installer** that installs the app, the embedded Python runtime, and Ollama on a clean machine, so packaging problems show up early. Check screens against the screenshots.
2. **Diagnostics:** device-abstraction layer, J2534 (RLink X3), RP1210, J1939/J1708/OBD II, Equipment Health, live data, recording/playback
3. **Manuals and AI:** ingestion (seed library first), hybrid search, Ollama + model, Tech Chat, guided troubleshooting
4. **GSE troubleshooting** for no-ECU equipment, readings entry, decision trees (TUG 660-28 first)
5. **Repair history learning** and the recommendation engine
6. **Oscilloscope:** PicoScope, then Hantek, with a shared interface
7. **Vision and voice** skills, ECOM/CANCapture support
8. **Full installer** (`GSE-Tech-Pro-Setup.exe`): signing, silent multi-tablet deployment, upgrades, updates
9. Optional shop server sync

**Definition of done for each phase:**
- Builds cleanly; xUnit and pytest suites pass
- Every feature in the phase works in simulator mode (and on real hardware where we have it), with written test steps
- Screens checked against the matching reference screenshots and the touch rules (44x44 px, gloves, sunlight mode)
- Stays inside the performance budget on an 8 GB tablet
- `docs/PLAN.md` updated: what's done, what's next, risks

After each phase, show me screenshots and how to test it on the tablet.

**Clean sessions:** keep `docs/PLAN.md` and each agent's handoff (format in `AGENTS.md`) up to date so a brand-new session can resume from those files alone, without the old conversation. Keep context small by working one phase task at a time.
