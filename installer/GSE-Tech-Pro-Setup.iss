; ============================================================================
; GSE Tech Pro — Full Installer Script (Inno Setup)
; Phase 8: Complete installer with signing, runtime bundling, model progress,
;          driver detection, and update checking.
; Phase 10A: llama.cpp (llama-server, GGUF) replaces Ollama (ADR-012). No Windows
;          service: the AI service starts and stops llama-server itself.
;
; Architecture reference: docs/ARCHITECTURE.md §1.1 (unpackaged + Inno, ADR-001)
; Spec reference:         docs/PROMPT.md deployment section
; ============================================================================

#define AppName "GSE Tech Pro"
#define AppVersion "0.4.0"
#define AppPublisher "Commercial Truck and Auto (CTA)"
#define AppURL "https://www.commercialtruckandauto.com"
#define AppSupportURL "https://support.commercialtruckandauto.com/gse-tech-pro"
#define UpdateCheckURL "https://updates.commercialtruckandauto.com/gse-tech-pro/latest.json"

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

; Phase 8: Code signing (user provides cert, never commit keys)
; To sign: set SIGNTOOL_CERT and SIGNTOOL_PASS environment variables before building
SignTool=signtool.exe sign /f "$SIGNTOOL_CERT" /p "$SIGNTOOL_PASS" /tr http://timestamp.digicert.com /td sha256 /fd sha256 "$f"

[Types]
Name: "full"; Description: "Full installation (app, AI service, llama.cpp, models, manuals)"; Flags: iscustom
Name: "core"; Description: "Core app only (no AI/model — for shops using remote AI)"

[Components]
; Core application components
Name: "app"; Description: "GSE Tech Pro application"; Types: full core; Flags: fixed
Name: "device-host"; Description: "32-bit device host (J2534/RP1210)"; Types: full core; Flags: fixed

; AI stack — optional for shops using remote/cloud AI
Name: "ai-service"; Description: "Python AI service (FastAPI, RAG, tools)"; Types: full
Name: "llama-cpp"; Description: "llama.cpp local model runtime (llama-server, CPU build)"; Types: full
Name: "model-lfm3-omni"; Description: "lfm3-omni chat/vision/audio model, GGUF (~2.4 GB)"; Types: full; DependsOn: llama-cpp
Name: "model-nomic-embed"; Description: "nomic-embed-text embedding model, GGUF (~274 MB)"; Types: full; DependsOn: llama-cpp

; Content
Name: "manuals-default"; Description: "Default manual library (TUG 660-28, 600i seed)"; Types: full core
Name: "demo-data"; Description: "Demo work orders and equipment data"; Flags: fixed

[Files]
; ============================================================================
; APP PAYLOAD — Self-contained x64 publish output
; ============================================================================

; Main WinUI app (x64, self-contained with Windows App SDK)
Source: "..\src\GseTechPro.App\bin\x64\Release\net8.0-windows10.0.19041.0\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app

; 32-bit device host (x86, self-contained) — loads J2534/RP1210 vendor DLLs
Source: "..\src\GseTechPro.Devices.Host32\bin\x86\Release\net8.0-windows10.0.19041.0\publish\*"; DestDir: "{app}\DeviceHost32"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: device-host

; ============================================================================
; AI SERVICE PAYLOAD — Embedded Python runtime + all packages
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
; LLAMA.CPP PAYLOAD (ADR-012) — pinned CPU (AVX2) build of llama-server + GGUF models
; No Windows service: the AI service starts llama-server on 127.0.0.1 and stops it on idle/exit.
; ============================================================================

