# GSE Tech Pro: OpenCode Starter

This folder is ready to open in OpenCode. It has the spec, the reference screenshots, project rules, and 13 agents.

```
GSE-Tech-Pro/
├── AGENTS.md                  ← rules every agent follows (stack, folders, owners, safety)
├── README.md                  ← this file
├── .opencode/agents/          ← the agents
├── brand/                     ← company logo, app icon, splash, brand colours (generated)
├── Reference/                 ← vendor drivers, SDKs, and reference apps (reference only, never shipped)
└── docs/
    ├── PROMPT.md              ← full product spec
    ├── PROMPT.original.md     ← first draft of the spec, kept for history (agents: ignore)
    ├── Manuals/               ← seed manual library (TUG 660-28 / 600i first)
    ├── logos/                 ← reference images only, not for shipping (trademarks)
    ├── reference-screenshots/ ← Shop Foreman Pro screenshots to copy the layout from
    └── decisions/             ← architecture decision records (filled in by architect)
```

## The agents

| Agent | Type | Does |
|---|---|---|
| **gse-lead** | primary | Plans each phase, hands tasks to the others, checks results. **Talk to this one.** |
| architect | subagent | Architecture, database design, folder structure, shared interfaces |
| ui-winui | subagent | Windows 11 screens copying Shop Foreman Pro's layout, touch-friendly |
| device-comms | subagent | J2534 (RLink X3), RP1210, ECOM, J1939/J1708/OBD, 32-bit device host, simulator |
| scope | subagent | PicoScope + Hantek with one shared scope screen, guided tests |
| database | subagent | SQLite, migrations, full-text + vector search, backups |
| ai-engineer | subagent | llama.cpp + LiteLLM (tablet, shop server, cloud), Tech Chat, AI tools, voice, vision |
| rag-ingest | subagent | PDF manual import, OCR, chunking, hybrid search |
| recommender | subagent | Repair history learning, ranks fixes by success rate |
| gse-domain-expert | subagent | Troubleshooting trees, DTC data, test points, safety warnings (content, not code) |
| installer-deploy | subagent | `GSE-Tech-Pro-Setup.exe`, silent multi-tablet install, updates |
| tester | subagent | Builds, runs tests and simulator checks |
| reviewer | subagent | Read-only review for bugs, safety, offline, performance |

Each agent can only edit its own folders (see `AGENTS.md`). That keeps them from overwriting each other's work.

## How to use it

1. Open this folder in OpenCode:
   ```bash
   cd "C:\Users\cray1\Desktop\GSE_Tech_Pro"
   ```
   ```bash
   opencode
   ```
2. Switch to the **gse-lead** agent (Tab cycles primary agents).
3. Start with:
   > Read docs/PROMPT.md and AGENTS.md. Have the architect write docs/ARCHITECTURE.md with the 6 recommendations, database design, folder structure, AI workflow, and phase plan. Don't write any app code until I approve it.
4. Review `docs/ARCHITECTURE.md`. Ask for changes or approve it.
5. Then: "Start Phase 1." The lead hands tasks to the right agents, sends results to tester and reviewer, and checks back with you at the end of each phase.

The reference screenshots are already in `docs/reference-screenshots/`, so agents read them from there. You don't need to attach them.

**Before the first session (tooling):** run REA setup and REA doctor, and make sure gitnexus and claude-mem are available. The roadmap/phase plan lives in `docs/PLAN.md` (owned by gse-lead). There is no separate `ROADMAP.md`.

**Starting a clean session:** a new session only needs `AGENTS.md`, `docs/PROMPT.md`, `docs/PLAN.md`, and `docs/ARCHITECTURE.md`. Say "Read those and continue with the next task in PLAN.md."

You can also call a subagent directly, e.g. "Use the gse-domain-expert subagent to write a troubleshooting tree for a belt loader with slow hydraulic lift."

## Models

Only `gse-lead` sets its own model in its header. The other agents use whatever model you pick in OpenCode. To give an agent its own model, add a line to its header, e.g.:

```yaml
model: anthropic/claude-sonnet-5-5
```

Suggestion: a strong model for `gse-lead`, `architect`, `device-comms`, and `reviewer`. A cheaper, faster one is fine for `tester`.

Note: `lfm3-omni` is the model **inside the app** for technicians. It is not meant to be the model that writes the code.

## Notes

- These files use the current OpenCode agent format (`.opencode/agents/`, with `permissions` lists). If your OpenCode version is older and ignores them, update OpenCode.
- Agents ask before running shell commands other than builds and tests, and can never `git push`.
- Spec values (pressures, voltages, torque) are never invented. They must come from a manual you import, or they're marked `TODO`.
