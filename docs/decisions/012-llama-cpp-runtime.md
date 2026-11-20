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

## Pinned build and tablet settings (2026-11-19)

- **llama.cpp:** `b12359`, the build of release **v1.0.0** (both published 2026-11-13). Windows asset
  `llama-b12359-bin-win-cpu-x64.zip`, 25.1 MB, sha256
  `837c1655e87df3b66989f4be33a792a1399c785bf259038dc75c900096683e69` (GitHub release digest). Liquid's guide says LFM3
  needs v0.8.0 (b11960) or later and that its examples were checked with b12359. MIT; LICENSE goes into the
  installer's third-party notices.
- **Chat model:** LFM3-Omni-2B **Q4_K_M** (1.1 GB), Liquid's "recommended" size/quality balance. Projector
  `mmproj-LFM3-Omni-2B-Q8_0.gguf` only once image/audio input is used (Phase 7); left out until then to save RAM.
- **Flags from Liquid (model card + llama.cpp guide):** `--reasoning off` (LFM3-Omni-2B is a non-reasoning model);
  temperature **0** for text (greedy). Both are in config (`llama_server.extra_args`, `models.chat_temperature`).
- **Our own choices (not from Liquid):** context `8192` (Liquid's server default is 4096; we need room for chat
  history plus tool results), threads = llama.cpp default. Liquid lists no thread, batch, mmap or flash-attention
  setting for LFM3 on CPU.
- **Binary (checked 2026-11-19, zip hash verified, nothing executed):** b12359 ships `llama-server.exe` (and a
  `llama.exe`), so `llama_server.subcommand` stays empty; it exists for builds that only ship `llama serve`. A string
  scan of `llama-common.dll` / `llama-server-impl.dll` finds every flag the service passes: `--model`, `--host`,
  `--port`, `--ctx-size`, `--threads`, `--alias`, `--mmproj`, `--embedding`, `--pooling`, `--reasoning`. The service
  uses the long forms. Still to do on the tablet: one real start with `--reasoning off` and the GGUF.
- **Runtime files:** `llama-server.exe` imports `llama-server-impl.dll`, `llama-common.dll`, `llama.dll`, `mtmd.dll`,
  `ggml.dll`, `ggml-base.dll`, `libomp.dll`; ggml loads one `ggml-cpu-*.dll` for the CPU at run time. Only these
  ship (`Reference\llama.cpp\PINNED.txt`). The zip has no llama.cpp LICENSE; it was taken from the same tag
  (MIT). LLVM OpenMP's license ships too. **ggml needs the Microsoft VC++ 2015–2022 x64 runtime** (`MSVCP140.dll`,
  `VCRUNTIME140.dll`), which the installer must provide.
- **License:** `lfm3-omni` is under the LFM Open License v1.0 (model card: `lfm1.0`). **Owner approved bundling on
  2026-11-19.**

## Still to check (not measured yet)

- `lfm3-omni` Q4_K_M on b12359 on the 7220: RAM and tokens/s on an 8 GB 8th-gen i5, then image/audio via the projector.
  Measure, don't quote.

## Alternatives considered

- **Keep Ollama:** works, but adds a Windows service and a model registry the tablet does not need. Rejected by the owner.
- **`llama-cpp-python` in-process:** one process less, but a model crash takes the whole AI service down and RAM can
  only be freed by restarting it. A child process can be stopped and restarted on its own.

## Consequences

- No `GSE-Ollama` service; the installer bundles `llama-server.exe` (CPU/AVX2 build) and the GGUF files.
- The AI service owns two child processes and must stop them on exit.
- Tests use a fake `llama-server` (ready / loading / down); no model is needed to run them.