; llama-server.exe and its DLLs from the pinned llama.cpp Windows CPU release (MIT).
; PINNED: llama.cpp b12359 (= v1.0.0, 2026-11-13), asset llama-b12359-bin-win-cpu-x64.zip (25.1 MB),
;   sha256 837c1655e87df3b66989f4be33a792a1399c785bf259038dc75c900096683e69 (GitHub release digest).
;   Reference\llama.cpp\ holds only llama-server.exe, the DLLs it imports, the ggml-cpu-*.dll
;   variants (picked at run time) and the licenses; see Reference\llama.cpp\PINNED.txt. LFM3 needs b11960+.
; ggml.dll needs the Microsoft VC++ 2015-2022 x64 runtime (MSVCP140.dll, VCRUNTIME140.dll).
; Microsoft's redistributable installer is bundled and run silently by InstallVcRuntime (see [Code]).
; Put vc_redist.x64.exe (https://aka.ms/vs/17/release/vc_redist.x64.exe) in Reference\vcredist\.
; PINNED: 14.44.35211.0, sha256 cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b,
;   Authenticode-signed by Microsoft Corporation (see Reference\vcredist\PINNED.txt).
Source: "..\Reference\vcredist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: llama-cpp
Source: "..\Reference\llama.cpp\*"; DestDir: "{app}\llama.cpp"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: llama-cpp
; MIT LICENSE goes into the third-party notices (ARCHITECTURE §7.1)
Source: "..\Reference\llama.cpp\LICENSE"; DestDir: "{app}\third-party"; DestName: "llama.cpp-LICENSE.txt"; Flags: ignoreversion; Components: llama-cpp
Source: "..\Reference\llama.cpp\LICENSE-LLVM-OpenMP"; DestDir: "{app}\third-party"; DestName: "llvm-openmp-LICENSE.txt"; Flags: ignoreversion; Components: llama-cpp

; lfm3-omni chat model, GGUF (~2.4 GB on disk) — LFM Open License v1.0 (revenue threshold; owner sign-off pending)
Source: "..\Reference\models\lfm3-omni.gguf"; DestDir: "{app}\models"; Flags: ignoreversion; Components: model-lfm3-omni

; nomic-embed-text embedding model, GGUF (~274 MB) — Apache-2.0
Source: "..\Reference\models\nomic-embed-text.gguf"; DestDir: "{app}\models"; Flags: ignoreversion; Components: model-nomic-embed

; AI service config template; CurStepChanged fills in the bundled paths and writes AiService\config\config.yaml
Source: "..\installer\ai-service-config.yaml"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: ai-service

; ============================================================================
; DEFAULT MANUAL LIBRARY
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
Name: "checkupdates"; Description: "Check for updates on startup"; GroupDescription: "Update settings:"; Flags: checkedonce

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
  InstallLocalModel: Boolean;
  AllowCloudAi: Boolean;
  ShopServerUrl: string;
  CheckUpdatesOnStartup: Boolean;

procedure InitializeWizard();
begin
  // Defaults when no /CONFIG file is given: install the local model; cloud AI off (ADR-013).
  InstallLocalModel := True;
  AllowCloudAi := False;
  ShopServerUrl := '';

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
    
    // install_ollama_model is the pre-0.4.0 name of install_local_model; still read for old config files.
    var installModelStr := GetPrivateProfileString('install', 'install_local_model',
      GetPrivateProfileString('install', 'install_ollama_model', 'true', ConfigFile), ConfigFile);
    InstallLocalModel := (Uppercase(installModelStr) = 'TRUE');

    // ADR-013: the cloud AI route is OFF unless this file turns it on (still needs the admin PIN in the app).
    AllowCloudAi := (Uppercase(GetPrivateProfileString('ai', 'allow_cloud', 'false', ConfigFile)) = 'TRUE');
    ShopServerUrl := GetPrivateProfileString('ai', 'shop_server_url', '', ConfigFile);
  end;
end;

// ============================================================================
// PHASE 10A: AI SERVICE CONFIG (ADR-012/013)
// Fills installer/ai-service-config.yaml with the bundled paths. Paths only: route keys
// live in Windows Credential Manager and are set by an administrator, never by setup.
// ============================================================================

function YamlPath(const Path: string): string;
begin
  // Single-quoted YAML scalar: backslashes are literal; a quote is doubled.
  Result := Path;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

procedure WriteAiServiceConfig();
var
  Template: AnsiString;
  Text: string;
  Binary, ChatModel, EmbedModel: string;
begin
  if not LoadStringFromFile(ExpandConstant('{tmp}\ai-service-config.yaml'), Template) then
    Exit;
  Text := String(Template);
  Binary := '';
  ChatModel := '';
  EmbedModel := '';
  if IsComponentSelected('llama-cpp') and InstallLocalModel then
  begin
    Binary := ExpandConstant('{app}\llama.cpp\llama-server.exe');
    if IsComponentSelected('model-lfm3-omni') then
      ChatModel := ExpandConstant('{app}\models\lfm3-omni.gguf');
    if IsComponentSelected('model-nomic-embed') then
      EmbedModel := ExpandConstant('{app}\models\nomic-embed-text.gguf');
  end;
  StringChangeEx(Text, '@LLAMA_SERVER@', YamlPath(Binary), True);
  StringChangeEx(Text, '@CHAT_MODEL@', YamlPath(ChatModel), True);
  StringChangeEx(Text, '@EMBED_MODEL@', YamlPath(EmbedModel), True);
  StringChangeEx(Text, '@SHOP_SERVER_URL@', YamlPath(ShopServerUrl), True);
  if ShopServerUrl <> '' then
    StringChangeEx(Text, '@SHOP_SERVER_ENABLED@', 'true', True)
  else
    StringChangeEx(Text, '@SHOP_SERVER_ENABLED@', 'false', True);
  ForceDirectories(ExpandConstant('{app}\AiService\config'));
  SaveStringToFile(ExpandConstant('{app}\AiService\config\config.yaml'), AnsiString(Text), False);
end;

// ============================================================================
// PHASE 10A: MICROSOFT VC++ 2015-2022 x64 RUNTIME (needed by llama.cpp's ggml.dll)
// vc_redist.x64.exe is Microsoft's redistributable installer. It is safe to run every
// time: it exits 0 when this or the same version is present and 1638 when a newer one is.
// ============================================================================

const
  VCREDIST_OK = 0;
  VCREDIST_NEWER_PRESENT = 1638;
  VCREDIST_REBOOT_INITIATED = 1641;
  VCREDIST_REBOOT_REQUIRED = 3010;

var
  VcRuntimeNeedsRestart: Boolean;

function VcRuntimePresent(): Boolean;
var
  Installed: Cardinal;
begin
  // Registry value written by every VC++ 2015-2022 x64 runtime install.
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
    'Installed', Installed) and (Installed = 1);
