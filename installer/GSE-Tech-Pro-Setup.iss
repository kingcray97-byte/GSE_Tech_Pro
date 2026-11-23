; ============================================================================
; GSE Tech Pro — Full Installer Script (Inno Setup 7)
; Phase 8: installer with signing, runtime bundling, silent multi-tablet config,
;          driver detection and upgrades.
; Phase 10A: llama.cpp (llama-server, GGUF) replaces Ollama (ADR-012). No Windows
;          service: the AI service starts and stops llama-server itself.
;
; Build with installer\build-installer.ps1 (publishes the app and the device host,
; checks the embedded Python, then runs ISCC). Test build without GGUF models:
;   build-installer.ps1 -NoModels   (passes /DNO_MODELS)
;
; Architecture reference: docs/ARCHITECTURE.md §1.1 (unpackaged + Inno, ADR-001)
; Spec reference:         docs/PROMPT.md deployment section
; ============================================================================

#define AppName "GSE Tech Pro"
#define AppVersion "0.4.0"
#define AppPublisher "Commercial Truck and Auto (CTA)"
#define AppURL "https://www.commercialtruckandauto.com"
#define AppSupportURL "https://support.commercialtruckandauto.com/gse-tech-pro"
#define AppExe "GseTechPro.App.exe"

; AUMID required for toast notifications on unpackaged WinUI 3 apps (ADR-001)
#define AppUserModelId "CommercialTruckAndAuto.GSETechPro"

