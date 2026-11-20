# ADR-008 — AI service HTTP API: transport, auth, endpoints, config, degradation, DB sharing

- **Status:** Accepted
- **Date:** 2026-11-16
- **Owner:** architect
- **Amended:** ADR-012 (`model_runtime_reachable`, `model_runtime_unreachable`, `model_loading`; `ollama_reachable` kept one release) and ADR-013 (`routing` on `/chat`, `route`/`model` on the answer, `routes` on `/health`), 2026-11-19

## Context

Phase 3 (Manuals & AI) has four owners that must build in parallel against one contract: `rag-ingest` (ingestion + hybrid search), `ai-engineer` (FastAPI service, Ollama/LiteLLM wiring, chat, guided), `database` (SQLite schema + repos), and `ui-winui` (Tech Chat, Manuals/RAG viewer, guided screen). The Phase-1 `IAiClient` was a stub (`ChatAsync`, `SearchManualsAsync`) and `AiContracts.cs` only has request-side shapes. This ADR is the **normative wire contract** for the App ↔ `GseTechPro.AiService` boundary, so the four owners implement against one source of truth and don't rework.

Two hard environment facts shape it:

1. **The dev machine has no Ollama and Python 3.15.2.** The service must boot, answer `/health` and `/config`, and return RAG search results with Ollama absent, using a **deterministic fallback** (BM25-only ranking, no vectors) so every test passes offline. Real models (`nomic-embed-text` 768-d, `lfm3-omni`) are swapped in by config only — **no model name is hardcoded in code**.
2. **The WinUI app is the only consumer** and is the orchestrator (ARCHITECTURE.md §1.1). All transport is `127.0.0.1` HTTP with a per-install `X-GSE-Token` header.

This ADR mirrors ADR-007: the app owns the session identity (here, the port + token), and a single JSON envelope/error shape serves every endpoint.

## Decision

### Transport, auth, and error envelope

- **Transport:** HTTP/1.1 over `127.0.0.1` only (never `0.0.0.0`). Default port **8765**. All bodies are `application/json; charset=utf-8`.
- **Wire JSON is `snake_case`** (matches pydantic defaults and the §2 column names). The C# client maps via `System.Text.Json` with `JsonNamingPolicy.SnakeCaseLower` + an enum converter that reads/writes each enum's `[EnumMember]` string value (reuse/port the `EnumMemberJsonConverter` already in `GseTechPro.Devices/Ipc/HostJson.cs`). Example: C# `PageStart` ↔ wire `page_start`, `JobId` ↔ `job_id`, `UseTools` ↔ `use_tools`.
- **Auth:** every endpoint requires the header **`X-GSE-Token: <token>`** — including `/health` and `/config`. Missing/blank/wrong token → **`401`** with the error envelope below, `code:"unauthorized"`. The service compares the token **constant-time** (never logs it). This is defense-in-depth against other local processes; the pipe/port is already same-user.
- **Error envelope** (used for every non-2xx response):

  ```json
  { "error": { "code": "string", "message": "string", "detail": "string|null" } }
  ```

  Error codes (`AiErrorCodes`): `unauthorized`, `invalid_request`, `validation_error`, `not_found`, `conflict`, `ai_unavailable`, `ai_starting`, `ingest_failed`, `unsupported`, `internal`.

  HTTP status mapping (the app acts on status + `code`, never on message text):

  | HTTP | `code` |
  |---|---|---|
  | 400 | `invalid_request` |
  | 401 | `unauthorized` |
  | 404 | `not_found` |
  | 409 | `conflict` |
  | 422 | `validation_error` (pydantic) |
  | 500 | `ingest_failed`, `unsupported`, `internal` |
  | 503 | `ai_unavailable`, `ai_starting` |

### Endpoints (request → response)