end;

procedure InstallVcRuntime();
var
  Redist: string;
  ExitCode: Integer;
begin
  Redist := ExpandConstant('{tmp}\vc_redist.x64.exe');
  if not FileExists(Redist) then
    Exit;
  WizardForm.StatusLabel.Caption := 'Installing the Microsoft Visual C++ runtime (needed by the local AI model)...';
  if not Exec(Redist, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Log('vc_redist.x64.exe could not be started: ' + SysErrorMessage(ExitCode));
    ExitCode := -1;
  end;
  Log('vc_redist.x64.exe exit code: ' + IntToStr(ExitCode));
  case ExitCode of
    VCREDIST_OK, VCREDIST_NEWER_PRESENT: ;
    VCREDIST_REBOOT_REQUIRED, VCREDIST_REBOOT_INITIATED: VcRuntimeNeedsRestart := True;
  else
    if not VcRuntimePresent() then
      SuppressibleMsgBox('The Microsoft Visual C++ runtime could not be installed (code ' + IntToStr(ExitCode) + ').' + #13#10 +
        'The local AI model will not start until it is installed. Manual search and diagnostics still work.' + #13#10 +
        'Run vc_redist.x64.exe from Microsoft, or reinstall GSE Tech Pro.',
        mbError, MB_OK, IDOK);
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := VcRuntimeNeedsRestart;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Write first-run config to app data so the wizard can pick it up
    var configPath := ExpandConstant('{userappdata}\GSE Tech Pro\Data\first-run-config.json');
    
    // Determine if update checking is enabled (from task selection)
    CheckUpdatesOnStartup := IsTaskSelected('checkupdates');
    
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
    if InstallLocalModel then
      json := json + '"install_local_model":true,'
    else
      json := json + '"install_local_model":false,';
    if AllowCloudAi then
      json := json + '"allow_cloud_ai":true,'
    else
      json := json + '"allow_cloud_ai":false,';
    
    if CheckUpdatesOnStartup then
      json := json + '"check_updates_on_startup":true}'
    else
      json := json + '"check_updates_on_startup":false}';
    
    SaveStringToFile(configPath, json, False);

    // Phase 10A (ADR-012): upgrades from 0.3.x remove the old GSE-Ollama service. Nothing replaces it:
    // the AI service starts llama-server itself, on 127.0.0.1 only.
    Exec('sc', 'stop GSE-Ollama', '', SW_HIDE, ewWaitUntilTerminated, RESULT);
    Exec('sc', 'delete GSE-Ollama', '', SW_HIDE, ewWaitUntilTerminated, RESULT);

    // llama-server.exe needs the VC++ 2015-2022 x64 runtime; install it whenever llama.cpp is installed.
    if IsComponentSelected('llama-cpp') then
      InstallVcRuntime();

    // Write the AI service config with the bundled llama-server and GGUF paths (never a key: ADR-013).
    if IsComponentSelected('ai-service') then
      WriteAiServiceConfig();

    // Phase 8: Driver presence detection and guidance
    DetectAndReportMissingDrivers();
  end;
end;

// ============================================================================
// PHASE 8: DRIVER PRESENCE DETECTION AND GUIDANCE
// Checks for common vendor drivers and reports missing ones
// ============================================================================

procedure DetectAndReportMissingDrivers();
var
  MissingDrivers: string;
begin
  MissingDrivers := '';

  // Check for TOPDON RLink X3 driver (J2534)
  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\RLinkX3') then
    MissingDrivers := MissingDrivers + '- TOPDON RLink X3 (J2534)' + #13#10;

  // Check for Nexiq USB-Link driver (RP1210)
  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NexiqUSBLink') then
    MissingDrivers := MissingDrivers + '- Nexiq USB-Link (RP1210)' + #13#10;

  // Check for Noregon DLA+ driver (J2534)
  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NoregonDLAPlus') then
    MissingDrivers := MissingDrivers + '- Noregon DLA+ (J2534)' + #13#10;

  // Check for PicoScope driver
  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PicoScope') then
    MissingDrivers := MissingDrivers + '- PicoScope' + #13#10;

  // Check for Hantek 6074BE driver
  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\Hantek6074BE') then
    MissingDrivers := MissingDrivers + '- Hantek 6074BE' + #13#10;

  // If any drivers are missing, show guidance
  if MissingDrivers <> '' then
  begin
    MsgBox('The following diagnostic device drivers were not detected:' + #13#10 + 
           MissingDrivers + #13#10 +
           'To use these devices, install their vendor drivers separately from the manufacturer websites.' + #13#10 +
           'This does not affect core app functionality.', mbInformation, MB_OK);
  end;
end;

// ============================================================================
// PHASE 8: MODEL DOWNLOAD PROGRESS INDICATION
// Shows progress during large model file installation
// ============================================================================

procedure CurFileChanged(SourceFilename: string; DestFilename: string);
begin
  // Detect when installing large model files and show progress
  if Pos('lfm3-omni', SourceFilename) > 0 then
    WizardForm.StatusLabel.Caption := 'Installing lfm3-omni model (~2.4 GB)...';
  
  if Pos('nomic-embed-text', SourceFilename) > 0 then
    WizardForm.StatusLabel.Caption := 'Installing nomic-embed-text embedding model (~274 MB)...';
end;

// ============================================================================
// PHASE 8: AUTOMATIC UPDATE CHECKING (OPTIONAL ONLINE CHANNEL)
// Checks for updates on startup if user selected the task
// ============================================================================

procedure CheckForUpdates();
var
  HttpReq: Variant;
  ResponseText: string;
  LatestVersion: string;
begin
  try
    // Create HTTP request object
    HttpReq := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    HttpReq.Open('GET', '{#UpdateCheckURL}', False);
    HttpReq.Send();

    if HttpReq.Status = 200 then
    begin
      ResponseText := HttpReq.ResponseText;
      
      // Parse JSON response (simplified — use proper JSON library in production)
      // Expected format: {"version":"0.4.0","url":"https://..."}
      LatestVersion := ExtractVersionFromJson(ResponseText);
      
      if LatestVersion <> '' and CompareVersions(LatestVersion, '{#AppVersion}') > 0 then
      begin
        var result := MsgBox('A new version of GSE Tech Pro is available: ' + LatestVersion + #13#10 + 
                             'Would you like to download the update now?', mbConfirmation, MB_YESNO);
        
        if result = IDYES then
        begin
          // Download and run update installer
          var UpdateURL := ExtractUrlFromJson(ResponseText);
          ShellExec('open', UpdateURL, '', '', SW_SHOWNORMAL);
        end;
      end;
    end;
  except
    // Silently fail if no internet or update server unreachable (offline-first)
  end;
end;

function ExtractVersionFromJson(Json: string): string;
begin
  // Simplified JSON parsing for version field
  var VersionStart := Pos('"version":"', Json);
  if VersionStart > 0 then
  begin
    VersionStart := VersionStart + 10;
    var VersionEnd := Pos('"', Json, VersionStart);
    if VersionEnd > 0 then
      Result := Copy(Json, VersionStart, VersionEnd - VersionStart);
  end;
end;

function ExtractUrlFromJson(Json: string): string;
begin
  // Simplified JSON parsing for url field
  var UrlStart := Pos('"url":"', Json);
  if UrlStart > 0 then
  begin
    UrlStart := UrlStart + 6;
    var UrlEnd := Pos('"', Json, UrlStart);
    if UrlEnd > 0 then
      Result := Copy(Json, UrlStart, UrlEnd - UrlStart);
  end;
end;

function CompareVersions(V1: string; V2: string): Integer;
begin
  // Simplified version comparison (major.minor.patch)
  var Parts1 := SplitString(V1, '.');
  var Parts2 := SplitString(V2, '.');
  
  var i := 0;
  while i < 3 do
  begin
    var N1 := 0;
    var N2 := 0;
    
    if i < Length(Parts1) then
      N1 := StrToIntDef(Parts1[i], 0);
    if i < Length(Parts2) then
      N2 := StrToIntDef(Parts2[i], 0);
    
    if N1 > N2 then
    begin
      Result := 1;
      Exit;
    end;
    if N1 < N2 then
    begin
      Result := -1;
      Exit;
    end;
    
    i := i + 1;
  end;
  
  Result := 0;
end;

function SplitString(S: string; Delimiter: string): array of string;
var
  Parts: array of string;
  CurrentPart: string;
  I: Integer;
begin
  SetLength(Parts, 0);
  CurrentPart := '';
  
  for I := 1 to Length(S) do
  begin
    if Copy(S, I, Length(Delimiter)) = Delimiter then
    begin
      AddToParts(Parts, CurrentPart);
      CurrentPart := '';
      I := I + Length(Delimiter) - 1;
    end
    else
    begin
      CurrentPart := CurrentPart + Copy(S, I, 1);
    end;
  end;
  
  if CurrentPart <> '' then
    AddToParts(Parts, CurrentPart);
  
  Result := Parts;
end;

procedure AddToParts(var Parts: array of string; Value: string);
var
  NewLength: Integer;
begin
  NewLength := Length(Parts) + 1;
  SetLength(Parts, NewLength);
  Parts[NewLength - 1] := Value;
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

    // Legacy (<= 0.3.x): remove the GSE-Ollama service if an old install left it behind.
    Exec('sc', 'delete GSE-Ollama', '', SW_HIDE, ewWaitUntilTerminated, RESULT);
  end;
end;