# GSE Tech Pro — Architecture

> Status: **draft for user review** — no code written yet. Owned by `architect`. Companion documents: `docs/decisions/` (ADRs), `docs/PLAN.md` (owned by `gse-lead`), `docs/DEPLOYMENT.md` (owned by `installer-deploy`).

This document is the single source of truth for process boundaries, data, folder layout, UI, AI, licensing, and the build order. It answers the six "before writing code" questions from `docs/PROMPT.md`, gives the full database design, the AI workflows, the Shop Foreman Pro feature inventory, the third-party license table, and the phased plan.

**Hard constraints (repeated here because every section is written against them):**
- Offline first. Nothing needs the internet at runtime except optional sync, updates, and queued email. No cloud APIs, no telemetry.
- Target device: Dell Latitude 7220 Rugged Tablet, Windows 11, 8th-gen Core i5/i7, **8–16 GB RAM, no GPU**, 12″ 1920×1080 touch, used outdoors with gloves.
- Performance budget: app + AI service + loaded model **under ~5 GB RAM**; model unloads after idle; UI thread never blocked; cold start to main window < ~10 s (measure in Phase 1).
- No made-up specs. Every value in examples is `TODO: needs spec from manual` or cited.
- Clean-room only. No decompiling vendor binaries. `Reference/` is read-only reference and never shipped.
- No third-party assets (Shop Foreman Pro / Pico / Bosch / vehicle-make logos). Segoe Fluent Icons, our own icons, `brand/` only.

---

## 1. Six recommendations

### 1.1 Software architecture — process boundaries and IPC

Four long-lived processes, one per responsibility, so that a 32-bit vendor DLL, a Python AI stack, and a native UI never share an address space.

| Process | Runtime | Purpose | Talks to |
|---|---|---|---|
| `GseTechPro.App.exe` | WinUI 3 / Windows App SDK, .NET 8, **x64, unpackaged** | UI, orchestration, navigation, state | device host (named pipes), AI service (HTTP), scope (in-proc) |
| `GseTechPro.Devices.Host32.exe` | .NET 8, **x86** | Loads 32-bit J2534/RP1210 vendor DLLs; decodes J1939/J1708/J1587/OBD/UDS; simulator | App (named pipes) |
| `GseTechPro.AiService` | Python 3.12 (embedded runtime), FastAPI | RAG, ingestion, LiteLLM routing (tablet / shop server / cloud, ADR-013), tools, voice, vision, recommender | App (HTTP 127.0.0.1), its own `llama-server` children (HTTP 127.0.0.1) |
| `llama-server.exe` x2 | C++ (llama.cpp, bundled, MIT) | Local model runtime, started and stopped by the AI service (ADR-012): chat (`lfm3-omni` GGUF) and embeddings (`nomic-embed-text` GGUF) | AI service (HTTP 127.0.0.1, ports from config) |

Scope support runs **fully in-process, x64** (ADR-011): **PicoSDK is 64-bit** and loads in-process inside `GseTechPro.Scope`, and the **Hantek 6074BE SDK is dual-architecture** (x64 + x86 DLLs/Libs confirmed in `Reference/HT6074BE_SDK/Dll/`, driver `Hantek6000BAMD64.Sys`) so Hantek capture also loads x64 in-process. **No new 32-bit process and no host32 routing for scope.** Vendor scope DLLs are loaded only behind `IScopeDevice` implementations, so the app never touches them. Note: PicoSDK is compiled against the published API but **not exercised against hardware** in Phase 6 (same clean-room pattern as J2534/RP1210); Hantek SDK license + unsigned driver remain "needs verification" (user-installed).

**IPC choices (concrete):**

1. **App ↔ 32-bit device host: named pipes** (`System.IO.Pipes`, duplex, per-session). Request/response for commands (connect, read DTCs, clear codes) and a streaming channel for live data / CAN capture. Framing: a **1-byte frame-kind tag + a little-endian 4-byte length + payload** (see §1.1.1): JSON for control, raw byte arrays for bus/scope streaming. Named pipes beat gRPC here: no HTTP port management, no TLS/HTTP2 stack on a low-power tablet, works in-process under one Windows user session, and is the Windows-native choice for "one local process to another." gRPC is *not* used anywhere — it adds a dependency and a port for zero benefit on a single machine.
2. **App ↔ AI service: local HTTP on `127.0.0.1` only.** FastAPI, LiteLLM and llama-server are HTTP-native, so this is the path of least resistance. The service binds a fixed port (default `8765`, configurable) and requires a random per-install token in a header (`X-GSE-Token`) as defense-in-depth against other local processes. Never bind `0.0.0.0`.
3. **AI service ↔ llama-server: HTTP on `127.0.0.1`** (ports from config). The AI service starts both `llama-server` processes itself with `--host 127.0.0.1`, so the model ports are never exposed (ADR-012).

### 1.1.1 App ↔ 32-bit device host — concrete IPC contract (ADR-007)

This is the normative spec for the Phase 2 IPC client (`device-comms`) and the x86 host. It resolves the Phase-1 "who generates the pipe name / how does the app learn it" gap. Wire DTOs and constants live in `src/GseTechPro.Core/Contracts/HostIpcContracts.cs`; the byte framing lives in `GseTechPro.Devices.Ipc` (device-comms) — Core never references `System.IO.Pipes`.

**Pipe-name handoff + per-launch token.** The **app is the orchestrator** and owns the session identity:

