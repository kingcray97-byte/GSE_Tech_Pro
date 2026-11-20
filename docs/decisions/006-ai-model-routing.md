# ADR-006 — Model routing: local-first with opt-in cloud fallback

- **Status:** Superseded by ADR-013 (2026-11-19): three routes (tablet / shop server / cloud), llama.cpp instead of Ollama
- **Date:** 2026-11-15
- **Owner:** architect

## Context

The app is offline-first (no cloud APIs, no telemetry). The user wants an optional higher-capability model when internet is available, without weakening the offline guarantee. All model/provider names must stay in config, never in code.

## Decision

Local `lfm3-omni` is the **default and always works offline**. When (and only when) the shop enables it in settings **and** the internet is reachable, LiteLLM may route chat to a cloud/hosted model (Ollama Cloud or any OpenAI-compatible endpoint) as a higher-capability fallback, automatically falling back to local when offline. Embeddings always stay local (`nomic-embed-text`).

## Alternatives considered

- **Local-only (no cloud tier):** fully preserves offline-first but forgoes the capability boost when online.
- **Cloud by default:** rejected — violates offline-first and would create a runtime dependency.

## Consequences

- This is an **opt-in exception to the offline-first rule**: disabled by default, never a runtime dependency, sends nothing unless turned on, and sends no telemetry.
- Cloud endpoint + model name are **config-only** (LiteLLM provider entry), no hardcoded names.
- Embeddings never leave the tablet, so customer document content is never sent to a cloud provider even when the chat tier is on.
- Referenced from `docs/ARCHITECTURE.md` §1.5, §4.1, §4.6, §7.2.
