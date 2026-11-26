# ADR-014 — Diagnostic routing: VCI selection, vehicle profile & identification, catalog, and OEM-module contracts

- **Status:** Accepted
- **Date:** 2026-11-25
- **Owner:** architect

## Context

`docs/PLAN-vci-vehicle-selection.md` splits the VCI/vehicle-selection feature into 10 steps; step 1
(architect) is the shared contracts in `GseTechPro.Core` that every later step builds against. Four
shape decisions needed to be made up front so `ui-winui` (settings service, modal, sidebar),
`device-comms` (identifier), and `gse-domain-expert` (seeded catalog JSON) don't diverge:

1. How the rich `VehicleProfile` relates to the existing `VehicleInfo`.
2. Whether the device `Kind` stays a string or becomes an enum.
3. Whether the vehicle catalog is sync or async.
4. How OEM modules are matched and resolved (including the generic fallback).

## Decision

### 1. `VehicleProfile` is a richer, separate model from `VehicleInfo`

- Keep **both** records. `VehicleInfo` (in `DiagnosticContracts.cs`) is the *raw* adapter report:
  `Vin`, `Protocol`, `Ecus`. `VehicleProfile` (new, in `Contracts/VehicleSelectionContracts.cs`) is
  the *session-level* model: decoded `Year`/`Make`/`Model`/`Engine`, `Region`, `Source`
  (Auto/Manual), plus `Vin`/`Protocol`.
- The vehicle identifier maps `VehicleInfo` → `VehicleProfile` (`Source = Auto`); the selector modal
  builds a profile directly (`Source = Manual`). The adapter never knows about regions or decoded
  fields, so `VehicleInfo` stays untouched.
- Every unknown field is nullable (`string?`, `int?`, `VehicleRegion?`, `ProtocolType?`) — nothing
  is invented (offline, no VIN-lookup service).

### 2. Device `Kind` stays a string (new `DeviceKinds` constant class)

- `DefaultVciSelection.Kind` and `DeviceInfo.Kind` remain **strings**. Added
  `static class DeviceKinds` (`J2534`/`Rp1210`/`Simulator` = `"j2534"`/`"rp1210"`/`"simulator"`) in
  `HostIpcContracts.cs`, matching the existing `HostStreamKinds` pattern, and pointed
  `DeviceInfo.Kind`'s doc at it.
- Rationale: `Kind` is a string on the wire (ADR-007) and must round-trip losslessly between the
  host's `list-devices`, VCI settings, and any future device class device-comms adds. An enum would
  add a mapping layer and risk drift for no benefit; unknown kinds pass through untouched.

### 3. Vehicle catalog is async-load, sync-query

- `IVehicleCatalog` exposes `Task LoadAsync(ct)` (the only I/O: reading seeded JSON) plus
  **synchronous** `GetYears()` / `GetMakes(region, year?)` / `GetModels(region, year?, make?)`
  (pure in-memory lookups over loaded data). `null` filter = "all"; `region` is always required.
- Rationale: the queries are dictionary reads after a one-time load, so making them async only adds
  plumbing; keeping them sync makes them trivially testable with an in-memory implementation. The
  app calls `LoadAsync` once off the UI thread (startup or lazy), then the modal queries synchronously.
- Return types are small records (`VehicleYear`/`VehicleMake`/`VehicleModel`) so a later step can add
  metadata (e.g. a normalized name for OEM matching) without a breaking change.

### 4. OEM modules: ordered registry, first `CanHandle` match wins

- `IOemModule`: `Id`, `Region` (`VehicleRegion?`, null = region-agnostic), `SupportedMakes`
  (`IReadOnlyList<string>`, empty = any make, case-insensitive), `CanHandle(VehicleProfile?)`
  (default member implementing the shared region+make rule, overridable), and
  `LoadAsync(profile, adapter, ct)`.
- `IOemModuleRegistry`: `Modules` (registration order), `Register(module)` (append; re-registering an
  `Id` replaces in place), `Resolve(profile?) → IOemModule?` (first match or null). Registration
  order is significant: **specific OEM modules first, the generic module last**, so a profile that
  matches no specific module falls through to the generic one, and `null` means "no OEM module
  installed".
- Matching semantics for the default `CanHandle`: a null profile never matches; a make-specific
  module requires a *known, listed* make; a region-specific module requires a *known, matching*
  region. So a partial (auto-detection-failed) profile only matches the generic module.

### 5. `IVehicleIdentifier` returns `VehicleProfile?`

- `IdentifyAsync(IDiagnosticAdapter adapter, ct) → VehicleProfile?` (null = not identified). The
  ~15 s timeout is the implementer's concern (device-comms, step 3), but the contract documents the
  obligations: off the UI thread, honour cancellation, never invent values, simulator-first.

## Alternatives considered

- **Add an enum for `Kind`**: rejected — string on the wire (ADR-007); an enum needs a mapping layer
  and would break on an unknown device class. String constants give named values without a boundary.
- **Fully async catalog** (`GetMakesAsync` etc.): rejected — the queries are pure in-memory lookups;
  async only adds plumbing. Load is async because it is I/O.
- **Merge `VehicleProfile` into `VehicleInfo`**: rejected — `VehicleInfo` is the adapter's raw report
  and must not grow decoded fields; the profile is a separate session model with provenance.
- **Fold `VehicleRegion`/`VehicleProfileSource` into `DbEnum` now**: rejected — nothing persists them
  this step. The `EnumMember` codes are declared (they also name the `data/vehicles/*.json` files) so
  a later persistence change is additive.
- **Make `CanHandle` an abstract member only**: rejected — a default member keeps the generic and
  specific modules consistent and unit-testable with tiny stubs.

## Consequences

- `ui-winui` (step 2): `VciSettingsService` stores `DefaultVciSelection` (Id + Kind, never a
  hardcoded name/DLL path); filters devices by `DeviceKinds`.
- `device-comms` (step 3): `VehicleIdentifier` implements `IVehicleIdentifier`, maps
  `GetVehicleInfoAsync` → `VehicleProfile` (Auto), decodes VIN without inventing values.
- `gse-domain-expert` (step 4): `VehicleCatalog` implements `IVehicleCatalog` over
  `data/vehicles/{domestic,asian,european,heavy_duty}.json` (region string codes match the enum).
- `ui-winui` (steps 5–9): registers a built-in generic `IOemModule` last; on Save resolves a module
  via `IOemModuleRegistry.Resolve` and calls `LoadAsync`, or shows the "no OEM module installed"
  message on null.
- Referenced from `docs/ARCHITECTURE.md` §1.1 (process boundaries) and the decision-records list.
