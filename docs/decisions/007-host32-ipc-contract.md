# ADR-007 — App↔32-bit device host IPC: pipe handoff + token + command protocol + streaming

- **Status:** Accepted
- **Date:** 2026-11-15
- **Owner:** architect

## Context

Phase 1 left the app↔`GseTechPro.Devices.Host32.exe` pipe half-defined: the host generated its own random pipe name and nobody could learn it, and `HostContracts.cs` was a `{ Command }`/`{ Ok, Message, Status }` stub. Phase 2 needs a concrete, implementable contract for `device-comms` to build the IPC client and the x86 host against, including how the 32-bit J2534/RP1210 vendor DLLs are driven and how the app can tell simulated data from real hardware.

## Decision

**The app is the orchestrator and owns the session identity.** It generates a per-session pipe name `GseTechPro.Devices.Host32-{guid:N}` **and** a per-launch token of 32 random bytes (64 lowercase hex chars), and launches the host with `--pipe <name> --token <token>`. The host stops inventing its own name.

**Token handshake.** The first frame after connect is a `hello` JSON carrying the token; the host compares it (constant-time) and replies `hello-ack`, closing the pipe on `unauthorized` before dispatching any device command.

**Command protocol.** One JSON control envelope: request `{ type, id, command, params }` → response `{ type, id, ok, result|error }` with a correlation `id` and a small fixed error-code list. Full Phase 2 command set: `list-devices`, `connect`, `disconnect`, `read-dtcs`, `clear-dtcs`, `read-live-data`, `subscribe-live-data`/`unsubscribe-live-data`, `read-freeze-frame`, `get-vehicle-info`, `ping` (heartbeat).

**Streaming.** Frames become `[1-byte kind][4-byte LE length][payload]` (`0x01` JSON, `0x02` binary, `0x03` scope). Live data streams as JSON `stream` frames; high-rate CAN capture uses `0x02` binary frames whose layout a preceding JSON `stream` frame declares.

**Provenance.** `IDiagnosticAdapter.IsSimulated` (default interface member `=> false`, so Phase-1 code keeps compiling), `DeviceInfo.IsSimulated`, and `PidValue.IsSimulated`/`DtcReadout.IsSimulated` carry real-vs-simulated end-to-end. The simulator must override `IsSimulated => true` (one line, owned by `device-comms`).

The wire DTOs/constants live in `src/GseTechPro.Core/Contracts/HostIpcContracts.cs` (Core stays free of `System.IO.Pipes`); the byte framing is implemented in `GseTechPro.Devices.Ipc`. Full normative text is in `docs/ARCHITECTURE.md` §1.1.1.

## Alternatives considered

- **Host keeps inventing the pipe name and publishes it out-of-band** (stdout line / file): fragile to parse, racy, and gives the app no clean way to authenticate. Rejected.
- **Token on stdin instead of `--token` arg**: adds a bootstrap handshake for no security gain here (pipe is already `CurrentUserOnly`); command-line token is acceptable residual risk because the token is never logged and the pipe is same-user only. Rejected for simplicity.
- **gRPC / HTTP between app and host**: rejected in ADR-001/§1.1 already — a port + HTTP stack for a same-machine 32-bit boundary is pure overhead.
- **Required (abstract) `IsSimulated` instead of a default member**: cleaner compile-time safety, but it breaks the Phase-1 `SimulatedDiagnosticAdapter` build, which is owned by `device-comms` and out of the architect's edit scope. Default member + a flagged one-line override is the pragmatic equivalent.

## Consequences

- `device-comms` has a precise, implementable spec for the IPC client, the `PipeMessage`→`PipeFrame` binary extension, and the host32 `--pipe`/`--token` arg parsing + token handshake.
- Until `device-comms` adds `SimulatedDiagnosticAdapter.IsSimulated => true` (and sets `IsSimulated = true` on its `PidValue`/`DtcReadout`), the simulator still reports `IsSimulated = false` — a known, temporary labeling gap, not a build break.
- The frame `kind` byte is settled once now, so the same framing serves scope streaming (Phase 6) and CAN capture (Phase 7) without a second redesign.
- Referenced from `docs/ARCHITECTURE.md` §1.1, §1.1.1, §2.2, and the decision-records list.