- The app generates a per-session pipe name `GseTechPro.Devices.Host32-{guid:N}` (32 lowercase hex chars) **and** a per-launch token of **32 random bytes → 64 lowercase hex chars** (`RandomNumberGenerator.GetBytes(32)` hex-encoded). Both are unique per launch; the host **stops inventing its own name**.
- The app locates `GseTechPro.Devices.Host32.exe` (same directory as the app, or the installer-chosen `host32\` subfolder — layout owned by `installer-deploy`) and launches it with exactly two arguments: `--pipe <name> --token <token>`.
- The host reads `--pipe`/`--token` from `args`, binds `NamedPipeServerStream` to `<name>` with `PipeOptions.Asynchronous | CurrentUserOnly`, and **never logs the token**.

**Token handshake.** The first frame after connect is a `hello` (JSON): `{ "type":"hello", "version":1, "token":"<64 hex>", "client":"GseTechPro.App", "clientVersion":"0.2.0" }`. The host compares the token byte-for-byte (constant-time), and on match replies `{ "type":"hello-ack", "accepted":true, "hostVersion":"…" }`. On mismatch/absent/malformed it replies `hello-ack` with `accepted:false, error:"unauthorized"` and **closes the pipe** — no device command is ever dispatched before this succeeds.

**Command protocol (JSON control channel).** A single envelope covers every request/response:

- Request (app → host): `{ "type":"request", "id":"<guid>", "command":"<name>", "params":{…} }`. `id` is a client correlation id echoed back.
- Response (host → app): `{ "type":"response", "id":"<same guid>", "ok":true, "result":{…} }` or `{ … "ok":false, "error":{ "code":"…", "message":"…", "detail":"…" } }`.

Command set (Phase 2) — name, params, result:

| Command | Params | Result |
|---|---|---|
| `list-devices` | — | `{ devices:[{id,name,kind,protocols[],isSimulated}] }` |
| `connect` | `{ deviceId, protocol? }` | `{ protocol }` |
| `disconnect` | — | `{ acknowledged:true }` |
| `read-dtcs` | — | `{ dtcs:[DtcReadout] }` |
| `clear-dtcs` | — | `{ cleared:int }` |
| `read-live-data` | `{ pidNames:[] }` | `{ pids:[PidValue] }` |
| `subscribe-live-data` | `{ pidNames:[], intervalMs:100 }` | `{ streamId }` |
| `unsubscribe-live-data` | `{ streamId }` | `{ acknowledged:true }` |
| `read-freeze-frame` | — | `{ freezeFrame: FreezeFrameData\|null }` |
| `get-vehicle-info` | — | `{ vehicleInfo: VehicleInfo }` |
| `ping` | — | `{ pong:"pong", uptimeMs }` |

`ping` is the **heartbeat**: the app pings every 10 s (configurable) and treats no response within 30 s as a dead host → reconnect/re-negotiate. Error codes (string, in `HostErrorCodes`): `unauthorized`, `invalid_request`, `unknown_command`, `device_not_found`, `device_not_connected`, `device_error`, `unsupported`, `timeout`, `internal`.

**Concurrency + streaming.** The single duplex pipe carries interleaved responses and pushes; framing makes each self-delimiting. The host runs a reader loop that dispatches commands **and** a writer that pushes `stream` frames; a single lock serializes writes. The app matches responses by `id` and routes `stream` frames by `streamId`.

**Streaming frame types (control vs binary).** Every frame starts with a 1-byte kind tag, then a 4-byte LE length, then the payload (16 MiB cap): `0x01` JSON, `0x02` binary, `0x03` reserved (unused — scope runs x64 in-process, ADR-011; the tag is kept reserved so the framing doesn't churn). Live data (~10 Hz) streams as JSON `stream` frames: `{ "type":"stream", "streamId":"…", "kind":"live-data", "payload":{ "pids":[PidValue] } }`. High-rate CAN capture uses kind `0x02` binary frames whose layout is **declared by a preceding JSON `stream` frame** (`kind:"can"`, `format:{ protocol, idBits }`); the recommended initial CAN record is `int64 timestampMs | uint32 id | u8 dlc | u8 flags | u8[8] data` (24 bytes) — final layout owned by `device-comms` when the decoders land. Phase 2 recording/playback works purely over the structured `live-data` stream, not raw CAN.

**Provenance across the boundary.** Real vs simulated data is carried end-to-end: `IDiagnosticAdapter.IsSimulated` (default `false`; the simulator overrides to `true`), `DeviceInfo.IsSimulated` from `list-devices`, and `PidValue.IsSimulated` / `DtcReadout.IsSimulated` on every value so a simulated reading stays marked simulated even after it crosses the pipe or is replayed from a recording.

**Packaging: unpackaged + Inno Setup (recommendation, ADR-001).** The app is **unpackaged** (self-contained `.exe` with the Windows App SDK bootstrapped/runtime installed by the setup), not MSIX. Rationale:

- **Silent multi-tablet deployment** (spec: `/SILENT` with a config file, USB or Intune) is trivial with Inno and painful with MSIX (MSIX requires per-machine provisioning, signed certs, and has no clean "silent install + config" story).
- **Upgrades keeping user data** and a **USB update channel** are first-class with Inno (custom `[Files]`/`[Code]`); MSIX forces the store-style update model and file-system virtualization that conflicts with a mutable per-tablet SQLite DB and user manuals folder.
- **No Store dependency, no identity/registry virtualization**, simpler code signing (single `.exe` Authenticode + Inno's signing).
- Trade-offs (accepted, mitigated): unpackaged apps get **Mica** by setting the system backdrop directly (Windows App SDK ≥ 1.2 supports Mica unpackaged), and **toast notifications** need an App User Model ID + a Start-menu shortcut — Inno creates the shortcut, so this is a one-time config, not a blocker.

**The 32-bit J2534/RP1210 helper is explicit and non-negotiable.** Most J2534 (TOPDON RLink X3) and RP1210 (Nexiq USB-Link, Noregon DLA+) drivers ship 32-bit DLLs. A 64-bit process cannot load them. The host32 process is the *only* place those DLLs are ever loaded, so the rule "keep 32-bit vendor DLLs out of the 64-bit app" is enforced by construction. The host is spawned on demand when a diagnostic tool is opened and torn down when the last tool closes.

**Startup / shutdown / crash recovery:**

- `GseTechPro.App.exe` is the orchestrator. On launch it: (1) migrates the SQLite DB, (2) starts `GseTechPro.AiService` if not already running, (3) the AI service starts its `llama-server` processes in the background (GGUF files from `{app}\models\`; nothing is downloaded).
- Each child process is independently supervised: the app holds process handles and a heartbeat over the IPC channel. If the AI service dies, the app restarts it once and degrades the Tech Chat panel to "AI is starting…" until it returns. If the device host dies, the app reconnects and re-negotiates the device session; an in-flight recording is marked "interrupted."
- **Idle unload**: the AI service stops its `llama-server` processes after a configurable idle timeout (default 10 min) so the RAM budget is met when the tablet is idle; the next chat/search starts them again.
- **Cold-start target**: main window interactive in < 10 s; the model loads in the background and the Tech Chat shows a "warming up" state. Measured in Phase 1 (propose and verify the number).

### 1.2 Database design — SQLite per tablet, FTS5 + sqlite-vec

**One SQLite database per tablet** (`gse-tech-pro.db`), with **FTS5** for full-text search, **sqlite-vec** for vector search, and manual PDF files on disk referenced by path. Full DDL is in §2. The design rules that shape every table:

- **GUIDs (`TEXT` UUIDv4) as primary keys from day one**, `created_at`/`updated_at` on every table, and **soft delete via `deleted_at`**. This makes later shop-server sync a pure addition with **no schema rewrite**.
- Tables that feed FTS5 or sqlite-vec (`manual_chunks`) use an **INTEGER primary key with a separate `guid` column**, because FTS5 and `vec0` index on rowid.
- **Encryption at rest: BitLocker, not SQLCipher (ADR-002).** Position: rely on Windows device encryption (the 7220 has TPM + BitLocker) as the at-rest control, verify it's on in the first-run wizard, and warn loudly if off. SQLCipher protects the raw file even on a removed disk but conflicts with the sqlite-vec C extension and FTS5, adds CPU on a constrained tablet, and complicates `VACUUM`/backup. Defer SQLCipher behind the repository boundary; add **AES-encrypted backup** (zip) for USB export now. Technician PINs are stored as salted hashes (never reversible), not as a substitute for disk encryption.

### 1.3 Folder structure

Full annotated tree in §3, mapped to owners from `AGENTS.md`. Summary: `src/GseTechPro.{App,Core,Data,Devices,Devices.Host32,Scope}`, `ai-service/{app,tools,rag,ingest,recommender,voice,vision}`, `data/{troubleshooting,dtc,specs}`, `installer/`, `tests/`, `docs/`, `brand/`, `Reference/` (gitignored, read-only).

### 1.4 User interface

Full palette, icon decision, screen map, touch rules, and units in §5-equivalent UI section below (this is §1.4; detail lives in the UI section). Summary: WinUI 3 Fluent restyle of Shop Foreman Pro's layout; **app accent = the CTA blue `#2E3391`**, with a **dark accent `#8C93F0`** and a **sunlight high-contrast mode**; **separate GSE Tech Pro app icon** with the CTA mark on splash/About; canonical units stored in SI with the tech's entered unit preserved.

### 1.5 AI integration

Local Python FastAPI service → LiteLLM → `llama-server` (llama.cpp, ADR-012) → `lfm3-omni` GGUF (chat/vision/audio) + `nomic-embed-text` GGUF (embeddings). Optional shop-server and cloud routes per ADR-013. Full workflow, tool list, and safety rules in §4. The model is swapped by config only — **no model name is ever hardcoded** — so a bigger server model or a cloud model is a config change.

**Model routing is local-first with an opt-in cloud fallback (ADR-006):** local `lfm3-omni` is the default and always works offline. If (and only if) the shop enables it in settings *and* the internet is reachable, LiteLLM may route to a cloud/hosted model as a higher-capability fallback. Offline behavior is unchanged and fully local. This is an **opt-in exception to the offline-first rule** — disabled by default, never a runtime dependency, sends nothing unless turned on, and sends no telemetry.

### 1.6 Future scalability

**Multi-tablet sync** (later, Phase 9): an optional shop server with **PostgreSQL + pgvector**. Because every row already has a GUID, `updated_at`, and soft-delete, sync is change-data-capture over those columns — no schema rewrites. Tablets work fully without it. **USB update channel** ships from Phase 1 (skeleton installer) onward. **Bigger server model** slots in by the ADR-013 `shop-server` route (the shop's LiteLLM proxy), config only.

---

## 2. Database design (DDL-level)

### 2.1 Conventions

- Primary keys: `TEXT` UUIDv4 (app-generated). Exception: `manual_chunks` and `manual_pages` use `INTEGER PRIMARY KEY` with a `guid` column for FTS5/vec0 rowid alignment.
- Timestamps: `TEXT` ISO-8601 UTC. Soft delete: `deleted_at TEXT NULL`.
- All examples carry `TODO: needs spec from manual` for any value that isn't from an imported manual.
- Migration: `schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT)`. Migrations are **additive and non-destructive**; the data layer (`GseTechPro.Data`) runs them on open, and the installer backs up the DB before any upgrade.

### 2.2 Tables

#### Org and settings

```sql
CREATE TABLE shops (                      -- one row per install (customer shop name from wizard)
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  default_unit_system TEXT NOT NULL DEFAULT 'imperial',  -- 'imperial' | 'metric'
  default_locale TEXT,                    -- e.g. 'en-US'
  timezone TEXT,
  address TEXT, phone TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);

CREATE TABLE technicians (
  id TEXT PRIMARY KEY,
  shop_id TEXT NOT NULL REFERENCES shops(id),
  display_name TEXT NOT NULL,
  pin_hash TEXT,                          -- salted hash; NULL = no PIN
  role TEXT,                              -- 'tech' | 'lead' | 'admin'
  active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_technicians_shop ON technicians(shop_id);

CREATE TABLE app_settings (               -- key/value, holds theme, port, idle timeout, etc.
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
```

#### Equipment / asset registry

```sql
CREATE TABLE equipment_models (           -- shared make/model catalog (TUG 660-28, 600i, …)
  id TEXT PRIMARY KEY,
  make TEXT NOT NULL,
  model TEXT NOT NULL,
  category TEXT,                          -- 'tug'|'belt_loader'|'gpu'|'air_start'|'pushback'|'deicer'|'lavatory_truck'|'loader'|'other'
  engine_make TEXT, engine_model TEXT, transmission TEXT, fuel_type TEXT,
  system_notes TEXT,
  default_unit_system TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_equipment_models_mm ON equipment_models(make, model);

CREATE TABLE assets (                     -- the universal registry (vehicle OR equipment)
  id TEXT PRIMARY KEY,
  asset_kind TEXT NOT NULL DEFAULT 'equipment',  -- 'equipment' | 'vehicle'
  asset_number TEXT NOT NULL UNIQUE,      -- human asset number
  name TEXT,
  model_id TEXT REFERENCES equipment_models(id),
  serial_number TEXT,
  status TEXT NOT NULL DEFAULT 'in_service', -- 'in_service'|'down'|'awaiting_parts'|'retired'
  location_station TEXT,                  -- gate/station/ramp location
  hour_meter REAL, hour_meter_as_of TEXT,
  configuration_json TEXT,                -- flexible per-asset config
  notes TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_assets_model ON assets(model_id);
CREATE INDEX idx_assets_status ON assets(status);

CREATE TABLE vehicles (                   -- 1:1 extension for fleet/HD vehicles
  asset_id TEXT PRIMARY KEY REFERENCES assets(id),
  vin TEXT UNIQUE,
  license_plate TEXT, fleet_number TEXT,
  year INTEGER, engine TEXT, transmission TEXT,
  odometer REAL, odometer_as_of TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_vehicles_vin ON vehicles(vin);
```

Design note: `assets` is the single registry (satisfying "asset tracking" and both the vehicle and equipment databases); `vehicles` holds vehicle-only fields (VIN, odometer, plate) as a 1:1 extension. GSE-equipment-specific fields (hour meter, station, category) live on `assets`/`equipment_models`.

#### Work orders and the visit record

```sql
CREATE TABLE work_orders (                -- one per equipment/vehicle visit (Shop Foreman Pro "event file")
  id TEXT PRIMARY KEY,
  asset_id TEXT NOT NULL REFERENCES assets(id),
  technician_id TEXT REFERENCES technicians(id),
  status TEXT NOT NULL DEFAULT 'open',    -- 'open'|'in_progress'|'waiting_parts'|'closed'
  priority TEXT,
  opened_at TEXT NOT NULL,
  closed_at TEXT,
  complaint_summary TEXT, symptoms_summary TEXT,
  diagnosis_summary TEXT, repair_summary TEXT,
  labor_hours REAL,
  hours_at_open REAL, mileage_at_open REAL,
  equipment_snapshot_json TEXT,           -- copy of asset config at open
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_wo_asset ON work_orders(asset_id);
CREATE INDEX idx_wo_tech ON work_orders(technician_id);
CREATE INDEX idx_wo_status ON work_orders(status);
CREATE INDEX idx_wo_opened ON work_orders(opened_at);
```

```sql
CREATE TABLE symptoms (                   -- structured symptom pick-list (content owned by gse-domain-expert)
  id TEXT PRIMARY KEY,
  code TEXT UNIQUE,
  name TEXT NOT NULL,
  category TEXT,                          -- 'engine'|'hydraulic'|'electrical'|'drivetrain'|'brakes'|'hvac'
  system TEXT,
  description TEXT,
  is_standard INTEGER NOT NULL DEFAULT 1, -- 1 = shipped pick-list entry, 0 = tech-created
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE INDEX idx_symptoms_category ON symptoms(category);

CREATE TABLE complaints (                 -- operator's own words (typed or spoken)
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  text TEXT NOT NULL,
  recorded_by TEXT NOT NULL DEFAULT 'text', -- 'text'|'voice'
  recorded_at TEXT NOT NULL,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE INDEX idx_complaints_wo ON complaints(work_order_id);

CREATE TABLE work_order_symptoms (        -- many-to-many WO <-> symptom
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  symptom_id TEXT NOT NULL REFERENCES symptoms(id),
  noted_at TEXT NOT NULL,
  notes TEXT,
  PRIMARY KEY (work_order_id, symptom_id)
);
```

#### Readings and test points (the "no ECU" troubleshooting path)

```sql
CREATE TABLE test_points (                -- named test ports per model, with spec from manual
  id TEXT PRIMARY KEY,
  equipment_model_id TEXT NOT NULL REFERENCES equipment_models(id),
  name TEXT NOT NULL,                     -- e.g. 'Main hydraulic test port (G3)'
  system TEXT,
  location TEXT,
  reading_type TEXT NOT NULL,             -- 'hydraulic_pressure'|'flow'|'temperature'|'voltage'|'fuel_pressure'|'current'|'other'
  expected_unit TEXT,
  spec_min REAL, spec_max REAL,
  spec_source TEXT,                       -- citation "manual + page" or 'TODO: needs spec from manual'
  procedure_ref TEXT,
  safety_warning_ids TEXT,                -- JSON array of safety warning ids
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_test_points_model ON test_points(equipment_model_id);

CREATE TABLE readings (                   -- every measurement a tech records
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  test_point_id TEXT REFERENCES test_points(id),
  reading_type TEXT NOT NULL,
  system TEXT,
  channel_component TEXT,                 -- e.g. 'P1 pump discharge', 'battery', 'chassis ground'
  value_canonical REAL NOT NULL,          -- canonical (SI) value: Pa, °C, m, L/min, V, A
  unit_canonical TEXT NOT NULL,           -- the SI unit the canonical value is in
  entered_value REAL, entered_unit TEXT,  -- exactly what the tech typed/heard (preserved)
  spec_min REAL, spec_max REAL, spec_unit TEXT,
  spec_source TEXT,                       -- citation or 'TODO: needs spec from manual'
  pass_fail TEXT,                         -- 'pass'|'fail'|'na'
  source TEXT NOT NULL DEFAULT 'manual',  -- 'manual'|'voice'|'scope'|'scan'
  entered_at TEXT NOT NULL, entered_by TEXT,
  notes TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_readings_wo ON readings(work_order_id);
CREATE INDEX idx_readings_testpoint ON readings(test_point_id);
```

**Canonical-unit rule (spec: "store in one canonical unit, keep the unit the tech entered"):** `value_canonical` is always SI (pressure → Pa, temp → °C, distance → m, flow → L/min, voltage → V, current → A). `entered_value` + `entered_unit` preserve the exact entry. The UI converts for display per shop preference (psi/bar, °F/°C, mi/km), so conversion is lossless and per-reading switching is a display concern, not a storage one. **Adapter-reported `PidValue.Unit` is not guaranteed canonical (e.g. kPa, °F): consumers MUST canonicalize via `UnitConverter.ToCanonical(value, reportedUnit)` guarded by `UnitConverter.IsKnownUnit(unit)` before display or comparison against spec ranges.**

#### DTCs and freeze frames

```sql
CREATE TABLE dtc_library (                -- standard SPN/FMI, OBD, manufacturer code reference
  id TEXT PRIMARY KEY,
  protocol TEXT NOT NULL,                 -- 'J1939'|'J1708'|'OBD2'|'UDS'|'manufacturer'
  code TEXT NOT NULL,                     -- e.g. 'SPN 94 FMI 3' or 'P0230'
  spn INTEGER, fmi INTEGER,
  description TEXT,
  system TEXT, severity TEXT,
  likely_causes TEXT,
  source_citation TEXT,                   -- manual/standard citation
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
  UNIQUE (protocol, code)
);

CREATE TABLE freeze_frames (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  captured_at TEXT NOT NULL,
  pids_json TEXT NOT NULL,                -- [{name,value,unit}, ...]
  created_at TEXT NOT NULL
);
CREATE INDEX idx_ff_wo ON freeze_frames(work_order_id);

CREATE TABLE dtcs (                       -- codes seen on a specific vehicle/equipment
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  library_id TEXT REFERENCES dtc_library(id),
  code TEXT NOT NULL,                     -- as read from the bus
  protocol TEXT NOT NULL,
  spn INTEGER, fmi INTEGER,
  status TEXT NOT NULL DEFAULT 'active',  -- 'active'|'pending'|'stored'  (active/confirmed = red, pending = blue in UI)
  description TEXT,
  severity TEXT,
  observed_at TEXT NOT NULL,
  freeze_frame_id TEXT REFERENCES freeze_frames(id),
  source TEXT NOT NULL DEFAULT 'scan',    -- 'scan'|'manual'
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_dtcs_wo ON dtcs(work_order_id);
CREATE INDEX idx_dtcs_code ON dtcs(code);
```

#### Diagnosis, repair, parts, outcomes, comebacks (recommendation engine)

```sql
CREATE TABLE diagnoses (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  statement TEXT NOT NULL,
  system TEXT,
  confidence REAL,
  sources_json TEXT NOT NULL DEFAULT '[]', -- citations: manual+page, WO numbers, DTC ids
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_diag_wo ON diagnoses(work_order_id);

CREATE TABLE repairs (                    -- what was done
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  diagnosis_id TEXT REFERENCES diagnoses(id),
  description TEXT NOT NULL,
  system TEXT,
  labor_hours REAL,
  performed_at TEXT NOT NULL, performed_by TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_repairs_wo ON repairs(work_order_id);
CREATE INDEX idx_repairs_system ON repairs(system);

CREATE TABLE parts (                      -- parts catalog
  id TEXT PRIMARY KEY,
  part_number TEXT NOT NULL,
  description TEXT,
  category TEXT, vendor TEXT, unit_cost REAL,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE UNIQUE INDEX idx_parts_number ON parts(part_number);

CREATE TABLE parts_replaced (             -- part-replacement history (per asset, per WO)
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  part_id TEXT REFERENCES parts(id),
  part_number TEXT NOT NULL,              -- denormalized snapshot
  description TEXT,                       -- snapshot at install
  quantity INTEGER NOT NULL DEFAULT 1,
  vendor TEXT, warranty TEXT,
  installed_at TEXT NOT NULL,
  hours_at_install REAL, mileage_at_install REAL,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_parts_wo ON parts_replaced(work_order_id);
CREATE INDEX idx_parts_replaced_number ON parts_replaced(part_number);

CREATE TABLE outcomes (                   -- how the repair actually went
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL UNIQUE REFERENCES work_orders(id),
  result TEXT NOT NULL,                   -- 'fixed'|'not_fixed'|'unsure'
  reason TEXT,
  closed_by TEXT, closed_at TEXT NOT NULL,
  comeback_detected INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);

CREATE TABLE comebacks (                  -- auto-detected repeat failure
  id TEXT PRIMARY KEY,
  original_work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  comeback_work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  match_reason TEXT,                      -- same asset + same system/symptom within window
  window_days INTEGER, window_hours REAL,
  auto INTEGER NOT NULL DEFAULT 1,        -- 1 = system-detected, 0 = manual
  confirmed INTEGER NOT NULL DEFAULT 0,   -- tech confirms
  detected_at TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_comebacks_original ON comebacks(original_work_order_id);

CREATE TABLE recommendations (            -- memoized repair suggestions (audit + learning)
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  candidate_repair_id TEXT REFERENCES repairs(id),
  similarity_score REAL NOT NULL,
  success_rate REAL NOT NULL,             -- smoothed
  evidence_json TEXT NOT NULL,            -- e.g. "Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, WO #4471"
  rank INTEGER NOT NULL,
  presented INTEGER NOT NULL DEFAULT 0,   -- shown to tech?
  accepted INTEGER NOT NULL DEFAULT 0,    -- tech followed it?
  generated_at TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_reco_wo ON recommendations(work_order_id);
```

#### Notes and attachments

```sql
CREATE TABLE notes (                      -- work order, asset, technician, record, frame notes
  id TEXT PRIMARY KEY,
  scope TEXT NOT NULL,                    -- 'work_order'|'asset'|'technician'|'record'|'frame'
  target_id TEXT NOT NULL,
  technician_id TEXT,
  body TEXT NOT NULL,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_notes_scope ON notes(scope, target_id);

CREATE TABLE attachments (                -- photos, waveforms, recordings, scans, docs
  id TEXT PRIMARY KEY,
  scope TEXT NOT NULL, target_id TEXT NOT NULL,
  kind TEXT NOT NULL,                     -- 'photo'|'waveform'|'recording'|'scan'|'document'
  file_path TEXT NOT NULL,                -- relative to the data dir (files live on disk)
  mime_type TEXT, size_bytes INTEGER,
  taken_at TEXT, caption TEXT,
  annotations_json TEXT,                  -- pen annotations
  created_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_att_scope ON attachments(scope, target_id);
```

#### Manuals, chunks, embeddings, FTS (RAG)

```sql
CREATE TABLE manuals (
  id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  manufacturer TEXT, model_family TEXT,   -- e.g. 'TUG' / '660-28 & 600i'
  file_path TEXT NOT NULL,                -- relative path on disk
  file_hash TEXT NOT NULL,                -- dedupe
  page_count INTEGER,
  format TEXT NOT NULL DEFAULT 'pdf',     -- 'pdf'|'docx'|'pptx' (converted to PDF)
  status TEXT NOT NULL DEFAULT 'imported',-- 'imported'|'failed'
  imported_at TEXT NOT NULL,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_manuals_mfg ON manuals(manufacturer, model_family);

CREATE TABLE manual_pages (               -- page-level metadata (open PDF to cited page)
  id INTEGER PRIMARY KEY,
  guid TEXT NOT NULL UNIQUE,
  manual_id TEXT NOT NULL REFERENCES manuals(id),
  page_number INTEGER NOT NULL,
  section TEXT,                           -- detected: specs/troubleshooting/procedures/wiring/torque
  has_image INTEGER NOT NULL DEFAULT 0,
  thumbnail_path TEXT,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_pages_manual ON manual_pages(manual_id, page_number);

CREATE TABLE manual_chunks (              -- INTEGER pk for FTS5/vec0 rowid alignment
  id INTEGER PRIMARY KEY,
  guid TEXT NOT NULL UNIQUE,
  manual_id TEXT NOT NULL REFERENCES manuals(id),
  page_start INTEGER NOT NULL, page_end INTEGER NOT NULL,
  section TEXT,
  chunk_kind TEXT NOT NULL,               -- 'text'|'table'|'procedure'|'specs'|'troubleshooting'|'wiring'|'torque'|'diagram_caption'
  heading TEXT,
  chunk_text TEXT NOT NULL,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_chunks_manual ON manual_chunks(manual_id, page_start);
CREATE INDEX idx_chunks_kind ON manual_chunks(chunk_kind);

CREATE TABLE chunk_tags (                 -- manufacturer/model/system tags for filtering
  chunk_id INTEGER NOT NULL REFERENCES manual_chunks(id),
  tag TEXT NOT NULL,
  PRIMARY KEY (chunk_id, tag)
);
```

FTS5 and vector tables:

```sql
-- Full-text (BM25). rowid == manual_chunks.id
CREATE VIRTUAL TABLE manual_chunks_fts USING fts5(
  chunk_text, heading, section,
  content='manual_chunks', content_rowid='id',
  tokenize='unicode61'
);
-- keep in sync via triggers (see §2.3)

-- Vector search. 768-dim matches nomic-embed-text. Key is the chunk id.
CREATE VIRTUAL TABLE embeddings USING vec0(
  chunk_id INTEGER PRIMARY KEY,
  embedding float[768]
);
```

`symptoms_fts` for symptom/complaint search:

```sql
CREATE VIRTUAL TABLE symptoms_fts USING fts5(name, description, category, content='symptoms', content_rowid='rowid');
```

#### Waveforms and guided scope tests (scope agent)

```sql
CREATE TABLE guided_tests (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,                     -- 'alternator ripple', 'injector current', …
  equipment_model_id TEXT REFERENCES equipment_models(id),
  system TEXT,
  description TEXT, procedure TEXT,
  safety_warning_ids TEXT,
  reference_waveform_id TEXT,
  scope_setup_json TEXT,                  -- ScopeCaptureRequest: channels, timebase, sample rate, trigger (ADR-011 §7)
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);

CREATE TABLE reference_waveforms (        -- known-good, from real captures/manuals only
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  equipment_model_id TEXT REFERENCES equipment_models(id),
  system TEXT, test_name TEXT,
  expected_json TEXT,                     -- ReferenceExpectations: named measurements, nominal±tolerance or min/max, unit, is_todo (ADR-011 §5)
  data_path TEXT NOT NULL,                -- optional known-good .wfm sample to overlay ('' = none)
  source TEXT NOT NULL,                   -- 'manual'|'real_capture' (never synthesized)
  citation TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);

CREATE TABLE waveforms (                  -- a saved scope capture, tied to a work order
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  scope_device TEXT NOT NULL,             -- 'picoscope'|'hantek'|'simulator'
  channel_config_json TEXT NOT NULL,      -- ScopeChannelConfig[] (JSON array)
  timebase REAL, sample_rate INTEGER, sample_count INTEGER,
  data_path TEXT NOT NULL,                -- binary samples on disk: waveforms/{id}.wfm (ADR-011 §6)
  guided_test_id TEXT REFERENCES guided_tests(id),
  reference_waveform_id TEXT REFERENCES reference_waveforms(id),
  measurements_json TEXT,                 -- ScopeMeasurement[]: computed freq/peak/Vpp/duty/rise/ripple/current + comparison verdicts
  created_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_waveforms_wo ON waveforms(work_order_id);
```

Waveform persistence and comparison (ADR-011, normative):

- **Samples → binary `.wfm` file, metadata → the `waveforms` row.** `data_path` is relative to the data dir (`waveforms/{id}.wfm` and `reference_waveforms/{id}.wfm`). The file is little-endian: `byte[8]` magic `GSEWAV1\0`, `int32` version `=1`, `int32` channelCount, `int32` sampleCount, `double` sampleRateHz, `double` timebaseSecondsPerDivision, then per channel `int32` channelIndex (1-based) + `sampleCount × double` (probe-corrected volts). No JSON sidecar, no per-sample timestamps.
- **`measurements_json` = `ScopeMeasurement[]`** (six measurements — frequency/Hz, peak/V, peak-to-peak/V, duty/%, rise/s, ripple/V (AC RMS), current/A — each with a `status` for no-trigger/too-few-samples/out-of-range/flat-signal/not-configured) **plus** the comparison verdicts (`pass`/`fail`/`na` + the expected nominal/tolerance or min/max and unit) when a reference was compared. One column, so comparison is additive — no schema rewrite.
- **Comparison is deterministic C#** in the scope math engine; the AI only explains the verdict, never judges the raw image or the numbers. `na` = no expectation / `TODO` reference / un-computable measurement / unit mismatch.
- **Memory:** block capture = `sampleRate × timebase × 10` samples/channel; the screen caps total depth so 4 channels ≤ 4 M samples (32 MB) worst case, written streaming, off the UI thread.

#### Recordings and playback (live-data capture, Phase 2)

```sql
CREATE TABLE recordings (                  -- one live-data recording session (screens 17/18)
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  asset_id TEXT REFERENCES assets(id),
  technician_id TEXT REFERENCES technicians(id),
  kind TEXT NOT NULL DEFAULT 'live_data',  -- 'live_data' (Phase 2); 'can'/'scope' later
  source TEXT NOT NULL DEFAULT 'real',     -- provenance: 'simulated' | 'real'
  adapter_name TEXT NOT NULL,              -- device name at capture (e.g. "TOPDON RLink X3", "Simulator (J1939)")
  protocol TEXT NOT NULL,
  pid_names_json TEXT NOT NULL,            -- JSON array of recorded PID names
  trigger_json TEXT,                       -- serialized TriggerConfig; NULL = manual
  started_at TEXT NOT NULL,
  ended_at TEXT,
  status TEXT NOT NULL DEFAULT 'recording',-- 'recording'|'completed'|'interrupted' (host died mid-capture)
  frame_count INTEGER NOT NULL DEFAULT 0,  -- denormalized, updated on close
  notes TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_recordings_wo ON recordings(work_order_id);
CREATE INDEX idx_recordings_asset ON recordings(asset_id);

CREATE TABLE recording_frames (            -- one row per sample instant (a batch of all PIDs)
  id INTEGER PRIMARY KEY,                  -- rowid; high-volume, NOT sync'd (see note below)
  guid TEXT NOT NULL UNIQUE,
  recording_id TEXT NOT NULL REFERENCES recordings(id),
  sequence INTEGER NOT NULL,               -- monotonic 0-based frame index (VCR cursor = seek key)
  captured_at TEXT NOT NULL,
  pids_json TEXT NOT NULL,                 -- JSON array of PidValue (each preserves IsSimulated)
  created_at TEXT NOT NULL,
  UNIQUE (recording_id, sequence)
);
CREATE INDEX idx_recording_frames_recording ON recording_frames(recording_id, sequence);
```

Design notes:

- **One frame = one timestamp's batch of all PIDs** (not one row per PID). At ~10 Hz that is ~10 rows/s, so the VCR playback bar seeks by `sequence` without scanning per-PID rows. A row-per-PID layout would inflate rows ~9× for no query benefit.
- `recording_frames` uses an **INTEGER rowid + separate `guid`** (like `manual_chunks`) for insert locality; it is **excluded from shop-server sync** — only the parent `recordings` metadata syncs, raw frames stay local (a fleet server doesn't need every sample).
- **Provenance (ADR-007):** `recordings.source` records whether the capture came from the simulator or real hardware, and every `PidValue` inside `pids_json` carries its own `IsSimulated` flag, so replay never mislabels data. Phase 4 builds `readings.source` on this: when a scan/scope reading is saved from a recording, the data layer derives simulated-vs-real from `recordings.source` (recommend an additive `readings.is_simulated INTEGER NOT NULL DEFAULT 0` column then, or persist it in `notes` — no rewrite needed).
- **Interrupted recordings:** when the device host dies mid-capture, the app marks the row `status='interrupted'` and updates `ended_at`/`frame_count`, so the VCR bar can still open a partial recording.

#### Troubleshooting trees

```sql
CREATE TABLE troubleshooting_trees (
  id TEXT PRIMARY KEY,
  equipment_model_id TEXT NOT NULL REFERENCES equipment_models(id),
  symptom_id TEXT REFERENCES symptoms(id),
  title TEXT NOT NULL,
  system TEXT,
  status TEXT NOT NULL DEFAULT 'draft',   -- 'draft'|'approved'|'retired'
  source TEXT NOT NULL DEFAULT 'handwritten', -- 'handwritten'|'ai_generated'|'history_improved'
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE INDEX idx_trees_model ON troubleshooting_trees(equipment_model_id, system);

CREATE TABLE troubleshooting_steps (      -- decision-tree nodes
  id TEXT PRIMARY KEY,
  tree_id TEXT NOT NULL REFERENCES troubleshooting_trees(id),
  step_order INTEGER NOT NULL,
  instruction TEXT NOT NULL,              -- "Measure pressure at port G3"
  question TEXT,
  reading_type TEXT, test_point_id TEXT REFERENCES test_points(id),
  spec_min REAL, spec_max REAL, spec_source TEXT,
  safety_warning_ids TEXT,
  next_on_pass TEXT, next_on_fail TEXT,   -- step ids (null = terminal)
  terminal_result TEXT,                   -- diagnosis/repair suggestion at leaf
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE INDEX idx_steps_tree ON troubleshooting_steps(tree_id, step_order);
```

#### Sync metadata

```sql
CREATE TABLE sync_log (                   -- CDC for optional shop-server sync
  id TEXT PRIMARY KEY,
  entity_type TEXT NOT NULL,              -- table name
  entity_id TEXT NOT NULL,                -- row GUID
  revision TEXT NOT NULL,                 -- row updated_at
  change_type TEXT NOT NULL,              -- 'upsert'|'delete'
  pushed INTEGER NOT NULL DEFAULT 0,
  pushed_at TEXT,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_sync_pending ON sync_log(pushed, entity_type);
```

### 2.3 FTS5 ↔ vec0 ↔ relational relationship

- **`manual_chunks` is the single source of truth** for a chunk. Its integer `id` is the shared key.
- **`manual_chunks_fts`** (FTS5, external content) maps `rowid → manual_chunks.id`; triggers `INSERT/UPDATE/DELETE` on `manual_chunks` mirror into the FTS table so BM25 stays in sync.
- **`embeddings`** (vec0) maps `chunk_id → manual_chunks.id` and stores the 768-dim `nomic-embed-text` vector.
- **Hybrid search** (rag-ingest owns this): query FTS5 for BM25 hits and `embeddings` for KNN hits in parallel, merge on `chunk_id`, and **re-rank with Reciprocal Rank Fusion (RRF)** (or a weighted blend) before returning top-k chunks with `manual_id` + `page_start` for citation. Changing the embedding model means re-embedding (rebuild `embeddings`), never mixing dimensions.

### 2.4 Migration strategy

- `schema_migrations` versioned, applied idempotently in a transaction on app open (and by the AI service on its own startup).
- Additive-only during 0.x; no destructive `DROP`/`ALTER ... DROP COLUMN` in shipped migrations.
- The installer (and first-run wizard) creates a dated backup of `gse-tech-pro.db` before applying upgrades — upgrades never delete user data (spec).
- sqlite-vec is loaded as a SQLite extension (`.dll`) bundled with both the .NET data layer and the Python service; FTS5 is built into SQLite.

---

## 3. Folder structure (annotated with owners)

```
GSE-Tech-Pro/
├── AGENTS.md                     # gse-lead
├── README.md                     # gse-lead
├── .gitignore
├── .opencode/agents/             # the 13 agents
├── brand/                        # generated assets (recolor via tools/brand, NOT hand-edited)
│   ├── source/logo.jpg           # original (source of truth)
│   ├── brand-colors.json         # measured palette
│   ├── logo-*.png/.svg, cta.ico, splash*.png, icons/
├── Reference/                    # [gitignored] vendor drivers/SDKs/reference apps — READ ONLY, never shipped
├── docs/
│   ├── PROMPT.md                 # gse-lead (spec)
│   ├── PROMPT.original.md        # ignore
│   ├── ARCHITECTURE.md           # architect (this file)
│   ├── PLAN.md                   # gse-lead
│   ├── DEPLOYMENT.md             # installer-deploy
│   ├── decisions/                # architect (ADRs)
│   ├── Manuals/                  # [gitignored] seed manual library (TUG 660-28/600i first)
│   ├── logos/                    # reference only — never ship
│   └── reference-screenshots/    # 01–29, layout reference only
├── data/                         # gse-domain-expert (content, not code)
│   ├── troubleshooting/          # decision trees (JSON/YAML)
│   ├── dtc/                      # SPN/FMI, OBD, manufacturer code libraries
│   └── specs/                    # test-point definitions + spec values (all cited or TODO)
├── src/
│   ├── GseTechPro.sln
│   ├── GseTechPro.Core/          # architect — shared models, interfaces (IDiagnosticAdapter, IScopeDevice, IAiClient)
│   ├── GseTechPro.Data/          # database — SQLite, migrations, repositories, backup, FTS/vec access
│   ├── GseTechPro.App/           # ui-winui — WinUI 3 screens, styles, view models (MVVM)
│   ├── GseTechPro.Devices/       # device-comms — device abstraction, J1939/J1708/OBD decoders, simulator, host client
│   ├── GseTechPro.Devices.Host32/# device-comms — 32-bit process: J2534/RP1210 vendor DLL loading + IPC server
│   └── GseTechPro.Scope/         # scope — IScopeDevice impls (PicoSDK x64, Hantek), waveform math
├── ai-service/                   # Python 3.12 (embedded)
│   ├── pyproject.toml, requirements*.txt
│   ├── app/                      # ai-engineer — FastAPI app, endpoints, config, llama-server/LiteLLM routing
│   ├── tools/                    # ai-engineer — AI skills/tools (lookup, record reading, note, DTC, etc.)
│   ├── voice/                    # ai-engineer — STT/TTS (lfm3-omni audio first, whisper.cpp/Piper fallback)
│   ├── vision/                   # ai-engineer — photo inspection, OCR (Tesseract/Windows.Media.Ocr)
│   ├── rag/                      # rag-ingest — hybrid search, RRF re-rank, chunk retrieval
│   ├── ingest/                   # rag-ingest — PDF/docx import, OCR, chunking, embedding
│   └── recommender/              # recommender — similar-failure search, success-rate ranking, smoothing
├── installer/                    # installer-deploy — Inno Setup scripts, config templates, assets
├── tests/                        # tester (owners may add their own)
│   ├── GseTechPro.Tests/         # xUnit (C#)
│   └── ai-service-tests/         # pytest (Python)
└── tools/brand/                  # (future) brand generator — not yet in repo
```

The folders in this tree that do **not** exist yet are created in Phase 1 (the `src/`, `ai-service/`, `tests/`, `installer/` projects and skeletons). `data/` is populated as content is authored by `gse-domain-expert`.

---

## 4. AI integration

### 4.1 Runtime topology

```
GseTechPro.App (WinUI) ──HTTP 127.0.0.1:8765──▶ GseTechPro.AiService (FastAPI)
                                                    │  LiteLLM (config-only routes, ADR-013)
                                                    ▼
                          ┌─────────────────────────┴──────────────────────────────┐
                          │ tablet (always, offline): llama-server on 127.0.0.1    │
                          │   ├── lfm3-omni GGUF        (chat / vision / audio)    │
                          │   └── nomic-embed-text GGUF (embeddings, 768-d)        │
                          │ shop-server (optional): the shop's LiteLLM proxy       │
                          │ cloud (admin opt-in + online only): LiteLLM provider   │
                          └────────────────────────────────────────────────────────┘
```

- **Model names, paths and endpoints live only in config** (`config.yaml`: `models`, `llama_server`, `routes`), never in code. Embeddings always stay on the tablet; remote routes are for chat only.
- **Routing (ADR-013):** `tablet-only` (default), `prefer-shop-server`, `prefer-cloud`. Fallback cloud → shop server → tablet. Cloud needs both the service config (`routes.cloud.enabled`) and the app's administrator setting; job details go to the cloud only when the administrator also allows it. Every answer carries its `route`. Keys live in Windows Credential Manager, never in config or logs.
- **Embedding model: `nomic-embed-text`** (137M params, 768-d, ~274 MB, Apache-2.0, runs on CPU). Uses its `search_document:`/`search_query:` task prefixes. ADR-003.
- **Deterministic code does the exact work** (OCR, PDF text/table extraction, waveform measurements, unit conversion, SPN/FMI decode). The model **explains and connects**, it never computes exact values.
- **Prompt discipline for a 2B model**: short prompts, heavy retrieval, tool-first. The model looks things up instead of guessing (spec: "be honest about the 2B model's limits").

### 4.2 Workflow A — Guided troubleshooting

```mermaid
flowchart TD
  A[Tech opens asset, picks symptom] --> B[Load troubleshooting tree for model+symptom]
  B --> C[Show step: instruction + safety warnings]
  C --> D{Tech provides reading / answer}
  D --> E[App converts to canonical, compares to spec range]
  E --> F{pass / fail / no spec?}
  F -->|no spec| G[Model: "spec is TODO, needs manual" — cite, do not invent]
  F -->|pass| H[Follow next_on_pass step]
  F -->|fail| I[Follow next_on_fail step]
  H --> J[leaf? -> diagnosis/repair suggestion with sources]
  I --> J
  J --> K[Model explains, cites manual+page or WO#]
  K --> L[Tech approves/records; history feeds recommender]
```

Numbered steps (the exact behavior the `ai-engineer` + `gse-domain-expert` build):

1. Tech opens an asset and a symptom (structured pick or free text / spoken complaint).
2. The app loads the matching `troubleshooting_trees`/`troubleshooting_steps` (handwritten, AI-generated-from-manual, or history-improved).
3. Each step shows the instruction **and its safety warnings first** (stored hydraulic pressure, lockout/tagout, high voltage, aircraft proximity).
4. The tech provides a reading (typed, picked, or spoken). Voice readings are **read back and confirmed before saving**.
5. The app converts to canonical SI units and compares against `spec_min/max` from the manual (cited) or marks `TODO: needs spec from manual`.
6. Pass/fail routes to the next step (`next_on_pass`/`next_on_fail`).
7. At a leaf, the model produces a diagnosis/repair suggestion **citing its source** (manual + page, or past WO number). It never contradicts or skips manufacturer procedures.
8. The tech records the outcome; that history feeds the recommendation engine (§4.3).

> Tree matching and spec verdicts follow ADR-009: a `draft` tree is served (and labeled "DRAFT — not yet approved") when no `approved` tree matches the same model+symptom, and a single-value spec (`spec_min == spec_max`) yields verdict `na` rather than pass/fail by strict equality.

### 4.3 Workflow B — Recommendation engine (similar-failure search → ranked repairs)

```mermaid
flowchart TD
  A[New diagnosis starts] --> B[Build query vector: symptoms + complaint + readings + DTCs + text]
  B --> C[Search similar past work orders]
  C --> D[1. same model -> 2. same family -> 3. everything]
  D --> E[Match on symptoms, readings, DTCs, text similarity (hybrid)]
  E --> F[Group candidate repairs]
  F --> G[Rank by smoothed success rate, weighted by similarity + recency]
  G --> H[Show evidence: Fixed 18 of 20 on TUG MA-50, avg 2.1h, WO #4471]
  H --> I[Tech picks; outcome + comebacks feed back into the model]
```

Numbered steps (the `recommender` agent builds):

1. On diagnosis start, build a query vector from the new WO's symptoms, complaint text, readings, and DTCs.
2. Search history in three widening tiers: **same model → same equipment family → all**.
3. Score candidates by symptom/DTC/system/text similarity — a deterministic composite (structured overlap + Jaccard token-overlap), with an **optional** vector cosine path RRF-merged when the embedder is present (ADR-010).
4. Group hits into candidate repairs; **rank by smoothed success rate** — Wilson/Laplace smoothing so a 1-for-1 record cannot outrank 45-for-50 — weighted by similarity and recency.
5. Present evidence strings exactly like the spec example, with the source WO numbers (clickable).
6. Record `outcomes` (fixed/not_fixed/unsure) and auto-detect `comebacks` (same asset + same system/symptom within N days/hours) to mark earlier repairs failed, closing the learning loop.

**Concrete contract (ADR-010 — normative).** `recommender` (Python, read-only on the DB) does the
search/ranking; the app (C#) is the single writer of `outcomes`/`comebacks`/`recommendations`; the
endpoint is **`POST /search/repair-history`** (additive — `AiServiceProtocol.Version` stays 1). Tier
is recorded per candidate as `MatchTier` (`model`/`family`/`all`), where model = same
`assets.model_id`, family = same `equipment_models.make` + `category`. Similarity v1 is
deterministic-first (Jaccard text overlap; no new index), vector+RRF only when `nomic-embed-text`
is present (else `fallback:true`). Smoothing = **Wilson lower bound (95%)** with
`n` = non-`unsure` outcomes, `s` = `fixed` minus confirmed comebacks (1-for-1 → 0.207 vs 45-for-50 →
0.786). Comeback defaults = **14 days or 200 hours, whichever is sooner** (configurable), and only
**confirmed** comebacks demote the earlier repair. Evidence template:
`Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, last done 3 weeks ago, WO #4471`. Wire DTOs in
`Core/Contracts/RepairHistoryContracts.cs`; data needs (additive `work_orders.number` +
`idx_wo_closed`) documented in ADR-010 §8 for `database`.

### 4.4 AI skills / tools (complete list)

| Tool | Owner | What it does |
|---|---|---|
| `search_manuals` | rag-ingest | Hybrid RAG (BM25 + vector, RRF) over `manual_chunks`; returns chunks with manual + page |
| `search_repair_history` | recommender | Similar-past-failure search over closed WOs (symptoms/DTCs/readings/text) — same engine as `POST /search/repair-history` (ADR-010) |
| `get_asset` | ai-engineer | Look up an asset by number/VIN/serial; return config + current status |
| `get_repair_history` | ai-engineer | Full repair/outcome/comeback history for an asset |
| `get_part_history` | ai-engineer | Part-replacement history for an asset or part number |
| `get_spec` | ai-engineer | Expected range for a test point, with citation or `TODO: needs spec from manual` |
| `record_reading` | ai-engineer | Validate + convert to canonical units, save; voice readings read back first |
| `create_note` | ai-engineer | Add a work-order / asset / technician note |
| `start_guided_test` / `next_step` | ai-engineer | Drive the troubleshooting tree |
| `search_dtc` | ai-engineer | SPN/FMI, OBD, manufacturer code lookup with severity + likely causes |
| `get_safety_warnings` | gse-domain-expert | Return safety warnings for a step/test point |
| `get_manual_page` | rag-ingest | Open the PDF to the exact cited page |
| `vision_inspect` | ai-engineer (vision) | Describe a photo (leak, worn part, harness, gauge) and match it to manual diagrams |
| `ocr_dataplate` | ai-engineer (vision) | Offline OCR (Tesseract / Windows.Media.Ocr) of serial/data-plate text; **tech confirms** — the model never reads IDs |
| `voice_transcribe` / `speak` | ai-engineer (voice) | Speech in / spoken answers (lfm3-omni audio first, whisper.cpp/Piper fallback) |

### 4.5 AI safety rules (binding on every tool and prompt)

1. Every recommendation **cites its source** (manual + page, or past WO number).
2. Never contradict or skip manufacturer procedures or safety warnings; show warnings **before** related steps.
3. AI output is **explicitly labeled as a suggestion**; the tech makes the final call.
4. If the answer isn't in the manuals or history, **say so** — never invent a spec value.
5. Serial/data-plate text is read by **deterministic OCR**, never by the model, and always tech-confirmed.

### 4.6 Future scalability for AI

- **Bigger server model**: the ADR-013 `shop-server` route (the shop's LiteLLM proxy); tablets that can reach it use it, otherwise fall back to the tablet's `llama-server`. Config-only.
- **Cloud route (opt-in + online only, ADR-013)**: a LiteLLM provider entry used only when an administrator enables it *and* the internet is reachable; otherwise the app stays fully local. Config-only, disabled by default, no hardcoded provider/model names, no telemetry.
- **Fleet-wide learning**: when sync (Phase 9) is on, the recommendation engine queries the server's PostgreSQL + pgvector so rankings use the whole fleet's repair history, not just one tablet.

---

## 5. User interface (WinUI 3, Fluent)

### 5.1 Layout (copied from Shop Foreman Pro, restyled Windows 11)

Kept from the original (`01`, `03`, `04`): menu bar → main toolbar (RO List / Work Orders, New, Open, Search, Save, Print, Home, Notes, Current Technician drop-down, then tool buttons **J2534 · Heavy Duty · OBD II · Scope · CAN Capture · Camera · AI Tech Chat**) → document tabs (one per work order) → **left navigation rail** (was the vertical toolbar; same buttons, same order: Back, Live Data, Refresh, DTCs, Freeze Frames, Current Data, Readiness, Monitors, Data Format…) → breadcrumb (`Equipment Selection >> Main Menu >> Equipment Health`) → dense Value/Units data tables → status bar (device, protocol, connection state). Work-order browser keeps the technician tree / sortable grid / preview pane.

Windows 11 restyle: Mica/Acrylic, Segoe UI Variable, Segoe Fluent Icons (+ own icons), rounded corners, and a Fluent navigation rail replacing the vertical toolbar.

### 5.2 Screen map (against reference screenshots)

| Screen | Source | Status |
|---|---|---|
| App shell (menu/toolbar/tabs/nav rail/breadcrumb/status) | 01 | keep |
| Home | 02 | later (dashboard) |
| Work Order / RO browser | 03 | keep |
| Equipment/Vehicle Health | 04 | keep |
| Equipment Selection / Main Menu | 05, 06 | keep |
| Live data (list, snaplist editor, custom display, graph cursors) | 13, 14, 15, 16 | keep |
| Recording/playback (VCR bar + triggers) | 17, 18 | keep |
| Freeze frames | 07 | keep |
| Readiness / Monitors / O2 sensors | 08, 09, 10 | keep (readiness+monitors); O2 sensors → later |
| Drive / vehicle info | 11 | later |
| In-use tracking | 12 | later |
| Notes | 19 | keep |
| Camera + photo annotate | 20, 21 | keep |
| Search files | 22 | keep |
| User/technician database | 23 | keep |
| Email setup | 24 | later (queued email) |
| Control toolbar (actuation) | 25 | later |
| OBD2 setup / scan-tool setup | 26, 28 | keep |
| Clear codes | 27 | keep |
| Integrated Diagnostics tab | 29 | keep |
| Scope (PicoScope workflow) | new | keep |
| AI Tech Chat | new | keep |
| Guided troubleshooting | new | keep |
| Manuals / RAG viewer | new | keep |

### 5.3 Color palette (proposal, concrete hex — pending user approval)

The app accent is the CTA blue; the logo is recolored to match (see ADR-004), not the other way around.

| Token | Light | Dark | Sunlight (high-contrast) |
|---|---|---|---|
| Background | `#F3F3F3` | `#1C1C1C` | `#FFFFFF` |
| Surface / card | `#FFFFFF` | `#2A2A2A` | `#FFFFFF` |
| Text primary | `#1A1A1A` | `#FFFFFF` | `#000000` |
| Text secondary | `#5C5C5C` | `#A0A0A0` | `#1A1A1A` |
| **Accent (CTA blue)** | `#2E3391` | `#8C93F0` | `#1E2FB8` |
| Accent hover | `#24286E` | `#A7ADF5` | `#16228A` |
| On-accent text | `#FFFFFF` | `#1C1C1C` | `#FFFFFF` |
| Success | `#0F7B0F` | `#6CCB5F` | `#007100` |
| Warning | `#9D5D00` | `#FCE100` | `#8A5200` |
| Danger | `#C42B1C` | `#FF99A4` | `#B30000` |
| **DTC active/confirmed** | `#C42B1C` | `#FF99A4` | `#B30000` |
| **DTC pending/inactive** | `#2E3391` | `#8C93F0` | `#1E2FB8` |
| Focus/border | `#2E3391` | `#8C93F0` | `#000000` |

DTC colors follow Shop Foreman Pro's convention: **red = active/confirmed, blue = pending/inactive** (spec).

- **Light** and **dark** follow the Windows setting; **sunlight** is a manual high-contrast mode for the ramp (pure-white surface, near-black text, thick `#000000` borders/strokes, a stronger saturated blue accent so it reads in glare).
- The palette matches the existing `brand/brand-colors.json` (`#2E3391` spec blue, `#8C93F0` dark accent, `#1C1C1C` dark splash). `ui-winui` owns the theme resources; the palette is recorded here so `ui-winui` and the brand generator stay consistent.

### 5.4 App icon (decision — ADR-004)

**Decision: Option A is approved** — a separate GSE Tech Pro icon, with the CTA logo on the splash and About box only. Both options are described below for the record.

- **Option A — separate GSE Tech Pro icon (APPROVED).** A new own-drawn mark for the product (title bar, Start menu, `cta.ico`). CTA logo appears only on the splash and About box as "by Commercial Truck and Auto." Reason: the product name is "GSE Tech Pro," the CTA truck mark is not readable below 48 px (per `brand/README.md`), and a product-specific mark is clearer on the rugged tablet's taskbar.
- **Option B — CTA mark as the app icon (not chosen).** Reuse `logo-mark.svg`/`cta.ico`; simplest, strongest company branding, but the truck is unreadable at small sizes (current generator already falls back to bold "CTA" letters).

Both reuse the recolored CTA palette. The splash/About always carry the CTA logo + "by Commercial Truck and Auto."

### 5.5 Touch and units

- **Touch-first**: all controls ≥ **44×44 px**, glove-friendly, pen annotation support, on-screen-keyboard friendly; keyboard + mouse fully supported.
- **Units**: canonical storage in SI (§2.2 `readings`), display converted per shop default (imperial/metric) and switchable per reading; the tech's entered value + unit are preserved verbatim. Adapter-reported `PidValue.Unit` is a display hint, not canonical: canonicalize via `UnitConverter.ToCanonical(...)` guarded by `UnitConverter.IsKnownUnit(...)` before display/spec comparison.

---

## 6. Shop Foreman Pro feature inventory (keep / drop / later)

Features visible in Shop Foreman Pro (reference screenshots, the user-facing help file `SFPHelpWithM1NAPA.chm`, and `readme.txt`) that are **not already in the spec**. **Wait for user answers before building any "later" item.**

| # | Feature | Verdict | Reason |
|---|---|---|---|
| 1 | Home page dashboard (02) | later | Useful landing/shortcuts; not required for diagnostics |
| 2 | O2 sensor / lambda tests (10) | later | Light-duty gas-engine test; most GSE is diesel/industrial |
| 3 | IM / smog readiness specifics (09) | later | Light-duty inspection; general readiness/monitors already in scope |
| 4 | Drive-info / VIN decode panel (11) | later | VIN decode needs a data source; store VIN now, decode later |
| 5 | In-use performance tracking, mode $06 (12) | later | Emission PID counters; low GSE value |
| 6 | Control toolbar / bi-directional actuation (25) | later | Active tests are hardware/protocol-sensitive; read-only first |
| 7 | Email setup / outbox (24) | later | Spec already allows queued email; low priority |
| 8 | Specialized OBD II modes beyond $01–$04 | later | Keep core live-data/DTC/clear; add modes on demand |
| 9 | Gauge cluster / custom analog gauge sets | keep | In spec (live-data views); via custom display builder |
| 10 | Saved-event file search across work orders (22) | keep | Spec's "search" generalized to WOs/events/files |
| 11 | Print layouts / report templates | keep | Spec lists print/export; styling deferred to ui/installer phase |
| 12 | Mitchell1 / NAPA TRACS shop-management RO integration (RO browser import/export, Parts & Labor tab, Service Info tab, Vehicle History tab, RO Notes tab) | drop | We have our own work orders; external shop-management integration is out of scope |
| 13 | Mitchell1 repair information as a content source (procedures, wiring diagrams, parts & labor times) | later | Possible future RAG content/partner; not a shop-management RO integration |
| 14 | 5-Gas / exhaust analyzer (MTS 1100) | drop | Emissions testing; out of scope for diesel/industrial GSE |
| 15 | NVH analyzer (MTS 4100) | drop | Noise/vibration/harshness; out of scope |
| 16 | Bosch Engine Analyzer (MTS 5200) hardware | drop | Bosch proprietary; our Scope (PicoScope/Hantek) fills the role |
| 17 | Bosch MTS 3100/5100/5200 Scan Tools | drop | Bosch proprietary; we use J2534 (RLink X3) + RP1210 |
| 18 | Global OBD II (J2534 ECU data) | keep | This is our J2534 diagnostics; already in scope |
| 19 | Auto Mode OBD II (automatic OBD II detection) | keep | Fold into our OBD II device auto-detection |
| 20 | Keypad view / touch keypad UI | keep | Serves our glove/touch-first tablet requirement |
| 21 | Copy records between event files | keep | Cheap + useful (reuse a snapshot in another work order) |
| 22 | Full-screen mode | later | Nice for the tablet; low priority |
| 23 | Drive cycles / trips tracking | later | Readiness-adjacent; light-duty emission focus |
| 24 | COM port test / identify COM port | later | Legacy serial; RP1210/J1708 goes over USB adapters |
| 25 | TWAIN scanner driver | later | Legacy imaging; camera covers the need |
| 26 | Screen capture of the app | keep | Fold into export/screenshot; cheap |

**Source and decisions.** (a) The source of truth for this pass was the user-facing help file (`SFPHelpWithM1NAPA.chm`) plus `readme.txt` — clean-room only, no binaries decompiled; it superseded the screenshots-only list that previously lived here. (b) The user has explicitly decided the big items: **Mitchell1/NAPA TRACS shop-management RO integration is dropped**, **Mitchell1 repair information as a RAG content source is later**, and both the **5-Gas exhaust analyzer (MTS 1100)** and the **NVH analyzer (MTS 4100)** are **dropped**. (c) The Bosch MTS 3100/1100/4100/5200 hardware is **dropped** in favor of **J2534/RP1210 + our own Scope (PicoScope/Hantek)**, matching the spec's device strategy. All remaining `later` items stay out of scope until the user promotes or drops them.

Anything not listed here is either already in the spec or not present in the reference material.

---

## 7. Third-party license table + AI model check

### 7.1 License table

| Component | License | Bundle in installer? | Notes |
|---|---|---|---|
| **llama.cpp (`llama-server`)** | MIT | ✅ bundle | MIT permits redistribution; copy its LICENSE into third-party notices (ADR-012). Replaces Ollama. |
| **`lfm3-omni`** (Liquid AI LFM3-Omni-2B) | **LFM Open License v1.0** (Apache-2.0 base + **$10M/yr revenue threshold**) | ✅ bundle (see note) | Free commercial use **only if the legal entity's annual revenue < $10M**; above that, contact Liquid AI. Include license + notices. Downstream customer revenue is their own concern. **Verify the customer's revenue before shipping.** |
| **`nomic-embed-text`** (Nomic AI) | Apache-2.0 | ✅ bundle | 137M, 768-d, ~274 MB |
| **PicoSDK** (Pico Technology) | Proprietary-permissive ("copy & distribute SDK without restriction, keep copyright; use only with Pico products") | ✅ bundle (with copyright intact) | Not OSI open source; redistributable per terms. Requires Pico hardware. |
| **Hantek 6074BE SDK** | **needs verification** (no public license found) | ⚠️ treat as user-installed until verified | SDK DLL + examples on Hantek's site; **no license text found**. Also: Hantek 6074BE drivers are **not digitally signed** (Win10/11 install friction). Recommend user installs the Hantek driver separately; bundle only if Hantek confirms. |
| **RP1210 / J2534 vendor drivers** (Nexiq USB-Link, TOPDON RLink X3, Noregon DLA+) | Proprietary (per-vendor EULA) | ❌ user-installed separately | Vendor drivers must be installed by the user/shop; never bundled or redistributed. |
| **Enovation ECOM v2 API** | Proprietary (vendor license) | ⚠️ needs verification | Build against published API; confirm redistribution of the API/example code. |
| **OCR engine — Tesseract** | Apache-2.0 | ✅ bundle | Offline OCR; alternatively `Windows.Media.Ocr` (OS-provided, no license burden). |
| **PyMuPDF** | **AGPL-3.0** or commercial | ❌ **do not bundle under AGPL** | AGPL copyleft would force our proprietary app/source disclosure. **Use permissive alternatives instead** (ADR-005): `pypdf` (BSD-3-Clause), `pdfplumber` (MIT), `pdfminer.six` (MIT), or `pypdfium2` (BSD-3-Clause/Apache-2.0). PyMuPDF only if we buy Artifex commercial license. |
| **FastAPI / pydantic / uvicorn** | MIT / MIT / BSD-3-Clause | ✅ bundle | |
| **LiteLLM** | MIT | ✅ bundle | |
| **whisper.cpp** (STT fallback) | MIT | ✅ bundle | |
| **Piper** (TTS fallback) | MIT | ✅ bundle | |
| **sentence-transformers** (if used) | Apache-2.0 | ✅ bundle | |
| **sqlite-vec** | MIT / Apache-2.0 dual | ✅ bundle | |
| **Windows App SDK (WinUI 3)** | MIT | ✅ bundle | |
| **CommunityToolkit.Mvvm** | MIT | ✅ bundle | |
| **Microsoft.Data.Sqlite / EF Core (optional)** | MIT / MIT | ✅ bundle | |

### 7.2 AI model check

- **Runtime (ADR-012): llama.cpp `llama-server`** with the LiquidAI GGUF build. Liquid's llama.cpp guide names a minimum llama.cpp build for LFM3 and `--reasoning off` for the server (set in `llama_server.extra_args`). **Needs verification** on our pinned build, including image/audio via the projector. (The Ollama tag checked earlier no longer applies.)
- **License: ✅ with a caveat** — LFM Open License v1.0. Free commercial use **under $10M annual revenue** (whole legal entity); a customer above that threshold needs a separate Liquid AI license. The license terminates on any breach. Include the license + NOTICE in the installer.
- **Size/RAM:** 2.17B params; GGUF `Q4_K_M` ≈ 1.73B model (~1.1 GB) + 475M vision projector (BF16, ~950 MB) ≈ **2.0–2.4 GB on disk**, similar in RAM when loaded, plus KV cache and llama-server/Python overhead → expect the AI stack ≈ **3–3.5 GB**, inside the 5 GB budget. Embedding model adds ~300 MB transiently.
- **Speed on 8th-gen i5, 8 GB CPU-only:** **needs measurement** (spec requires it). Rough expectation for a 2B-class Q4 model on that CPU is single-digit-to-low-teens tokens/sec for text, slower with audio; plan for retrieval-heavy, tool-first prompts and short context (use the **32K context** figure from the spec table, not the 125K page header).
- **Honest limits:** Liquid's own card says it is **not recommended for agentic coding or complex multi-step reasoning** — consistent with our design (deterministic code does the exact work; the model explains/connects). Audio quality on CPU also needs verification before committing voice to `lfm3-omni` instead of whisper.cpp + Piper.

**Fallback models (config-swappable):**

Local `lfm3-omni` is the default and always works offline. The cloud route is an **opt-in, online-only** exception (ADR-013) — disabled by default, never a runtime dependency, no telemetry, and nothing leaves the tablet unless the shop enables it. Embeddings stay local (`nomic-embed-text`) regardless of which chat model is in use.

| Priority | Model | License | Why |
|---|---|---|---|
| 0 (shop server / cloud, opt-in) | **ADR-013 remote routes** — shop LiteLLM proxy, or any LiteLLM cloud provider (config-only) | varies by provider | Higher capability when online **and** the shop opts in; auto-falls back to local `lfm3-omni` offline. No hardcoded provider/model names. |
| 1 | **Qwen2.5-Omni-3B** | Apache-2.0 | Closest omni-modal fallback (text+vision+audio), 3B class, CPU-viable, no revenue threshold |
| 2 | **Split pipeline**: `Qwen3`-4B or `Phi-3.5-mini` (MIT) + `whisper.cpp` STT + `Piper` TTS + a small vision model (e.g. moondream/llava) | Apache-2.0 / MIT | If on-device omni audio is too slow; matches spec's stated fallback |
| 3 | **LFM3-1.7B** text-only (same LFM license) | LFM Open License v1.0 | Text/tool-calling only; same license caveat |

Anything not verified above is marked **needs verification** rather than assumed.

---

## 8. Phase plan

> Mirrors the spec's 9 phases. `docs/PLAN.md` (owned by `gse-lead`) tracks granular tasks; this is the architecture-level map with per-phase deliverables and dependencies. Definition of done per phase: builds clean; xUnit + pytest pass; every feature works in **simulator mode** (and on real hardware where available) with written test steps; screens checked against reference screenshots + 44×44 touch rules + sunlight mode; within the 8 GB performance budget; `docs/PLAN.md` updated.

| Phase | Deliverables | Depends on | DoD emphasis |
|---|---|---|---|
| **1. Shell, data, build pipeline** | Main window (Shop Foreman Pro layout, Win11 style), SQLite schema + migrations (all §2 tables), repositories, work orders + asset/equipment DB, simulator, **skeleton installer** (app + embedded Python + Ollama on a clean machine). Folder tree created. Cold-start < 10 s measured. | none | Screens vs 01/03/04; first packaging risk surfaced early |
| **2. Diagnostics** | Device-abstraction layer, 32-bit host, J2534 (RLink X3), RP1210, J1939/J1708/OBD II, Equipment Health, live data, recording/playback | 1 | Simulator-first for every protocol; real hardware where present |
| **3. Manuals and AI** | Ingestion (seed library first), hybrid search (FTS5+vec0+RRF), Ollama + `lfm3-omni` wired via LiteLLM, Tech Chat, guided troubleshooting | 1, 2 | 600i maintenance question returns the right page (RAG acceptance test) |
| **4. GSE troubleshooting (no ECU)** | Readings entry (canonical units), decision trees (TUG 660-28 first), safety warnings | 3 | All spec values cited or `TODO` |
| **5. Repair history learning** | Recommendation engine, outcomes, comebacks, smoothing + evidence | 1, 4 | "Fixed 18 of 20…" evidence strings; smoothing verified |
| **6. Oscilloscope** | `IScopeDevice` + PicoSDK, then Hantek; shared scope screen; guided tests; waveform math + comparison | 1 | Real + reference waveforms; never synthesized known-good |
| **7. Vision and voice, ECOM/CANCapture** | Photo inspection + OCR, voice PTT + STT/TTS, ECOM v2, CANCapture import | 3, 6 | Voice readings read-back-confirmed; OCR tech-confirmed |
| **8. Full installer** | `GSE-Tech-Pro-Setup.exe`: signing, silent multi-tablet deploy, upgrades (keep data), updates, USB channel | all | Silent install verified on clean + upgraded tablets |
| **9. Optional shop-server sync** | PostgreSQL + pgvector, CDC from `sync_log`, fleet-wide recommender | 1, 5 | Tablets fully functional with sync off |

---

## Decision records

Key irreversible decisions are recorded as ADRs in `docs/decisions/`:

- `001-packaging-unpackaged-inno.md` — unpackaged + Inno Setup (vs MSIX)
- `002-encryption-at-rest.md` — BitLocker now, SQLCipher deferred (vs SQLCipher now)
- `003-embedding-model.md` — `nomic-embed-text` (vs bge-m3 / mxbai / all-minilm); runtime amended by 012
- `004-app-icon.md` — separate GSE Tech Pro icon (Option A, approved)
- `005-pdf-library.md` — avoid PyMuPDF (AGPL); permissive PDF stack
- `006-ai-model-routing.md` — local-first `lfm3-omni` + opt-in cloud fallback (**superseded by 013**)
- `007-host32-ipc-contract.md` — app↔host32 pipe handoff + token, JSON command envelope, binary streaming frames, `IsSimulated` provenance
- `008-ai-service-api.md` — app↔AI-service HTTP contract: `X-GSE-Token`, error envelope, `/health` `/config` `/chat` `/search/manuals` `/ingest` `/guided`, config schema, offline/BM25-only degradation, SQLite/sqlite-vec sharing
- `009-gse-troubleshooting.md` — Phase-4 content schema (five JSON files, slug PKs), spec-unit convention (authoring unit → canonical SI), guided `/guided/*` resolution flow, `safety_warnings` table, `readings.is_simulated` provenance
- `010-recommendation-engine.md` — Phase-5 recommendation engine: ownership boundary, 3-tier search, deterministic similarity + optional vector/RRF, Wilson smoothing, comeback detection, evidence-string format, `/search/repair-history` wire contract
- `011-scope-interface.md` — Phase-6 oscilloscope: x64 in-process for both brands (no host32), block-capture `IScopeDevice`/`IScopeDeviceFactory`, six measurement algorithms + units/status, `ReferenceExpectations` comparison (deterministic C#, AI only explains), `.wfm` binary persistence, guided-test flow
- `012-llama-cpp-runtime.md` — llama.cpp `llama-server` (GGUF) replaces Ollama; service-supervised child processes, idle stop, `/health` field changes
- `013-ai-routing-tablet-shop-cloud.md` — tablet / shop-server / cloud routes via LiteLLM, fallback order, two-key cloud opt-in, job-detail stripping, Credential Manager keys, route on every answer
- `§6 feature inventory` — keep/drop/later decisions (Mitchell1/NAPA TRACS RO integration, 5-gas, NVH, and Bosch MTS hardware dropped; Mitchell1 repair-info → later) recorded inline in §6 rather than a separate ADR
