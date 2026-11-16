# ADR-004 — App icon: separate GSE Tech Pro icon (Option A)

- **Status:** Decided — Option A approved by the user (2026-11-15)
- **Date:** 2026-11-15
- **Owner:** architect

## Context

The product is "GSE Tech Pro," published "by Commercial Truck and Auto" (CTA). We must decide whether the app icon is the existing CTA mark or a new product-specific mark. The CTA truck is unreadable below 48 px (`brand/README.md`; the current generator already falls back to bold "CTA" letters at 16–40 px).

## Decision

**Option A — separate GSE Tech Pro icon.** A new own-drawn product mark for the title bar, Start menu, and `cta.ico`. The CTA logo appears on the splash and About box as "by Commercial Truck and Auto."

## Alternatives considered

- **Option B — CTA mark as the app icon.** Simplest and strongest company branding, but the truck is unreadable at small sizes on the rugged tablet's taskbar. Not chosen.

## Consequences

- The splash/About carry the CTA logo + "by Commercial Truck and Auto," and the chosen palette (light `#2E3391`, dark `#8C93F0`, sunlight `#1E2FB8`) drives recoloring the CTA logo to match the app.
- A separate icon needs design work (own vector mark, Segoe Fluent Icons or our own art); `tools/brand` must be rebuilt/added before recoloring assets.
