# GSE Tech Pro — IT Deployment Guide

This guide covers installing, upgrading, and managing GSE Tech Pro across multiple tablets in a shop environment.

## What Gets Installed

`GSE-Tech-Pro-Setup.exe` installs the following components:

| Component | Location | Purpose |
|-----------|----------|---------|
| **GSE Tech Pro App** (x64) | `C:\Program Files\GSE Tech Pro\` | Main WinUI 3 application |
| **Device Host** (x86) | `C:\Program Files\GSE Tech Pro\DeviceHost32\` | 32-bit process for J2534/RP1210 vendor DLLs |
| **Python AI Service** | `C:\Program Files\GSE Tech Pro\AiService\` | Local FastAPI service (RAG, tools, voice, vision) |
| **Ollama Runtime** | `C:\Program Files\GSE Tech Pro\Ollama\` | Local model inference engine |
| **AI Models** | `C:\Program Files\GSE Tech Pro\Ollama\models\` | lfm3-omni (~2.4 GB) + nomic-embed-text (~274 MB) |
| **Database & User Data** | `%APPDATA%\GSE Tech Pro\Data\` | SQLite DB, manuals, logs (per-user) |

### Runtime Dependencies

The installer bundles or installs these runtimes:

- **.NET 8 Desktop Runtime (x64)** — for the main app
- **.NET 8 Desktop Runtime (x86)** — required for the 32-bit device host (J2534/RP1210 DLLs are 32-bit)
- **Windows App SDK** — for WinUI 3 controls and Mica/notifications

> **Note:** Vendor diagnostic drivers (TOPDON RLink X3, Nexiq USB-Link, Noregon DLA+, PicoScope, Hantek) must be installed separately by the shop. They are not bundled due to proprietary licensing.

## System Requirements

| Requirement | Minimum | Recommended |
|-------------|---------|-------------|
| OS | Windows 11 (64-bit) | Windows 11 Pro |
| CPU | Intel Core i5 (8th gen or newer) | Intel Core i7 |
| RAM | 8 GB | 16 GB |
| Disk Space | 10 GB free | 20 GB free (with model + manuals) |
| Display | 1920×1080 touch | 1920×1080 touch |

## Installation Methods

### Interactive Install (Single Tablet)

1. Copy `GSE-Tech-Pro-Setup.exe` to the tablet (USB or network share).
2. Double-click and follow the wizard.
3. Choose **Full installation** for app + AI service + Ollama + model, or **Core only** if using remote AI.
4. After install, the first-run setup wizard launches automatically.

### Silent Install (Multi-Tablet Deployment)

For deploying to many tablets via USB or Intune:

```bash
GSE-Tech-Pro-Setup.exe /SILENT /CONFIG="C:\path\to\config.ini"
```

Create a config file from `installer/config-template.ini` with shop-specific values (shop name, technicians, devices). The installer reads this and pre-configures the app without user interaction.

#### USB Deployment Example

1. Copy `GSE-Tech-Pro-Setup.exe` and your `config.ini` to a USB drive.
2. On each tablet, run:
   ```bash
   D:\GSE-Tech-Pro-Setup.exe /SILENT /CONFIG="D:\config.ini"
   ```

#### Intune Deployment Example

1. Upload `GSE-Tech-Pro-Setup.exe` to Microsoft Endpoint Manager as a Win32 app package.
2. Set the install command:
   ```bash
   GSE-Tech-Pro-Setup.exe /SILENT /CONFIG="%TEMP%\gse-config.ini"
   ```
3. Include your `config.ini` in the deployment payload and copy it to `%TEMP%` before running the installer (via a detection script or pre-install task).

## First-Run Setup Wizard

After installation, the first-run wizard collects:

1. **Shop name** — appears on work orders and printouts
2. **Technician list** — names for the technician dropdown
3. **Connected devices** — which diagnostic adapters are available at this station
4. **Unit system preference** — imperial or metric (can be overridden per reading)

If a silent install config file was provided, these values are pre-populated and the wizard can be skipped.

## Upgrading

To upgrade GSE Tech Pro:

1. Run the new version's `GSE-Tech-Pro-Setup.exe`.
2. The installer backs up the existing database before upgrading.
3. **User data is preserved:** work orders, equipment records, manuals, and settings remain intact.
4. Only application files are replaced; user data in `%APPDATA%\GSE Tech Pro\` is never deleted during an upgrade.

### USB Update Channel

For shops without internet access:

1. Download the update package on a connected computer.
2. Copy to USB drive and transfer to tablets.
3. Run the installer as above — it detects the existing installation and upgrades in place.

## Uninstalling

To uninstall GSE Tech Pro:

- **Via Settings:** Windows Settings → Apps → Installed apps → GSE Tech Pro → Uninstall
- **Via Control Panel:** Programs and Features → GSE Tech Pro → Uninstall

During uninstall, you are prompted whether to keep or remove user data (database, manuals, logs). Choose "Yes" to remove all data, "No" to keep it for potential reinstallation.

## Pre-Staging Manuals

To deploy a manual library across multiple tablets:

1. Import manuals on one tablet using the app's manual import feature.
2. Copy the `%APPDATA%\GSE Tech Pro\Manuals` folder to a USB drive or network share.
3. On each target tablet, copy the manuals folder to its `%APPDATA%\GSE Tech Pro\Manuals` location.
4. The app will detect and index the new manuals on next launch.

Alternatively, include the manual library in your silent install config by setting up a shared network location for manuals.

## Troubleshooting

### Installer Fails to Start

- Verify Windows 11 (64-bit) is installed.
- Check that at least 10 GB of disk space is available.
- Run the installer as Administrator.

### App Won't Launch After Install

- Ensure .NET 8 Desktop Runtime (both x64 and x86) are installed: check Windows Settings → Apps → Installed apps.
- Verify the Windows App SDK is present.
- Check the event log for WinUI-related errors.

### AI Service Not Responding

- Confirm Ollama is running: open Task Manager and look for `ollama.exe`.
- Check that the model files exist in `C:\Program Files\GSE Tech Pro\Ollama\models\`.
- Verify port 8765 (AI service) and 11434 (Ollama) are not blocked by firewall.

### Device Host Issues (J2534/RP1210)

- Ensure the vendor driver is installed separately for your diagnostic adapter.
- Check that `GseTechPro.Devices.Host32.exe` is running in Task Manager when a device tool is open.
- Verify the 32-bit .NET runtime is installed (required for loading 32-bit vendor DLLs).

### Log Locations

| Component | Log Location |
|-----------|--------------|
| App logs | `%APPDATA%\GSE Tech Pro\Data\logs\app.log` |
| AI service logs | `%APPDATA%\GSE Tech Pro\Data\logs\ai-service.log` |
| Ollama logs | `C:\Program Files\GSE Tech Pro\Ollama\ollama.log` |
| Device host logs | `%APPDATA%\GSE Tech Pro\Data\logs\device-host.log` |

## Phase 8 Features (Implemented)

The full installer includes the following production features:

### Code Signing
Authenticode code signing is supported via environment variables. Set `SIGNTOOL_CERT` and `SIGNTOOL_PASS` before building the installer to sign all binaries with your certificate. The installer uses DigiCert timestamping for long-term validity.

### Runtime Bundling
The installer bundles self-contained .NET 8 Desktop Runtime (both x64 and x86) and Windows App SDK, so no separate runtime installation is required on target tablets. This enables true offline deployment with zero prerequisites.

### Ollama Service Installation
When the "full" install type is selected, Ollama is automatically installed as a Windows service (`GSE-Ollama`) that starts on boot. The service runs under LocalSystem and manages the local model runtime lifecycle. On uninstall, the service is cleanly removed.

### Model Download Progress Indication
During installation of large AI models (lfm3-omni ~2.4 GB, nomic-embed-text ~274 MB), the installer displays progress messages indicating which model is being installed and its approximate size.

### Driver Presence Detection
After installation, the installer automatically detects missing vendor diagnostic drivers (TOPDON RLink X3, Nexiq USB-Link, Noregon DLA+, PicoScope, Hantek 6074BE) and displays guidance for installing them separately from manufacturer websites. This does not affect core app functionality.

### Automatic Update Checking
An optional "Check for updates on startup" task is available during installation. When enabled, the app checks an online update channel (`updates.commercialtruckandauto.com`) for new versions and prompts the user to download updates. The check fails silently when offline (offline-first design).
