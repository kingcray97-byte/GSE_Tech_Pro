# ADR-013 — AI routing: tablet, shop server, or cloud (opt-in, online only)

- **Status:** Accepted (owner's decision, 2026-11-19). Changes AGENTS.md rule 1 (cloud is an opt-in exception).
- **Date:** 2026-11-19
- **Owner:** architect
- **Supersedes:** ADR-006 (local-first with opt-in cloud fallback). Folds in the draft `0002-ai-routing-shop-server-and-cloud.md`.

## Decision

The AI service sends each chat question through **LiteLLM** to one of three routes:

| Route | What it is | Data leaves the tablet? |
|---|---|---|
| **`tablet`** | `llama-server` on this tablet (ADR-012). Always there; the last fallback. | No |
| **`shop-server`** | The shop's own LiteLLM proxy (bigger model on shop hardware), over the shop LAN or a private tunnel. | Only to the shop's machine |
| **`cloud`** | A hosted model through LiteLLM, only when online and allowed. | Yes, to the provider |

Routing modes (`AiRoutingMode`, chosen in the app):

- **`tablet-only`** (default): never leaves the tablet.
- **`prefer-shop-server`**: shop server if configured and healthy, else tablet. Never the cloud.
- **`prefer-cloud`**: cloud if allowed and reachable, else shop server if configured and healthy, else tablet.

The order is one pure function in both languages: `AiRouting.Plan` (`src/GseTechPro.Core/Ai/AiRouting.cs`) and
`app.routing.plan_routes` (`ai-service/app/routing.py`). They must agree; tests pin the same table.

## Rules

1. **Offline still works.** No remote route is ever required. A remote route whose last health check failed is
   skipped without waiting. A route that errors falls back to the next one; the tablet is always tried last.
2. **Cloud is off by default, two keys.** The service uses the cloud only when **both** the service config has
   `routes.cloud.enabled: true` (set by the installer/administrator) **and** the request says `cloud_allowed: true`
   (Setup > AI service, administrator PIN). Any technician can pick `tablet-only` for a job.
3. **Job details stay home unless allowed.** For the cloud route, unless the request also has
   `share_job_details_with_cloud: true`, the service drops the work order / asset context line and offers only the
   `search_manuals` tool (no asset, repair-history, part-history, DTC-by-asset or reading tools). Manual passages, the
   system prompt and the conversation go. The shop server is shop-owned and gets the same context as the tablet.
4. **The app never talks to a remote model and holds no secrets.** It only calls the AI service on 127.0.0.1.
   Keys for the shop proxy and the cloud provider are in **Windows Credential Manager** (service name
   `GSE Tech Pro AI`, user = the route's `key_name`), read through `keyring`. A config file that contains a key
   (`api_key`, `key`, `token`, `secret`, `password` under `routes`) is refused at start-up. Keys are never logged.
5. **Every answer says where it came from.** `ChatResponse.route` (`tablet` / `shop-server` / `cloud`) and
   `ChatResponse.model`. The app shows a route badge on each answer; `CLOUD` is clearly marked.
6. **Same safety rules on every route.** The system prompt, tool-sourced citations, the unverified-value note and the
   "AI suggestion" flag are the service's job, so a remote model follows them too.
7. **No telemetry** on any route (`LITELLM_TELEMETRY=False`).
8. **Embeddings never leave the tablet.**

## API changes (additive to ADR-008)

`POST /chat` request gains an optional object (missing = `tablet-only`):

```json
"routing": { "mode": "tablet-only | prefer-shop-server | prefer-cloud",
             "cloud_allowed": false, "share_job_details_with_cloud": false }
```

`POST /chat` response gains `"route": "tablet | shop-server | cloud"` and `"model": "<label>"`.

`GET /health` gains one entry per route:

```json
"routes": [
  { "route": "tablet", "configured": true, "available": true, "model": "lfm3-omni", "message": "Ready" },
  { "route": "shop-server", "configured": false, "available": false, "model": null, "message": "Not set up" },
  { "route": "cloud", "configured": true, "available": false, "model": "<provider/model>", "message": "Offline" }
]
```

## How the service does it

- **Config** (`routes:` in `config.yaml`): `shop_server` and `cloud` each have `enabled`, `model` (a LiteLLM model
  string), `api_base`, `key_name`; cloud also has `probe_host` (`host:port` for the online check). `health_ttl_s`
  (15), `health_timeout_s` (1.5). Provider/model are left blank in the template until the owner picks one.
- **Health checks** (cached, short timeouts): tablet = `llama-server` `GET /health`; shop server = LiteLLM proxy
  `GET /health/liveliness` (no auth); cloud = a TCP connect to `probe_host` (or the `api_base` host, port 443) with
  nothing sent, plus "key present in Credential Manager".
- **Fallback:** each route runs the whole tool loop; a `ChatUnavailable` from a remote route marks it down (until the
  next health check) and the next route runs from the start. The response is one JSON body, so two answers never mix.

## Still to decide (owner)

- Which cloud provider/model and its data terms (no training on shop data, retention). Config only.
- Tunnel product and who runs the shop server.