The wire DTOs/constants live in `src/GseTechPro.Core/Contracts/AiServiceContracts.cs`; the enums in `src/GseTechPro.Core/Enums/AiEnums.cs`. `AiContracts.cs` is unchanged (its records serialize to the same snake_case under the client's naming policy).

**`GET /health` → `AiServiceHealth`** (always `200`, even degraded — this is the liveness + degradation probe):

```json
{
  "status": "ok",                       // "starting" | "ok" | "degraded"
  "ollama_reachable": false,
  "chat_available": false,
  "search_available": true,
  "chat_model": "lfm3-omni",            // configured model name (from config), not "loaded"
  "embedding_model": "nomic-embed-text",
  "degradation_reason": "ollama_unreachable",  // null when status == "ok"
  "version": "0.3.0"
}
```

- `status`: `starting` = booting / model warming up; `ok` = fully ready; `degraded` = running with reduced capability.
- `chat_available` = `ollama_reachable && chat model present`. `search_available` = the SQLite DB is open (RAG search always works — with vectors when the embedding model is present, BM25-only otherwise).
- `degradation_reason` allowed values (open set, lowercase snake): `ollama_unreachable`, `chat_model_missing`, `embedding_model_missing`, `db_unavailable`; `null` when `ok`.

**`GET /config` → `AiServiceConfig`** (the *effective* runtime config the app displays):

```json
{
  "base_url": "http://127.0.0.1:8765",
  "chat_model": "lfm3-omni",
  "embedding_model": "nomic-embed-text",
  "embedding_dimension": 768,
  "idle_unload_minutes": 10
}
```

`base_url` is the service's own bound URL including the **effective** port (so the app can self-configure). `ollama.host` is **not** exposed — it is a service-internal concern; reachability is surfaced via `/health`.

**`POST /chat` → `ChatResponse`** (or `503` when chat is unavailable — see Degradation).

Request (existing `AiChatRequest`, unchanged):

```json
{
  "messages": [ { "role": "user", "content": "why no hydraulics?", "tool_call_id": null } ],
  "work_order_id": null,
  "asset_id": null,
  "use_tools": true,
  "system_instruction": null
}
```

Response:

```json
{
  "text": "Check the hydraulic pump discharge pressure at port G3 …",
  "is_suggestion": true,
  "citations": [
    { "kind": "manual-page", "display_text": "TUG 660-28 Maintenance Manual, page 12",
      "manual_id": "<uuid>", "page_start": 12, "work_order_number": null },
    { "kind": "work-order", "display_text": "WO #4471: fixed 18 of 20",
      "manual_id": null, "page_start": null, "work_order_number": "4471" }
  ]
}
```

`is_suggestion` is `true` for every model-generated answer (safety rule §4.5 #3); the app renders the "AI output is a suggestion — the technician makes the final call" label whenever it is `true`. `citations` may be empty.

**`POST /search/manuals` → `SearchManualsResponse`**:

Request:

```json
{ "query": "hydraulic pressure test port", "top_k": 5, "manual_id": null, "chunk_kinds": null, "include_text": true }
```

- `top_k` default 5, max 50. `manual_id` / `chunk_kinds` (list of `ChunkKind` codes) optional filters; `null` = no filter. `include_text` default `true`.

Response:

```json
{
  "results": [
    { "chunk_id": "<guid>", "manual_id": "<uuid>", "manual_title": "TUG 660-28",
      "page_start": 12, "page_end": 12, "section": "specs",
      "text": "Main relief set to …", "score": 0.87,
      "citation": "TUG 660-28 Maintenance Manual, page 12" }
  ],
  "fallback": false
}
```

- `results[i].chunk_id` is **`manual_chunks.guid`** (the sync-safe UUID), **not** the integer rowid. The integer rowid (`manual_chunks.id`, the `vec0` key) is an internal DB detail and is never on the wire.
- `fallback`: `true` when the embedding model was unavailable and ranking was **BM25-only**; `false` for the normal hybrid path (BM25 + vector, RRF). The app badges results as "text-only" when `fallback` is `true`.

**`POST /ingest` → `202 Accepted`**, body `{ "job_id": "<uuid>" }`:

Request:

```json
{ "paths": ["C:\\manuals\\tug-660.pdf", "D:\\manuals\\600i\\"], "force_reingest": false }
```

- `paths` are absolute file or folder paths (the app has already picked them via a file/folder dialog). `force_reingest` = re-import even if `manuals.file_hash` is unchanged. Returns `202` with a `job_id`; the app polls status.

**`GET /ingest/status?job_id=<uuid>` → `IngestionStatus`** (`404 not_found` for an unknown job):

```json
{
  "job_id": "<uuid>",
  "state": "running",                   // "pending" | "running" | "succeeded" | "failed" | "cancelled"
  "files_total": 3,
  "files_done": 1,
  "chunks_created": 120,
  "chunks_embedded": 0,                 // chunks that got a vector row (0 when embedding model unavailable)
  "current_file": "tug-660.pdf",        // null when not running
  "errors": [ { "file": "bad.pdf", "message": "unreadable PDF", "page": null } ]
}
```

`chunks_embedded < chunks_created` means the embedding model was unavailable at ingest time; search still works (BM25-only) and those chunks can be re-embedded later (future additive endpoint / `force_reingest`).

**`POST /guided/start` → `GuidedStep`** and **`POST /guided/next` → `GuidedStep`** (contract only in Phase 3 — the tree *content* lands in Phase 4, so the service returns the clear "no tree yet" shape):

Start request:

```json
{ "equipment_model_id": "<uuid>", "symptom_id": null, "complaint_text": null }
```

Next request:

```json
{ "session_id": "<uuid>", "step_id": "<uuid>", "verdict": null, "reading_value": null, "reading_unit": null, "answer": null }
```

- `verdict` is `"pass" | "fail" | "na"` (the app has already canonicalized + compared against spec, per §4.2). `reading_value`/`reading_unit` optional (service canonicalizes + records); `answer` optional free text for question steps.

Step response (Phase 3 = always `available:false`, with the canonical "no tree yet" `instruction`):

```json
{
  "session_id": "<uuid>",
  "available": false,
  "tree_id": null, "step_id": null, "step_order": 0,
  "instruction": "No troubleshooting tree is available for this model and symptom yet.",
  "question": null,
  "safety_warnings": [],
  "reading_type": null, "test_point_id": null,
  "spec_min": null, "spec_max": null, "spec_source": null,
  "terminal_result": null,
  "next_on_pass": null, "next_on_fail": null
}
```

`/guided/start` always returns a fresh `session_id` even when `available:false`. `/guided/next` with an unknown session → `404 not_found`; with a known session but no tree → the same `available:false` shape. `safety_warnings` are **display strings** (resolved by the service; ids are a DB detail). Phase 4 fills `available:true` + the tree fields without changing this shape.

### Config schema — `ai-service/config/config.yaml`

The service reads its config from `ai-service/config/config.yaml` (path overridable via `--config`). Exact shape (defaults shown):

```yaml
# ai-service/config/config.yaml — owned by ai-engineer; installer ships a real copy (installer-deploy).
service:
  host: 127.0.0.1            # never 0.0.0.0
  port: 8765                 # standalone default; the App overrides via --port at runtime
  token: "dev-local-token"   # standalone default; the App overrides via --token at runtime

ollama:
  host: "http://127.0.0.1:11434"
  no_cloud: true             # OLLAMA_NO_CLOUD=1

models:
  chat: "lfm3-omni"          # config-only; never hardcoded in code
  embedding: "nomic-embed-text"
  embedding_dim: 768         # MUST match the vec0 dimension (§2.2) — changing it means re-embedding

idle_unload_minutes: 10      # unload models after idle (RAM budget, §1.1)

degradation:
  offline_chat_message: "AI assistant is unavailable (Ollama not reachable). Manual search remains available."
  fallback_search: "bm25"    # when no embedding model: rank by FTS5 BM25 only (deterministic, no model)
```

### How the app learns the port + token (no ambiguity)

The **app is the orchestrator and owns the port + token**, exactly as it owns the pipe name + token in ADR-007:

- On first run the app generates a **per-install token** (32 random bytes → 64 lowercase hex chars, same format as ADR-007) and selects a **port** (default 8765), persists both in `app_settings` (`ai_service.port`, `ai_service.token`), and launches the service with **`--port <n> --token <token>`**.
- **CLI args override `config.yaml`.** The `service.port` / `service.token` entries in `config.yaml` are **standalone-run defaults only** (dev/test: `pytest`, engineers running `python -m app.main`). When the app launches the service it always passes `--port`/`--token`, so the two can never diverge at runtime.
- The service **never invents** a port/token and **never logs** the token. The app calls `/health` and `/config` with the token it generated; `/config.base_url` echoes the effective port back so the app can self-check.
- The app never reads `config.yaml`; the service never reads `app_settings`. Each side has exactly one source of truth.

### Degradation & offline behavior (normative, identical for `rag-ingest` and `ai-engineer`)

1. **Boot with no Ollama.** Service starts, binds the port, opens the SQLite DB, then probes Ollama (`GET /api/version` on `ollama.host`). `ollama_reachable` = probe succeeded. `/health` returns `200` with `status:"degraded"`, `ollama_reachable:false`, `chat_available:false`, `search_available:true`, `degradation_reason:"ollama_unreachable"`. `/config` returns `200` with the configured model names. **Nothing fails to start.**
2. **`/chat` when chat is unavailable** (Ollama unreachable, or chat model missing/not yet loaded): return **`503`** with the error envelope `{ error: { code: "ai_unavailable", message: <degradation.offline_chat_message>, detail: "<why>" } }`. While the model is loading (Ollama reachable, model not yet ready): `503` `code:"ai_starting"` (detail can name the model). The app renders `ai_unavailable` as "AI unavailable — search still works" and `ai_starting` as "AI is starting…". **`/chat` never fabricates an answer and never returns a bare empty body.**
3. **`/search/manuals` with no embedding model:** rank by **FTS5 BM25 only** (skip vector KNN and RRF), and set `fallback:true`. This is the deterministic path that keeps every offline test green. When the embedding model *is* present, use hybrid (BM25 + `vec0` KNN, RRF-merge) with `fallback:false`.
4. **Ingestion with no embedding model:** still parse, chunk, store `manual_chunks` + `manual_pages` + `chunk_tags`, and sync `manual_chunks_fts`. Write **no** `embeddings` rows; report `chunks_embedded:0` in `IngestionStatus`. Search still works BM25-only. (Re-embedding is a future additive step / `force_reingest`.)
5. **Idle unload:** after `idle_unload_minutes` of no chat/search traffic, the service asks Ollama to unload models to meet the RAM budget; the next `/chat` shows `ai_starting` while it reloads.

### DB sharing between Python and C#

- **Single SQLite file** `gse-tech-pro.db`, opened in **WAL mode** by both runtimes: `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;`. This is what lets Python read (search) while Python/C# write without blocking.
- **Schema authority is `GseTechPro.Data` (C#, `database`).** Migrations are versioned SQL files under `src/GseTechPro.Data/Migrations/`, tracked by `schema_migrations`. The **app migrates on open** (orchestrator starts first, §1.1). The AI service **does not author DDL**; on startup it applies any pending migrations from the same SQL files (shipped alongside it) as an idempotent safety net for standalone runs — a no-op when the app already migrated.
- **Write ownership (no two writers per table):** Python (`ai-service`) is the **sole writer of the RAG tables** (`manuals`, `manual_pages`, `manual_chunks`, `chunk_tags`, `manual_chunks_fts`, `embeddings`). C# (`GseTechPro.Data`) is the **sole writer of all other tables** and reads the RAG tables for the UI via repositories. This prevents lock contention by construction.
- **sqlite-vec** (`.dll`) is bundled with **both** the .NET data layer and the Python service (ADR §2.4); FTS5 is built into SQLite. The `embeddings` `vec0` table is created by the C# migration; Python loads the same extension to query/write it.
- **Embedding dim is fixed at 768** (ADR-003, `nomic-embed-text`), and the vec0 key `embeddings.chunk_id` (INTEGER PK) **== `manual_chunks.id`** (INTEGER rowid). Never mix dimensions; changing the model means re-embedding (§2.3).

### Where the HTTP client lives

**`AiServiceClient` lives in `src/GseTechPro.App/Services/AiServiceClient.cs`, owned by `ui-winui`.** Rationale: the app is the only consumer (constraint), so a new `GseTechPro.Ai` project would add a project + an owner for a ~200-line adapter. The contract (interface `IAiClient` + DTOs) stays in `GseTechPro.Core` (owned by `architect`); the implementation is a thin `System.Net.Http` + `System.Text.Json` adapter in the app. `ui-winui` implements it against this ADR and wires it in DI.

## Alternatives considered

- **New `src/GseTechPro.Ai` project for the client:** cleaner layering and future-proof if a second consumer appears (headless sync/test harness), but a new project + owner for a single consumer now. Deferred; revisit if a second consumer shows up.
- **camelCase wire JSON** (matching the existing `RecordingSerialization`): possible, but snake_case matches pydantic defaults and the §2 column names, removing a mapping layer on the Python side. Chosen snake_case.
- **`/chat` returns `200` with a sentinel "unavailable" string:** fragile (app must string-match) and conflates success with failure. `503` + `code:"ai_unavailable"` is RESTful and the app keys off the code.
- **Skip the deterministic fallback and require Ollama for all tests:** violates the offline/no-Ollama constraint and blocks parallel Phase-3 work. Rejected.
- **`/health` unauthenticated:** slightly simpler liveness probe, but leaves a local info leak and a second auth rule to remember. Rejected — one rule: every endpoint requires the token.
- **Hash-embedding fallback (768-d feature-hash) instead of BM25-only:** keeps the KNN path "working" offline, but returns fake vectors with unknown quality and complicates the RRF merge. BM25-only is simpler, deterministic, and honest. Chosen BM25-only.

## Consequences

- `rag-ingest`, `ai-engineer`, `database`, `ui-winui` each have a precise, implementable slice: ingestion/search semantics + `fallback` flag (rag-ingest), endpoint handlers + pydantic models + config load (ai-engineer), WAL + schema ownership + no cross-writes (database), and the screen states driven by `/health` + `/config` + `is_suggestion` + `fallback` (ui-winui).
- `AiChatRequest` / `AiChatMessage` / `AiChatRoles` / `SearchResult` in `AiContracts.cs` are **unchanged**; new shapes live in `AiServiceContracts.cs` + `AiEnums.cs`. `IAiClient` is extended **additively** (`ChatAsync` / `SearchManualsAsync` signatures untouched; new full variants added).
- The guided contract is settled now with an explicit `available:false` "no tree yet" shape, so Phase 4 tree content drops in without a contract change.
- `fallback:true` and `chunks_embedded` make the offline degradation path self-describing, so the app can honestly badge degraded results instead of pretending.
- Referenced from `docs/ARCHITECTURE.md` §1.1, §1.5, §2.2–2.4, §4, and the decision-records list.
