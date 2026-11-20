# ADR-012 — Local model runtime: llama.cpp (`llama-server`, GGUF), not Ollama

- **Status:** Accepted (owner's decision, 2026-11-19)
- **Date:** 2026-11-19
- **Owner:** architect
- **Supersedes:** the Ollama parts of ARCHITECTURE §1.1, §1.5, §4.1 and §7.2. **Amends:** ADR-003 (embedding runtime), ADR-008 (`/health` fields).

## Context

Phases 1–3 ran the local model on Ollama (`ollama serve` as the `GSE-Ollama` Windows service, LiteLLM route
`ollama_chat/`). The owner asked to replace it with llama.cpp. An earlier draft of this decision
(`0001-llama-cpp-and-chat-api.md`) also defined a new streaming chat API and a `GseTechPro.AiClient` project; neither
exists on `master`. The working API is ADR-008 (`AiServiceClient`), and this ADR keeps it.

## Decision

1. **Runtime.** The AI service runs llama.cpp's **`llama-server`** (OpenAI-compatible HTTP) as child processes it
   starts, watches and stops itself. There is no Windows service and no background runtime outside the AI service.
   - **Chat:** one `llama-server` with the chat GGUF (`lfm3-omni`, plus its projector file when image/audio input is
     turned on).
   - **Embeddings:** a second `llama-server --embedding --pooling <model's pooling>` with the embedding GGUF (ADR-003
     as amended below).
   - Both bind **`127.0.0.1` only**. The service always passes `--host 127.0.0.1` and refuses config that tries to
     set `--host` itself.
2. **Models are GGUF files** under `{app}\models\` (installer) or a path in config (USB copy). No model registry, no
   download at runtime.
3. **Config only** (`llama_server:` block in `config.yaml`): binary path, chat model path, projector path, context
   size, threads, port, extra flags; and an `embedding:` sub-block (model path, port, pooling, context). Flags that
   differ between llama.cpp releases (e.g. `--reasoning off` for LFM3) go in `extra_args`, not in code. When
   `binary` is empty the service **attaches** to a `llama-server` already listening on the configured port (dev and
   tests).
4. **Health.** `GET /health` on `llama-server`: `200` = ready, `503` = loading the model, no answer = down. The AI
   service caches the result (5 s) and maps it to its own `/health`.
5. **Idle unload.** After `idle_unload_minutes` without chat/search traffic the service **stops** both processes to
   give the RAM back. The next chat starts the chat process again and waits for it to load (`ai_starting` if it is
   not ready within the start timeout); the next search starts the embedding process and runs BM25-only until it is
   ready.
6. **Chat calls** go through LiteLLM's `openai/<models.chat>` route with `api_base` = `http://127.0.0.1:<port>/v1`.
   `LITELLM_TELEMETRY=False` and the local model-cost map stay set.

### ADR-008 changes (additive, older tablets keep working)

- `AiServiceHealth` gains **`model_runtime_reachable`** (the chat `llama-server` answered its health check). The
  old **`ollama_reachable`** field stays for one release with the same value, then is removed.
- New degradation reasons: **`model_runtime_unreachable`** (replaces `ollama_unreachable`) and **`model_loading`**
  (with `status: "starting"`). `chat_model_missing` now means "no chat GGUF configured, or the file is not there".
- Routing fields (`routing`, `routes`, `route`) are in ADR-013.

### ADR-003 amendment

The embedding model is still `nomic-embed-text` v1.5 (768-d, same `search_document:` / `search_query:` prefixes),
now as a **GGUF** file served by `llama-server --embedding --pooling mean`. Vector dimension stays 768; a different
model means a re-embed of every chunk (unchanged rule). **Still open (rag-ingest):** confirm the GGUF build runs on
the pinned llama.cpp release, its license, and CPU speed on the 7220.

## Still to check (not measured yet)

- `lfm3-omni` GGUF on the pinned llama.cpp build (text, then image/audio via the projector), RAM and tokens/s on an
  8 GB 8th-gen i5. Measure, don't quote.
- `lfm3-omni` license (LFM Open License v1.0, revenue threshold): owner sign-off before bundling.
- llama.cpp is MIT; its LICENSE goes into the installer's third-party notices.

## Alternatives considered

- **Keep Ollama:** works, but adds a Windows service and a model registry the tablet does not need. Rejected by the owner.
- **`llama-cpp-python` in-process:** one process less, but a model crash takes the whole AI service down and RAM can
  only be freed by restarting it. A child process can be stopped and restarted on its own.

## Consequences

- No `GSE-Ollama` service; the installer bundles `llama-server.exe` (CPU/AVX2 build) and the GGUF files.
- The AI service owns two child processes and must stop them on exit.
- Tests use a fake `llama-server` (ready / loading / down); no model is needed to run them.
