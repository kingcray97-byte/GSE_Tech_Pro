# ADR-002 — Encryption at rest: BitLocker now, SQLCipher deferred

- **Status:** Accepted (pending user approval)
- **Date:** 2026-11-15
- **Owner:** architect

## Context

Tablet data (fleet repair history, technician names, salted PINs) must be protected at rest. We must choose between relying on OS-level encryption (BitLocker/device encryption) and app-level DB encryption (SQLCipher).

## Decision

Rely on **BitLocker/Windows device encryption** as the at-rest control for now. **Defer SQLCipher** behind the repository boundary. Add **AES-encrypted backups** for USB export immediately. Technician PINs are salted hashes regardless.

## Alternatives considered

- **SQLCipher now:** protects the DB file even on a removed disk and independent of OS state. Rejected for now because it conflicts with the **sqlite-vec C extension** and FTS5, adds CPU on an 8 GB no-GPU tablet, and complicates `VACUUM`/backup/restore. It remains a valid future option.

## Consequences

- First-run wizard verifies BitLocker is on and warns loudly if not.
- Data layer hides all DB access behind repositories so SQLCipher can be enabled later without touching the UI or business logic.
- USB backup/export is AES-encrypted; PINs never stored reversibly.
- Trade-off: if a disk is pulled and BitLocker was off, the raw DB is readable — mitigated by the wizard warning and the fleet's standard BitLocker posture on the Dell 7220 (TPM present).
