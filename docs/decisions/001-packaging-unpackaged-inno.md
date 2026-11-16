# ADR-001 — Unpackaged + Inno Setup (not MSIX)

- **Status:** Accepted (pending user approval)
- **Date:** 2026-11-15
- **Owner:** architect

## Context

The WinUI 3 app must be installed by a single signed `GSE-Tech-Pro-Setup.exe` on many rugged tablets (silent install via `/SILENT` + config, USB or Intune), upgrade while keeping user data, support a USB update channel, and show Windows 11 Mica + notifications. We must choose early whether the app is packaged (MSIX) or unpackaged, because it determines the installer, updates, and several OS integrations.

## Decision

Ship the app **unpackaged** (self-contained `.exe` + Windows App SDK runtime), installed and updated with **Inno Setup**.

## Alternatives considered

- **MSIX (packaged):** clean identity and updates via the Store model, first-class toast/background identity. Rejected because silent fleet deployment is awkward (provisioning, certs), there is no clean "silent install + config file" story, upgrades/updates that keep a mutable per-tablet SQLite DB and user manuals folder fight file-system virtualization, and it drags in Store/identity complexity we don't need offline.

## Consequences

- ✅ Trivial silent multi-tablet install, upgrades keep data, USB update channel, single Authenticode-signed `.exe`.
- ⚠️ **Mica**: set the system backdrop directly (Windows App SDK ≥ 1.2 supports Mica unpackaged).
- ⚠️ **Toast notifications**: need an App User Model ID + Start-menu shortcut; Inno creates the shortcut, so it's a one-time config.
- No Store dependency; no registry/virtualization surprises.
