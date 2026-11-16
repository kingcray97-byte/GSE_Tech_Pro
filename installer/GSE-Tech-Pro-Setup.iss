; ============================================================================
; GSE Tech Pro — Installer Script (Inno Setup)
; Phase 1: Skeleton to surface packaging issues early.
; Phase 8: Fill in real payloads, signing, driver bundling decisions.
;
; Architecture reference: docs/ARCHITECTURE.md §1.1 (unpackaged + Inno, ADR-001)
; Spec reference:         docs/PROMPT.md deployment section
; ============================================================================

#define AppName "GSE Tech Pro"
#define AppVersion "0.1.0"
#define AppPublisher "Commercial Truck and Auto (CTA)"
#define AppURL "https://www.commercialtruckandauto.com"
#define AppSupportURL "https://support.commercialtruckandauto.com/gse-tech-pro"

; AUMID required for toast notifications on unpackaged WinUI 3 apps (ADR-001)
#define AppUserModelId "CommercialTruckAndAuto.GSETechPro"

; Default install directory — per-machine, Program Files
#define DefaultInstallDir "{pf}\GSE Tech Pro"

[Setup]
AppId={{A7F2E9C4-8B3D-4E1A-B6F5-C0D2E8A9B1C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppSupportURL}
DefaultDirName={#DefaultInstallDir}
DisableProgramGroupPage=yes
OutputBaseFilename=GSE-Tech-Pro-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64
PrivilegesRequired=admin
WizardStyle=modern

; TODO Phase 8: Code signing (user provides cert, never commit keys)
; SignTool=signtool.exe sign /f "$CERT" /p "$PASS" /tr http://timestamp.digicert.com /td sha256 /fd sha256 "$f"

[Types]
Name: "full"; Description: "Full installation (app, AI service, Ollama, model, manuals)"; Flags: iscustom
Name: "core"; Description: "Core app only (no AI/model — for shops using remote AI)"

[Components]
; Core application components
Name: "app"; Description: "GSE Tech Pro application"; Types: full core; Flags: fixed
Name: "device-host"; Description: "32-bit device host (J2534/RP1210)"; Types: full core; Flags: fixed

; AI stack — optional for shops using remote/cloud AI
Name: "ai-service"; Description: "Python AI service (FastAPI, RAG, tools)"; Types: full
Name: "ollama"; Description: "Ollama local model runtime"; Types: full
Name: "model-lfm3-omni"; Description: "lfm3-omni chat/vision/audio model (~2.4 GB)"; Types: full; DependsOn: ollama
Name: "model-nomic-embed"; Description: "nomic-embed-text embedding model (~274 MB)"; Types: full; DependsOn: ollama

; Content
Name: "manuals-default"; Description: "Default manual library (TUG 660-28, 600i seed)"; Types: full core
Name: "demo-data"; Description: "Demo work orders and equipment data"; Flags: fixed

[Files]
; ============================================================================
; APP PAYLOAD — TODO Phase 8: Replace placeholder paths with real build output
; ============================================================================

; Main WinUI app (x64)
Source: "..\src\GseTechPro.App\bin\x64\Release\net8.0-windows10.0.19041.0\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app

; 32-bit device host (x86) — loads J2534/RP1210 vendor DLLs
Source: "..\src\GseTechPro.Devices.Host32\bin\x86\Release\net8.0-windows10.0.19041.0\publish\*"; DestDir: "{app}\DeviceHost32"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: device-host

; ============================================================================
; RUNTIME PREREQUISITES — TODO Phase 8: Bundle or install at runtime
; ============================================================================

; .NET 8 Desktop Runtime (x64) for the main app
; Option A: Use Inno's [Setup] Prerequisites directive to download/install
; Option B: Bundle self-contained publish output (larger installer, no network needed)
; Decision pending from architect — see handoff notes.

; .NET 8 Desktop Runtime (x86) for the 32-bit device host
; REQUIRED because J2534/RP1210 vendor DLLs are 32-bit and cannot load in a 64-bit process.
; This is separate from the x64 runtime — both must be present.

; Windows App SDK (WinUI 3) — required for unpackaged WinUI apps
; Option A: Bundle via self-contained publish or Inno's [Setup] Prerequisites
; Option B: Require pre-installation on target machines
; Decision pending from architect.

; ============================================================================
; AI SERVICE PAYLOAD — TODO Phase 8
; ============================================================================

; Embedded Python runtime (3.12) + all packages (no pip at install time)
Source: "..\ai-service\dist\python-embedded\*"; DestDir: "{app}\Python"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai-service

; AI service application code
Source: "..\ai-service\app\*"; DestDir: "{app}\AiService"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai-service
Source: "..\ai-service\tools\*"; DestDir: "{app}\AiService\tools"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai-service
Source: "..\ai-service\rag\*"; DestDir: "{app}\AiService\rag"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai-service
Source: "..\ai-service\ingest\*"; DestDir: "{app}\AiService\ingest"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai-service
Source: "..\ai-service\recommender\*"; DestDir: "{app}\AiService\recommender"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai-service
Source: "..\ai-service\voice\*"; DestDir: "{app}\AiService\voice"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai-service
Source: "..\ai-service\vision\*"; DestDir: "{app}\AiService\vision"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai-service

; ============================================================================
; OLLAMA PAYLOAD — TODO Phase 8
; ============================================================================

; Ollama binary (MIT license, bundle permitted)
Source: "..\Reference\ollama-windows-amd64.exe"; DestDir: "{app}\Ollama"; Flags: ignoreversion; Components: ollama

; lfm3-omni model (~2.4 GB on disk) — LFM Open License v1.0 (revenue threshold applies)
Source: "..\Reference\models\lfm3-omni.gguf"; DestDir: "{app}\Ollama\models"; Flags: ignoreversion; Components: model-lfm3-omni

; nomic-embed-text embedding model (~274 MB) — Apache-2.0
Source: "..\Reference\models\nomic-embed-text.gguf"; DestDir: "{app}\Ollama\models"; Flags: ignoreversion; Components: model-nomic-embed

; ============================================================================
; DEFAULT MANUAL LIBRARY — TODO Phase 8
; ============================================================================

Source: "..\docs\Manuals\*"; DestDir: "{userappdata}\GSE Tech Pro\Manuals"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: manuals-default

; ============================================================================
; DEMO DATA — Optional seed data for evaluation
; ============================================================================

Source: "..\installer\demo-data.db"; DestName: "gse-tech-pro.db"; DestDir: "{userappdata}\GSE Tech Pro\Data"; Flags: ignoreversion onlyifdoesntexist; Components: demo-data

[Icons]
; Start menu shortcut (required for unpackaged Mica/notifications per ADR-001)
Name: "{group}\{#AppName}"; Filename: "{app}\GseTechPro.App.exe"; IconFilename: "{app}\cta.ico"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\GseTechPro.App.exe"; IconFilename: "{app}\cta.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: uncheckedonce

[Registry]
; App User Model ID for toast notifications (unpackaged WinUI 3 requirement, ADR-001)
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#AppUserModelId}"; ValueType: string; ValueName: ""; ValueData: "{app}\GseTechPro.App.exe"

[Run]
; First-run setup wizard — launches the app with a flag to show shop/technician/device config
Filename: "{app}\GseTechPro.App.exe"; Parameters: "--first-run-wizard"; Description: "Launch {#AppName} first-run setup wizard"; Flags: nowait postinstall skipifsilent

[Code]
; ============================================================================
; SILENT INSTALL CONFIG FILE SUPPORT
; Usage: GSE-Tech-Pro-Setup.exe /SILENT /CONFIG="C:\path\to\config.ini"
; Config file format documented in installer/config-template.ini
; ============================================================================

var
  ConfigFile: string;
  ShopName: string;
  DefaultUnitSystem: string;
  TechniciansCSV: string;
  DevicesCSV: string;
  DataDirOverride: string;
  InstallOllamaModel: Boolean;

procedure InitializeWizard();
begin
  // Parse /CONFIG= parameter from command line
  ConfigFile := '';
  if ParamCount() > 0 then
  begin
    var i := 1;
    while i <= ParamCount() do
    begin
      if Pos('/CONFIG=', Uppercase(ParamStr(i))) = 1 then
      begin
        ConfigFile := Copy(ParamStr(i), 9, Length(ParamStr(i)) - 8);
      end;
      i := i + 1;
    end;
  end;

  // Load config values if file exists
  if ConfigFile <> '' and FileExists(ConfigFile) then
  begin
    ShopName := GetPrivateProfileString('install', 'shop_name', '', ConfigFile);
    DefaultUnitSystem := GetPrivateProfileString('install', 'default_unit_system', 'imperial', ConfigFile);
    TechniciansCSV := GetPrivateProfileString('technicians', 'list', '', ConfigFile);
    DevicesCSV := GetPrivateProfileString('devices', 'list', '', ConfigFile);
    DataDirOverride := GetPrivateProfileString('install', 'data_dir', '', ConfigFile);
    
    var installOllamaStr := GetPrivateProfileString('install', 'install_ollama_model', 'true', ConfigFile);
    InstallOllamaModel := (Uppercase(installOllamaStr) = 'TRUE');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Write first-run config to app data so the wizard can pick it up
    var configPath := ExpandConstant('{userappdata}\GSE Tech Pro\Data\first-run-config.json');
    
    // Build JSON config from parsed values (simplified — use proper JSON library in Phase 8)
    var json := '{';
    if ShopName <> '' then
      json := json + '"shop_name":"' + ShopName + '",' ;
    if DefaultUnitSystem <> '' then
      json := json + '"default_unit_system":"' + DefaultUnitSystem + '",' ;
    if TechniciansCSV <> '' then
      json := json + '"technicians":"[' + TechniciansCSV + ']","' ;
    if DevicesCSV <> '' then
      json := json + '"devices":"[' + DevicesCSV + ']","' ;
    json := json + '"install_ollama_model":' + (InstallOllamaModel ? 'true' : 'false') + '}';
    
    SaveStringToFile(configPath, json, False);
  end;
end;

// ============================================================================
// UPGRADE LOGIC — Never delete user data on upgrade
// User data lives in {userappdata}\GSE Tech Pro\ (DB, manuals, logs)
// Only the app files in {app} are replaced.
// ============================================================================

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Ask user whether to keep or remove user data on uninstall
    var result := MsgBox('Remove GSE Tech Pro user data (database, manuals, logs)?' + #13#10 + 
                         'Choose "Yes" to remove all data. Choose "No" to keep it.',
                         mbConfirmation, MB_YESNO);
    
    if result = IDYES then
    begin
      // Remove user data directory
      var userDataDir := ExpandConstant('{userappdata}\GSE Tech Pro');
      DelTree(userDataDir, True, True, True);
    end;
  end;
end;