[Setup]
AppId={{A7F2E9C4-8B3D-4E1A-B6F5-C0D2E8A9B1C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppSupportURL}
; Per-machine install in 64-bit Program Files (the app is x64; the device host is a separate x86 process).
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
OutputBaseFilename=GSE-Tech-Pro-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
; The solid archive is large (embedded Python, models): let lzma2 use more memory, not more time.
LZMANumBlockThreads=4
WizardStyle=modern
SetupIconFile=..\brand\cta.ico
UninstallDisplayIcon={app}\cta.ico
CloseApplications=yes

; Phase 8: Code signing (user provides cert, never commit keys)
; To sign: set SIGNTOOL_CERT and SIGNTOOL_PASS, then run installer\build-installer.ps1. It defines
; the "gsesign" sign tool on the ISCC command line (/S) and passes /DSIGN. Without them: unsigned build.
#ifdef SIGN
SignTool=gsesign
SignedUninstaller=yes
#endif

[Types]
Name: "full"; Description: "Full installation (app, AI service, llama.cpp, models, manuals)"
Name: "core"; Description: "Core app only (no AI/model — for shops using remote AI)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
; Core application components
Name: "app"; Description: "GSE Tech Pro application"; Types: full core custom; Flags: fixed
Name: "device_host"; Description: "32-bit device host (J2534/RP1210)"; Types: full core custom; Flags: fixed

; AI stack — optional for shops using remote/cloud AI
Name: "ai_service"; Description: "Python AI service (FastAPI, RAG, tools)"; Types: full
Name: "llama_cpp"; Description: "llama.cpp local model runtime (llama-server, CPU build)"; Types: full
; Child components: picking a model also picks llama_cpp (Inno has no DependsOn).
#ifndef NO_MODELS
Name: "llama_cpp\model_lfm3_omni"; Description: "lfm3-omni chat/vision/audio model, GGUF (LFM3-Omni-2B Q4_K_M, ~1.1 GB)"; Types: full
Name: "llama_cpp\model_nomic_embed"; Description: "nomic-embed-text v1.5 embedding model, GGUF"; Types: full
#endif

; Content
Name: "manuals_default"; Description: "Default manual library (TUG 660-28, 600i seed)"; Types: full core

[Files]
; ============================================================================
; APP PAYLOAD — Self-contained x64 publish output
; ============================================================================

; Main WinUI app (x64, self-contained with Windows App SDK). build-installer.ps1 publishes it to installer\build\app.
Source: "build\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app
; Shortcut icon (our own brand mark, brand\cta.ico)
Source: "..\brand\cta.ico"; DestDir: "{app}"; Flags: ignoreversion; Components: app

; 32-bit device host (x86, self-contained) — loads J2534/RP1210 vendor DLLs. Published to installer\build\host32.
; Must be {app}\host32: HostProcess looks next to the app or in host32\, nowhere else.
Source: "build\host32\*"; DestDir: "{app}\host32"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: device_host

; ============================================================================
; AI SERVICE PAYLOAD — Embedded Python runtime + all packages
; ============================================================================

; Embedded Python runtime (3.12) + all packages (no pip at install time).
; Built by installer\build-python-embedded.ps1 (pinned zip + every ai-service/pyproject.toml dependency,
; OCR packages included). Its python312._pth puts ..\AiService on the import path, so the layout below
; must stay {app}\Python next to {app}\AiService.
Source: "..\ai-service\dist\python-embedded\*"; DestDir: "{app}\Python"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service

; AI service code. {app}\AiService mirrors ai-service\ exactly: each folder is an import package
; (python -m app.main), and app\prompts.py / rag\config.py find prompts\ and config\ beside them.
; app\config.yaml is the dev default (dev token) and is not shipped: setup writes config\config.yaml.
Source: "..\ai-service\app\*"; DestDir: "{app}\AiService\app"; Excludes: "__pycache__,*.pyc,config.yaml"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service
Source: "..\ai-service\tools\*"; DestDir: "{app}\AiService\tools"; Excludes: "__pycache__,*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service
Source: "..\ai-service\rag\*"; DestDir: "{app}\AiService\rag"; Excludes: "__pycache__,*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service
Source: "..\ai-service\ingest\*"; DestDir: "{app}\AiService\ingest"; Excludes: "__pycache__,*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service
Source: "..\ai-service\recommender\*"; DestDir: "{app}\AiService\recommender"; Excludes: "__pycache__,*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service
Source: "..\ai-service\voice\*"; DestDir: "{app}\AiService\voice"; Excludes: "__pycache__,*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service
Source: "..\ai-service\vision\*"; DestDir: "{app}\AiService\vision"; Excludes: "__pycache__,*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service
; Prompt files (system, vision, ASR). Without them chat, Inspect photo and Talk cannot start.
Source: "..\ai-service\prompts\*"; DestDir: "{app}\AiService\prompts"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: ai_service
; Schema safety net (app\bootstrap.py) for a database the app has not migrated yet. The app itself
; embeds these files; the AI service reads them from AiService\migrations.
Source: "..\src\GseTechPro.Data\Migrations\*.sql"; DestDir: "{app}\AiService\migrations"; Flags: ignoreversion; Components: ai_service
; AI service config template; CurStepChanged fills in the bundled paths and writes AiService\config\config.yaml
Source: "ai-service-config.yaml"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: ai_service

; ============================================================================
; LLAMA.CPP PAYLOAD (ADR-012) — pinned CPU (AVX2) build of llama-server + GGUF models
; No Windows service: the AI service starts llama-server on 127.0.0.1 and stops it on idle/exit.
; Skipped when a /CONFIG file says install_local_model = false (WantLocalModel).
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
Source: "..\Reference\vcredist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: llama_cpp; Check: WantLocalModel
Source: "..\Reference\llama.cpp\*"; DestDir: "{app}\llama.cpp"; Excludes: "PINNED.txt"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: llama_cpp; Check: WantLocalModel
; MIT LICENSE goes into the third-party notices (ARCHITECTURE §7.1)
Source: "..\Reference\llama.cpp\LICENSE"; DestDir: "{app}\third-party"; DestName: "llama.cpp-LICENSE.txt"; Flags: ignoreversion; Components: llama_cpp; Check: WantLocalModel
Source: "..\Reference\llama.cpp\LICENSE-LLVM-OpenMP"; DestDir: "{app}\third-party"; DestName: "llvm-openmp-LICENSE.txt"; Flags: ignoreversion; Components: llama_cpp; Check: WantLocalModel

; GGUF models from Reference\models\ (ADR-012 pins). A release build needs both files; /DNO_MODELS
; builds a test setup without them (the AI service then reports "chat model missing", search still works).
#ifndef NO_MODELS
  #if !FileExists(SourcePath + "..\Reference\models\lfm3-omni.gguf") || !FileExists(SourcePath + "..\Reference\models\nomic-embed-text.gguf")
    #error Put lfm3-omni.gguf and nomic-embed-text.gguf in Reference\models\ (ADR-012), or build with /DNO_MODELS.
  #endif
; lfm3-omni chat model, LFM3-Omni-2B Q4_K_M — LFM Open License v1.0 (revenue threshold; owner sign-off pending)
Source: "..\Reference\models\lfm3-omni.gguf"; DestDir: "{app}\models"; Flags: ignoreversion; Components: llama_cpp\model_lfm3_omni; Check: WantLocalModel; BeforeInstall: ShowStatus('Installing the lfm3-omni chat model...')
; nomic-embed-text v1.5 embedding model, GGUF — Apache-2.0
Source: "..\Reference\models\nomic-embed-text.gguf"; DestDir: "{app}\models"; Flags: ignoreversion; Components: llama_cpp\model_nomic_embed; Check: WantLocalModel; BeforeInstall: ShowStatus('Installing the nomic-embed-text embedding model...')
#endif

; ============================================================================
; DEFAULT MANUAL LIBRARY — machine-wide, so it is there for whichever technician signs in.
; ============================================================================

Source: "..\docs\Manuals\*"; DestDir: "{commonappdata}\GSE Tech Pro\Manuals"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: manuals_default

[Dirs]
Name: "{commonappdata}\GSE Tech Pro"

[Icons]
; Start menu shortcut with the AUMID (required for unpackaged toast notifications, ADR-001)
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\cta.ico"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\cta.ico"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "checkupdates"; Description: "Check for updates on startup"; GroupDescription: "Update settings:"; Flags: checkedonce

[Registry]
; App User Model ID for toast notifications (unpackaged WinUI 3, ADR-001), machine-wide for every user.
Root: HKLM; Subkey: "Software\Classes\AppUserModelId\{#AppUserModelId}"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\AppUserModelId\{#AppUserModelId}"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\cta.ico"

[Run]
; Opens the app for the person who ran setup (not elevated). It reads {commonappdata}\GSE Tech Pro\first-run-config.json.
Filename: "{app}\{#AppExe}"; Parameters: "--first-run-wizard"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// ============================================================================
// SILENT INSTALL CONFIG FILE SUPPORT
// Usage: GSE-Tech-Pro-Setup.exe /SILENT /CONFIG="C:\path\to\config.ini"
// Config file format documented in installer/config-template.ini
// ============================================================================

const
  VCREDIST_OK = 0;
  VCREDIST_NEWER_PRESENT = 1638;
  VCREDIST_REBOOT_INITIATED = 1641;
  VCREDIST_REBOOT_REQUIRED = 3010;

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
  VcRuntimeNeedsRestart: Boolean;

function IsTrue(const Value: string): Boolean;
var
  V: string;
begin
  V := Lowercase(Trim(Value));
  Result := (V = 'true') or (V = 'yes') or (V = '1');
end;

function InitializeSetup(): Boolean;
var
  InstallModelStr: string;
begin
  Result := True;
  // Defaults when no /CONFIG file is given: install the local model; cloud AI off (ADR-013).
  InstallLocalModel := True;
  AllowCloudAi := False;
  ShopServerUrl := '';
  DefaultUnitSystem := 'imperial';

  ConfigFile := Trim(ExpandConstant('{param:CONFIG|}'));
  if ConfigFile = '' then
    Exit;
  if not FileExists(ConfigFile) then
  begin
    // A silent multi-tablet install must not go ahead with the wrong settings.
    SuppressibleMsgBox('The setup config file was not found:' + #13#10 + ConfigFile, mbCriticalError, MB_OK, IDOK);
    Log('Config file not found: ' + ConfigFile);
    Result := False;
    Exit;
  end;

  ShopName := Trim(GetIniString('install', 'shop_name', '', ConfigFile));
  DefaultUnitSystem := Lowercase(Trim(GetIniString('install', 'default_unit_system', 'imperial', ConfigFile)));
  DataDirOverride := Trim(GetIniString('install', 'data_dir', '', ConfigFile));
  TechniciansCSV := GetIniString('technicians', 'list', '', ConfigFile);
  DevicesCSV := GetIniString('devices', 'list', '', ConfigFile);

  // install_ollama_model is the pre-0.4.0 name of install_local_model; still read for old config files.
  InstallModelStr := GetIniString('install', 'install_ollama_model', 'true', ConfigFile);
  InstallModelStr := GetIniString('install', 'install_local_model', InstallModelStr, ConfigFile);
  InstallLocalModel := IsTrue(InstallModelStr);

  // ADR-013: the cloud AI route is OFF unless this file turns it on (still needs the admin PIN in the app).
  AllowCloudAi := IsTrue(GetIniString('ai', 'allow_cloud', 'false', ConfigFile));
  ShopServerUrl := Trim(GetIniString('ai', 'shop_server_url', '', ConfigFile));
  Log('Config file read: ' + ConfigFile);
end;

// [Files] Check: the llama.cpp runtime and models are skipped when the config says install_local_model = false.
function WantLocalModel(): Boolean;
begin
  Result := InstallLocalModel;
end;

// [Files] BeforeInstall: status line while a large model file is copied.
procedure ShowStatus(const Text: string);
begin
  WizardForm.StatusLabel.Caption := Text;
end;

// ============================================================================
// JSON / YAML helpers (strings from the config file can hold quotes or non-ASCII names)
// ============================================================================

function JsonString(const S: string): string;
var
  I: Integer;
  C: Char;
begin
  Result := '"';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if C = '"' then
      Result := Result + '\"'
    else if C = '\' then
      Result := Result + '\\'
    else if Ord(C) < 32 then
      Result := Result + Format('\u%.4x', [Ord(C)])
    else
      Result := Result + C;
  end;
  Result := Result + '"';
end;

// "a, b,,c" -> ["a","b","c"]
function JsonList(const Csv: string): string;
var
  Rest, Item: string;
  P: Integer;
  First: Boolean;
begin
  Result := '[';
  First := True;
  Rest := Csv;
  while Rest <> '' do
  begin
    P := Pos(',', Rest);
    if P = 0 then
    begin
      Item := Rest;
      Rest := '';
    end
    else
    begin
      Item := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;
    Item := Trim(Item);
    if Item <> '' then
    begin
      if not First then
        Result := Result + ',';
      Result := Result + JsonString(Item);
      First := False;
    end;
  end;
  Result := Result + ']';
end;

function JsonBool(const B: Boolean): string;
begin
  if B then
    Result := 'true'
  else
    Result := 'false';
end;

function SaveUtf8(const FileName, Text: string): Boolean;
var
  Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 1);
  Lines[0] := Text;
  Result := SaveStringsToUTF8FileWithoutBOM(FileName, Lines, False);
  if not Result then
    Log('Could not write ' + FileName);
end;

function YamlPath(const Path: string): string;
begin
  // Single-quoted YAML scalar: backslashes are literal; a quote is doubled.
  Result := Path;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

// ============================================================================
// FIRST-RUN CONFIG for the app: {commonappdata}\GSE Tech Pro\first-run-config.json
// ============================================================================

procedure WriteFirstRunConfig();
var
  Json: string;
begin
  Json := '{' +
    '"shop_name":' + JsonString(ShopName) + ',' +
    '"default_unit_system":' + JsonString(DefaultUnitSystem) + ',' +
    '"data_dir":' + JsonString(DataDirOverride) + ',' +
    '"technicians":' + JsonList(TechniciansCSV) + ',' +
    '"devices":' + JsonList(DevicesCSV) + ',' +
    '"install_local_model":' + JsonBool(InstallLocalModel) + ',' +
    '"allow_cloud_ai":' + JsonBool(AllowCloudAi) + ',' +
    '"shop_server_url":' + JsonString(ShopServerUrl) + ',' +
    '"check_updates_on_startup":' + JsonBool(WizardIsTaskSelected('checkupdates')) +
    '}';
  SaveUtf8(ExpandConstant('{commonappdata}\GSE Tech Pro\first-run-config.json'), Json);
end;

// ============================================================================
// PHASE 10A: AI SERVICE CONFIG (ADR-012/013)
// Fills installer/ai-service-config.yaml with the bundled paths. Paths only: route keys
// live in Windows Credential Manager and are set by an administrator, never by setup.
// ============================================================================

procedure WriteAiServiceConfig();
var
  Template: AnsiString;
  Text: string;
  Binary, ChatModel, EmbedModel: string;
begin
  if not LoadStringFromFile(ExpandConstant('{tmp}\ai-service-config.yaml'), Template) then
  begin
    Log('ai-service-config.yaml template missing');
    Exit;
  end;
  Text := String(Template);
  Binary := '';
  ChatModel := '';
  EmbedModel := '';
  if WizardIsComponentSelected('llama_cpp') and InstallLocalModel then
  begin
    Binary := ExpandConstant('{app}\llama.cpp\llama-server.exe');
#ifndef NO_MODELS
    if WizardIsComponentSelected('llama_cpp\model_lfm3_omni') then
      ChatModel := ExpandConstant('{app}\models\lfm3-omni.gguf');
    if WizardIsComponentSelected('llama_cpp\model_nomic_embed') then
      EmbedModel := ExpandConstant('{app}\models\nomic-embed-text.gguf');
#endif
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
  SaveUtf8(ExpandConstant('{app}\AiService\config\config.yaml'), Text);
end;

// ============================================================================
// PHASE 10A: MICROSOFT VC++ 2015-2022 x64 RUNTIME (needed by llama.cpp's ggml.dll)
// vc_redist.x64.exe is Microsoft's redistributable installer. It is safe to run every
// time: it exits 0 when this or the same version is present and 1638 when a newer one is.
// ============================================================================

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

// ============================================================================
// PHASE 8: DRIVER PRESENCE CHECK (published conventions only, nothing vendor-specific)
// J2534: SAE J2534-1 devices register under HKLM\SOFTWARE\PassThruSupport.04.04 (32-bit view).
// RP1210: TMC RP1210 adapters are listed in %WINDIR%\RP121032.ini.
// ============================================================================

function J2534DeviceCount(): Integer;
var
  Names: TArrayOfString;
begin
  Result := 0;
  if RegGetSubkeyNames(HKLM32, 'SOFTWARE\PassThruSupport.04.04', Names) then
    Result := GetArrayLength(Names);
  if RegGetSubkeyNames(HKLM32, 'SOFTWARE\PassThruSupport.05.00', Names) then
    Result := Result + GetArrayLength(Names);
end;

procedure ReportMissingDrivers();
var
  Missing: string;
  J2534Count: Integer;
begin
  Missing := '';
  J2534Count := J2534DeviceCount();
  Log('J2534 devices registered: ' + IntToStr(J2534Count));
  if J2534Count = 0 then
    Missing := Missing + '- No J2534 pass-thru device driver (for example TOPDON RLink X3, Noregon DLA+)' + #13#10;
  if not FileExists(ExpandConstant('{win}\RP121032.ini')) then
    Missing := Missing + '- No RP1210 adapter driver (for example Nexiq USB-Link)' + #13#10;
  if (Missing <> '') and not WizardSilent() then
    MsgBox('These diagnostic adapter drivers were not found:' + #13#10 + Missing + #13#10 +
           'Install them from the adapter maker before using those adapters. Oscilloscopes (PicoScope, Hantek) ' +
           'also need their maker''s driver. The simulator and the rest of the app work without them.',
           mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    WriteFirstRunConfig();

    // Phase 10A (ADR-012): upgrades from 0.3.x remove the old GSE-Ollama service. Nothing replaces it:
    // the AI service starts llama-server itself, on 127.0.0.1 only. Fails harmlessly when it is not there.
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop GSE-Ollama', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete GSE-Ollama', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // llama-server.exe needs the VC++ 2015-2022 x64 runtime; install it whenever llama.cpp is installed.
    if WizardIsComponentSelected('llama_cpp') and InstallLocalModel then
      InstallVcRuntime();

    // Write the AI service config with the bundled llama-server and GGUF paths (never a key: ADR-013).
    if WizardIsComponentSelected('ai_service') then
      WriteAiServiceConfig();

    ReportMissingDrivers();
  end;
end;

// ============================================================================
// UNINSTALL — Never delete the technician's database (%LOCALAPPDATA%\GseTechPro).
// Only the files setup itself put in {commonappdata}\GSE Tech Pro (default manuals,
// first-run config) are offered for removal, and only when someone answers Yes.
// ============================================================================

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  SetupData: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    SetupData := ExpandConstant('{commonappdata}\GSE Tech Pro');
    if DirExists(SetupData) and not UninstallSilent() then
      if MsgBox('Remove the default manuals and setup files in' + #13#10 + SetupData + '?' + #13#10 + #13#10 +
                'Work orders, readings and imported manuals in each technician''s database are kept either way.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(SetupData, True, True, True);

    // Legacy (<= 0.3.x): remove the GSE-Ollama service if an old install left it behind.
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete GSE-Ollama', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
