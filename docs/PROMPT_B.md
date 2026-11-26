# Feature Implementation Specification: Diagnostic Routing, VCI Settings & Vehicle Selection

Implement the following UI updates, hardware VCI persistence, and diagnostic workflow changes. Maintain the existing application architecture, styling, and theme structure.

---

## 1. Visual Reference Assets
Use these local screenshots as layout and placement references:
* **Sidebar Button Placement:** `F:\GSE_Tech_Pro\docs\reference-screenshots\side bar.jpg` (Place new sidebar buttons inside the area highlighted with the white outline).
* **Vehicle Selector Modal:** `F:\GSE_Tech_Pro\docs\reference-screenshots\car selector.jpg` (Match the top tab labels and overall modal layout).

---

## 2. Settings Menu: Default VCI Persistence
* **Standard / Light-Duty VCI:** Populate a dropdown with all detected SAE J2534 Pass-Thru interfaces. Allow the user to select a default J2534 VCI and persist this selection to application config/memory.
* **Heavy-Duty VCI:** Add a second dropdown for selecting and persisting a default Heavy-Duty VCI (e.g., RP1210 / J1939 interface) to application config/memory.

---

## 3. Core Diagnostic Modules & Button Behavior

### A. Automatic Protocol & VIN Reader
* Implement an automated background discovery routine that scans supported protocols on the active VCI, reads the vehicle VIN, decodes vehicle metadata (Year, Make, Model, Engine), and displays the resolved vehicle info in the UI.

### B. OBD-II Mode (`OBD II` Button)
* **Connection:** Automatically initialize communication using the **saved Default J2534 VCI** from Settings.
* **Protocol Discovery:** Remove the redundant `OBD2` button inside the OBD-II Diagnostic screen. Trigger automatic protocol discovery immediately upon entering the screen.

### C. Heavy-Duty Mode (`HD` Button — formerly `J1939`)
* **Main Entry:** Rename the `J1939` button to `HD` (Heavy Duty). Connect using the **saved Default Heavy-Duty VCI** from Settings.
* **Sidebar Updates:** Inside the Heavy-Duty Diagnostic screen, add the following buttons to the sidebar area outlined in white in `side bar.jpg`:
  * `Engine`
  * `Trans`
  * `ABS`
  * `Truck`
* **Standalone System Modals:** `Engine`, `Trans`, and `ABS` operate as standalone diagnostic systems. Clicking any of these four opens a popup modal to manually select **Brand**, **Year**, **Make**, and **Model** before loading that system's diagnostic session.

### D. OEM-Specific Mode (`VCI` Button)
* **Main Entry:** Repurpose the `VCI` button to launch OEM-specific diagnostics using the **saved Default J2534 VCI** from Settings.
* **Screen Layout & Sidebar Updates:** Keep the standard diagnostic screen layout, but apply these sidebar changes (referencing the white-outlined area in `side bar.jpg`):
  * Rename the `J1939` button to `Auto`.
  * Add three region buttons to the sidebar: `Domestic`, `Asian`, and `European`.
* **`Auto` Button Logic:**
  * Run automatic vehicle detection (VIN + protocol scan) and display the identified vehicle information.
  * **Error Handling:** If auto-detection fails or times out, display a clear user message: *"Automatic vehicle detection failed. Please select a region to enter vehicle details manually."*
* **Manual Vehicle Selector Modal (`Domestic` / `Asian` / `European`):**
  * Clicking a region button opens a tabbed popup modal modeled after `car selector.jpg`.
  * **Top Tabs:** Use the exact tab labels shown in `car selector.jpg` to step through **Brand**, **Year**, **Make**, and **Model** (filtered by the selected region).
  * **Bottom Action Buttons:** Include two buttons at the bottom: `Save` and `Close`.
  * **Save Behavior:** Clicking `Save` commits the selected vehicle profile, automatically closes the modal, and loads the corresponding OEM protocol module (design the module loader with a clean plugin/interface hook for future add-on OEM modules).

---

## 4. Implementation Constraints
1. **No Hardcoded Hardware:** Always read the active VCI handle from the persisted Settings state rather than hardcoding device names or DLL paths.
2. **Modular OEM Hooks:** Structure the OEM protocol loader so future brand-specific diagnostic modules can be registered dynamically without rewriting the UI layer.
3. **State Sync:** Ensure the selected vehicle profile (whether resolved via `Auto` or saved via the manual selector modal) updates the active session header across the diagnostic screen.