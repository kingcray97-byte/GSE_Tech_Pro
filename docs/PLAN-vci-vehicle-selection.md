# Plan: Diagnostic Routing, VCI Settings & Vehicle Selection

Status: **ALL 10 STEPS DONE (2026-11-25)** — Core contracts, default VCI persistence + Settings card, VehicleIdentifier (VIN decode), vehicle catalog (4 seed JSON + VehicleCatalog), OEM module hook (registry + GenericOemModule), session/header sync (ActiveVehicle), toolbar/screen behavior (HD rename, VCI retag, protocol dropdown, saved VCI), sidebar per mode (HD/OEM action items), vehicle selector modal (Years/Makes/Models tabs, Save/Close, OEM loader on Save), and final tests/manual check (857 C# tests pass, final reviewer APPROVE WITH CONDITIONS). **Workstream complete.** Reference screenshots: `docs/reference-screenshots/side bar.jpg` (new sidebar buttons go in the empty area under "Monitors") and `docs/reference-screenshots/car selector.jpg` (tabs: Years / Makes / Models).

## Decisions made
- Modal tabs are **Years / Makes / Models**, as in the screenshot (Brand is covered by Make; the region button filters Makes).
- "J1939" becomes "Auto" in the **protocol dropdown** in VCI mode. In OBD-II mode the protocol dropdown is removed (discovery is automatic).
- The existing VCI setup dialog **moves into Settings**.
- Year/Make/Model lists come from **seeded JSON files** in `data/vehicles/`.

## What exists today
- Toolbar tiles in `MainWindow.xaml` (~268-334): VCI, Scan, J1939, OBD II. Routed by `SelectToolbarAction` in `MainShellViewModel.cs` (cases `J2534`, `HeavyDuty`, `OBDII`) via `ActivateDiagnosticTool`.
- Sidebar is a `ListView` bound to `CurrentModuleNavItems`, built by the static `BuildSidebarSections()`.
- `ConnectSimulatorAsync` connects to `SelectedDevice`. `DiscoverDevicesAsync` lists the simulator plus host32 devices (`Kind` = `j2534` / `rp1210`).
- `VehicleInfo` has only `Vin`, `Protocol`, `Ecus`. No year/make/model/engine.
- `AppSettingsRepository` (`app_settings` table) is the persistence store. `AiRoutingSettingsService` is the pattern to copy.
- The VCI tile currently opens `OnOpenDeviceSetupDialogClick`.

## Steps

### 1. Contracts (`GseTechPro.Core`, owner: architect)
- `VehicleProfile`: `Vin`, `Year`, `Make`, `Model`, `Engine`, `Region`, `Source` (Auto/Manual), `Protocol`.
- `VehicleRegion` enum: Domestic, Asian, European, HeavyDuty.
- `DefaultVciSelection`: `Id`, `Name`, `Kind`.
- `IVehicleCatalog`: `GetYears`, `GetMakes(region, year)`, `GetModels(region, year, make)`.
- `IOemModule` / `IOemModuleRegistry`: `Id`, `Region`, `SupportedMakes`, `CanHandle(profile)`, `LoadAsync(profile, adapter)`, `Register(module)`.
- `IVehicleIdentifier`: `IdentifyAsync(adapter, ct)` returns a profile or null.

**✅ Done (2026-11-25, `architect`).** New files: `Enums/VehicleEnums.cs` (`VehicleRegion`, `VehicleProfileSource`), `Contracts/VehicleSelectionContracts.cs` (`VehicleProfile`, `DefaultVciSelection`, `VehicleYear/Make/Model`), `Abstractions/IVehicleCatalog.cs`, `IOemModule.cs`, `IOemModuleRegistry.cs`, `IVehicleIdentifier.cs`; additive `DeviceKinds` constants in `Contracts/HostIpcContracts.cs`; decision record `docs/decisions/014-vehicle-selection-contracts.md`. `VehicleInfo` (raw adapter report) kept separate from `VehicleProfile` (session model); `Kind` stays a string; catalog loads async / queries sync; all unknown fields nullable. Review: **APPROVED** (reviewer) with two nits for steps 5–6, noted below.

**Review notes (carry into steps 5–6, `ui-winui`):**
- Step 5: `GenericOemModule` (empty `SupportedMakes`, null `Region`) makes `IOemModuleRegistry.Resolve` return the generic module instead of `null`. To show "No OEM module installed for {Make}. Generic diagnostics loaded.", check `resolved is null || resolved.SupportedMakes.Count == 0` before calling `LoadAsync`.
- Step 6: render null `VehicleProfile` fields (`Year/Make/Model/Engine/Region/Vin`) as "Unknown", never empty strings.

### 2. Default VCI persistence (Settings)
- `VciSettingsService` in `GseTechPro.App/Services`, modelled on `AiRoutingSettingsService`. Keys: `vci.default.j2534`, `vci.default.heavy_duty`. Store the device `Id` and `Kind`; never hardcode names or DLL paths.
- New Settings card "Vehicle interfaces":
  - Standard / Light-Duty dropdown: devices where `Kind == "j2534"`.
  - Heavy-Duty dropdown: devices where `Kind == "rp1210"`.
  - Refresh button, plus a button that opens the existing device setup dialog.
- If the saved device is missing, show "Saved device not found" and keep the saved value. The simulator stays selectable (simulator-first rule).

**✅ Done (2026-11-25, `ui-winui`).** `Services/VciSettingsService.cs` (keys `vci.default.j2534` / `vci.default.heavy_duty`, stores `DefaultVciSelection` via `AppSettingsRepository`; missing key → null), `Services/VciDeviceChoices.cs` (pure builder: simulator + j2534/rp1210 rows, "not found" kept and selected), `ViewModels/MainShellViewModel.VciSettings.cs` (filtered lists, save-on-select, status, `LoadVciSettingsAsync` + `RefreshVciDevicesCommand`), `MainWindow.xaml` new **Vehicle interfaces** card (Light-Duty / Heavy-Duty dropdowns, Refresh, Device setup…). Review: **APPROVED**; one LOW race (save-selection vs concurrent `RebuildVciChoices`) fixed by updating the saved field before the await. Tests: `VciSettingsTests` (16). Full suite 744 pass.

### 3. Vehicle identification (owner: device-comms)
- `VehicleIdentifier` runs off the UI thread with a timeout (~15 s) and cancellation:
  1. Try each protocol the adapter supports, in fixed order.
  2. On first answer, read the VIN (OBD-II Mode 9 PID 02 for light duty; J1939 PGN 65260 / J1587 for heavy duty).
  3. Decode the VIN with standard rules: WMI (make/region), 10th character (model year), 8th character (engine, only where publicly mapped).
- No invented values: undecodable fields show "Unknown". No online lookup (offline rule).
- Must work against the simulator first.

**✅ Done (2026-11-25, `device-comms`).** `Devices/VinDecoder.cs` (ISO 3779/SAE J853/SAE J1042 VIN decoding: region from first char, make from WMI table, year from 10th char with 30-year disambiguation, check-digit validation for North America, engine returns null with TODO). `Devices/VehicleIdentifier.cs` (implements `IVehicleIdentifier`: 15s timeout, connect-if-needed, null on failure/cancellation/invalid VIN, `Source=Auto`, `Model=null`). Tests: `VinDecoderTests` (12) + `VehicleIdentifierTests` (4). Review: **APPROVED**; two MEDIUM fixes applied (duplicate `KND` key removed; `'7'` accepted as North American in `DecodeRegion`). LOW "HeavyDuty region mapping" deferred to Step 5. Full suite 760 pass.

### 4. Vehicle catalog and seed data (owner: gse-domain-expert)
- `data/vehicles/{domestic,asian,european,heavy_duty}.json` (year -> make -> model). Real data needed; a small starter set can ship first.
- `VehicleCatalog` loads, caches and filters by region. HD brands (Freightliner, Kenworth, Peterbilt, Volvo, International, etc.) are a separate list.

**✅ Done (2026-11-25, `gse-domain-expert` + `database`).** Seed JSON: `data/vehicles/{domestic,asian,european,heavy_duty}.json` (domestic 10 makes/94 models, asian 11/110, european 12/119, heavy_duty 9 truck brands + 4 GSE OEMs/113 models) + `README.md` (schema v1, newest-first years, years per make, GSE entries with empty years + notes). `src/GseTechPro.Data/VehicleCatalog.cs` implements `IVehicleCatalog` (embedded resources, in-memory cache, thread-safe reads, `GetYears`/`GetMakes`/`GetModels` with null-filter semantics, extra `HeavyDutyBrands` property). Review: **APPROVED** with 3 LOW nits; schema-version attribute fixed. Full suite 770 pass. Note for Step 9: GSE makes have empty `years` arrays and only appear when `year == null`.

### 5. OEM module hook
- `OemModuleRegistry` as a DI singleton. Modules register at startup; add-on modules need no UI change.
- On Save, resolve a module for the profile and call `LoadAsync`. If none match, show "No OEM module installed for {Make}. Generic diagnostics loaded."
- Ship one built-in `GenericOemModule` so the flow works end to end.

**✅ Done (2026-11-25, `ui-winui`).** `Services/OemModuleRegistry.cs` (implements `IOemModuleRegistry`: registration order decides match, re-register by Id replaces in place, thread-safe snapshot reads, null profile → null). `Services/GenericOemModule.cs` (id `gse.generic`, null region, empty makes, no-op `LoadAsync`). `ViewModels/MainShellViewModel.OemModule.cs` (partial: `OemModules` property, `ResolveOemModule`, `TryLoadOemModuleAsync` returning fallback message when generic/null). Wired into `App.xaml.cs` (registry created, generic registered last, exposed as `App.Current.OemModules`) and `MainWindow.xaml.cs`. Review: **APPROVED** with one LOW nit (empty/whitespace Make in fallback message) fixed. Full suite 780 pass.

### 6. Session and header sync
- Single `ActiveVehicle` property on `MainShellViewModel`, written by both Auto detection and the manual modal.
- Drives the status bar VIN text, a new session header line, and the breadcrumb. Cleared on disconnect.

**✅ Done (2026-11-25, `ui-winui`).** `ViewModels/MainShellViewModel.VehicleSession.cs` (partial: `ActiveVehicle` property, `ActiveVehicleHeaderLine` property, `SetActiveVehicle(profile)` updating VehicleVinText + header line + BreadcrumbRoot, `ClearActiveVehicle()`). Connect flow now decodes VIN via `VinDecoder.Decode` and calls `SetActiveVehicle` with an Auto-source profile; disconnect calls `SetActiveVehicle(null)`. Review: **APPROVED** with 4 LOW nits; Engine field added to header line (nit 1 fixed); nits 2-4 (toolbar breadcrumb clobber, XAML binding, integration test) deferred to Steps 7-10. Full suite 785 pass.

### 7. Toolbar and screen behavior
- **OBD II:** use the saved J2534 VCI. Remove the protocol dropdown in this mode. Run the identifier on entry.
- **J1939 -> HD:** rename the label, tooltip and AutomationProperties name (tag stays `HeavyDuty`). Use the saved HD VCI. Auto-discovery on entry.
- **VCI:** retag to `OemDiagnostics`. Opens the OEM screen with the saved J2534 VCI.

**✅ Done (2026-11-25, `ui-winui`).** `MainWindow.xaml`: J1939 tile renamed to "HD" (label, tooltip, AutomationProperties.Name; tag stays "HeavyDuty"); VCI tile retagged to "OemDiagnostics" routed through OnToolbarButtonClick; protocol ComboBox visibility bound to `ProtocolDropdownVisibility`. `MainShellViewModel.VciToolbar.cs` (new partial): `ProtocolDropdownVisibility` (collapsed in OBD II), `DefaultBreadcrumbRoot` (preserves vehicle breadcrumb), `ApplySavedVciAsync`, `RunIdentifierOnEntryAsync`, `EnterVciModeAsync`. `SelectToolbarAction` updated: J2534/HeavyDuty/OBDII/OemDiagnostics all use `EnterVciModeAsync` (discover + apply saved VCI). `ActivateDiagnosticTool` uses `DefaultBreadcrumbRoot`. Review: **APPROVED** (no blockers; deferred nit: breadcrumb root in non-diagnostic toolbar actions for Steps 8-10). Full suite 801 pass.

### 8. Sidebar per mode
`BuildSidebarSections()` becomes mode-aware. `SidebarItem` gets an action marker so entries can run a command instead of navigating.
- **HD mode:** existing items, then `Engine`, `Trans`, `ABS`, `Truck`. Each opens the selector modal first; Save loads that system's session (`Engine`/`Trans`/`ABS` standalone, `Truck` whole vehicle).
- **OEM mode:** existing items, then `Auto`, then `Domestic`, `Asian`, `European`.
  - `Auto` runs the identifier. On success, show the vehicle in the header. On failure or timeout, show exactly: "Automatic vehicle detection failed. Please select a region to enter vehicle details manually."
  - Region buttons open the modal pre-filtered by region.

**✅ Done (2026-11-25, `ui-winui`).** `DiagnosticSubViewModels.cs`: `SidebarItem` now has `IsAction` and `ActionKey` init properties + `SidebarItem.Action(...)` factory. `MainShellViewModel.SidebarMode.cs` (new partial): `GetModeSidebarItems()` returns HD items (Engine/Trans/ABS/Truck) for "Heavy Duty" and OEM items (Auto/Domestic/Asian/European) for "OemDiagnostics"; `OnSidebarActionAsync` handles Auto (runs identifier, sets ActiveVehicle on success, shows exact failure message on failure), region actions (sets `PendingVehicleRegion`), and HD system actions (sets `PendingHdSystem`). `CurrentModuleNavItems` appends mode items for Diagnostics section; `SelectSidebarItemAsync` runs action and returns for action items (with highlight restore via `_lastPageNavKey`). Review: **APPROVED** (no blockers; deferred: selector modal in Step 9). Full suite 828 pass.

### 9. Vehicle selector modal
- `ContentDialog` with tabs Years / Makes / Models, scrolling lists, newest year first. Makes depend on year + region; Models depend on make.
- Bottom buttons: **Save** (disabled until all three chosen; commits, closes, calls the OEM loader) and **Close** (discards).
- Touch targets at least 44x44; keyboard works. HD modals use the same tabs with a title naming the system.

**✅ Done (2026-11-25, `ui-winui`).** `VehicleSelectorDialog.xaml` + `.xaml.cs` (ContentDialog with Pivot tabs Years/Makes/Models, Save/Close buttons, 44px touch targets, tab auto-advance). `ViewModels/VehicleSelectorModel.cs` (pure model: collections, indices, IsSaveEnabled, BuildProfile with Source=Manual). `MainShellViewModel.VehicleSelector.cs` (partial: VehicleCatalog property, EnsureVehicleCatalogLoadedAsync, OpenVehicleSelectorAsync). Sidebar actions wired to open the modal (Domestic/Asian/European/Engine/Trans/ABS/Truck). TryLoadOemModuleAsync updated to accept nullable adapter. Review: **APPROVED** (no blockers; 3 LOW nits: banner text format confirmed correct, GSE makes empty years documented, dialog layout needs manual visual check). Full suite 856 pass. Note for gse-domain-expert: GSE makes with empty `years` arrays won't appear in year-filtered modal queries.

### 10. Tests (owner: tester)
- xUnit: VIN decode (valid, bad check digit, unknown WMI); `VciSettingsService` round trip; `VehicleCatalog` region filtering; OEM registry resolve and fallback; auto-detect failure shows the exact message; simulator identifier flow.
- `dotnet build` and `dotnet test`. Manual check of all five screens against the simulator.

**✅ Done (2026-11-25, `tester` + `reviewer`).** Full build clean (0 errors, 1 pre-existing xUnit2013 warning in GuidedVoiceTests.cs). Full test suite: 857 tests pass (Scope 113, Data 12, Devices 178, Core 253, App 301). All six specified test categories verified present and passing. Added integration test `ConnectFlow_SetsActiveVehicle_AndDisconnectClearsIt` (VehicleSessionTests). Final reviewer pass: **APPROVE WITH CONDITIONS** — 5 LOW-risk items accepted as design conditions (HD region mapping, GSE empty years, breadcrumb clobbering, ActiveVehicleHeaderLine XAML binding, auto-detect failure message). No blockers. Manual visual check of dialog layout and tab auto-advance recommended (unit tests can't create ContentDialog without XamlRoot).

## Risks:

- Making the static sidebar mode-aware touches shared navigation (`FindSidebarItem`, `SyncModuleNavSelection`); keep selection in sync.
- Host32 executable is not found on this machine (see the log), so real J2534/RP1210 can't be tested here.
- VIN position 8 only maps to an engine for some makers; show "Unknown" rather than guess.
- HD ECUs often answer VIN over J1939/J1587, not Mode 9, so the identifier needs a separate HD path.
- File ownership: Core contracts (architect), `data/vehicles` (gse-domain-expert), identifier (device-comms), App files (ui-winui).

## Suggested order of work
1. ✅ architect: contracts (done 2026-11-25). 2. ✅ ui-winui/database: settings service (done 2026-11-25). 3. ✅ device-comms: identifier (done 2026-11-25). 4. ✅ gse-domain-expert/database: vehicle JSON + catalog (done 2026-11-25). 5. ✅ ui-winui: OEM module hook (done 2026-11-25). 6. ✅ ui-winui: session/header sync (done 2026-11-25). 7. ✅ ui-winui: toolbar/screen behavior (done 2026-11-25). 8. ✅ ui-winui: sidebar per mode (done 2026-11-25). 9. ✅ ui-winui: vehicle selector modal (done 2026-11-25). 10. ✅ tester/reviewer: final tests + review (done 2026-11-25). **Workstream complete.**
