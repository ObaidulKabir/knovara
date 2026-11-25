; Inno Setup script for Knovara: the desktop app, the local runtime (API in Runtime:Mode=Local, with its
; two bundled ONNX models), the `knovara` CLI and the `knovara-mcp` server for Claude Code/Desktop.
; Build with installer\build-installer.ps1, which publishes everything and passes the version and
; (optionally) the control plane address:
;   ISCC /DAppVersion=1.0.0 /DControlPlaneUrl=https://control.example.com knovara.iss
;
; Installs per user (no administrator rights, nothing written under Program Files), so the app can save its
; own settings next to itself and the installer works on locked-down PCs. The installer is not code-signed;
; see README "Desktop installer".

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef ControlPlaneUrl
  #define ControlPlaneUrl ""
#endif

[Setup]
AppId={{7B4D1E52-6E0A-4C0B-9C1D-5A3F0E2B8A41}
AppName=Knovara
AppVersion={#AppVersion}
AppVerName=Knovara {#AppVersion}
AppPublisher=Knovara
DefaultDirName={autopf}\Knovara
DefaultGroupName=Knovara
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=Knovara-Setup-{#AppVersion}
; "x64" is accepted by every Inno Setup 6.x ("x64compatible" needs 6.3+).
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=assets\knovara.ico
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
UninstallDisplayName=Knovara
UninstallDisplayIcon={app}\Knovara.Desktop.exe
VersionInfoVersion={#AppVersion}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "addpath"; Description: "Add the &knovara command to PATH (for any terminal)"; Flags: checkedonce
Name: "registermcp"; Description: "Register knovara-mcp with &Claude Code (if installed)"; Flags: checkedonce

[Files]
; knovara.config.json is the developer's local settings (a sample tenant and localhost addresses); it is
; replaced below by a generated file so customers never inherit it. The layout under {#SourceDir} is:
;   (root)          Knovara.Desktop.exe and its dependencies
;   runtime\         Knovara.Api.exe (Runtime:Mode=Local) plus models\bge-small, models\minilm-rerank
;   cli\             knovara.exe
;   mcp\             knovara-mcp.exe and register-mcp.ps1
; The one wildcard below copies all of it, preserving that structure under {app}.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "knovara.config.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Knovara Desktop"; Filename: "{app}\Knovara.Desktop.exe"
Name: "{autodesktop}\Knovara Desktop"; Filename: "{app}\Knovara.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\mcp\register-mcp.ps1"" -McpExePath ""{app}\mcp\knovara-mcp.exe"""; \
    Tasks: registermcp; StatusMsg: "Registering knovara-mcp with Claude Code..."; Flags: runhidden
Filename: "{app}\Knovara.Desktop.exe"; Description: "Launch Knovara"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings written by the app next to itself. The enrollment (%LOCALAPPDATA%\Knovara\device.json), the local
; runtime's database and models (%LOCALAPPDATA%\Knovara\knovara.db, \models) are deliberately kept, so
; reinstalling or upgrading never loses a document you added or makes you enroll again.
Type: files; Name: "{app}\knovara.config.json"

[Code]
const
  WM_KNOVARA_SETTINGCHANGE = $1A;
  SMTO_KNOVARA_ABORTIFHUNG = $0002;

function SendMessageTimeout(hWnd: Integer; Msg: Integer; wParam: Integer; lParam: string;
  fuFlags: Integer; uTimeout: Integer; var lpdwResult: Integer): Integer;
  external 'SendMessageTimeoutW@user32.dll stdcall';

// Lets Explorer and new terminals pick up a PATH change without the user having to log off and back on.
procedure BroadcastEnvironmentChange;
var
  ignored: Integer;
begin
  SendMessageTimeout(HWND_BROADCAST, WM_KNOVARA_SETTINGCHANGE, 0, 'Environment', SMTO_KNOVARA_ABORTIFHUNG, 5000, ignored);
end;

function NeedsAddPath(Dir: string): boolean;
var
  Existing: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Existing) then
  begin
    Result := True;
    exit;
  end;
  // Padded with leading/trailing separators so a match can't be fooled by Dir being a substring of
  // some other entry (e.g. "C:\Knovara2\cli" must not look like it already contains "C:\Knovara\cli").
  Result := Pos(';' + Dir + ';', ';' + Existing + ';') = 0;
end;

procedure RemoveFromPath(Dir: string);
var
  Existing, Updated: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Existing) then exit;
  Updated := ';' + Existing + ';';
  P := Pos(';' + Dir + ';', Updated);
  if P = 0 then exit;
  Delete(Updated, P, Length(Dir) + 1);
  Updated := Copy(Updated, 2, Length(Updated) - 2); // drop the padding added above
  RegWriteStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Updated);
end;

// Pre-fills the "Control plane address" box in the app, and adds {app}\cli to PATH when that task is
// checked. Only for a fresh install/when still needed, so an upgrade never overwrites what the user changed.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Url, Json, ConfigPath, CliDir, Existing: String;
begin
  if CurStep <> ssPostInstall then exit;

  Url := '{#ControlPlaneUrl}';
  ConfigPath := ExpandConstant('{app}\knovara.config.json');
  if (Url <> '') and not FileExists(ConfigPath) then
  begin
    Json := '{' + #13#10 + '  "ControlPlaneUrl": "' + Url + '"' + #13#10 + '}' + #13#10;
    SaveStringToFile(ConfigPath, Json, False);
  end;

  if WizardIsTaskSelected('addpath') then
  begin
    CliDir := ExpandConstant('{app}\cli');
    if NeedsAddPath(CliDir) then
    begin
      if RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Existing) and (Existing <> '') then
        RegWriteStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Existing + ';' + CliDir)
      else
        RegWriteStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', CliDir);
      BroadcastEnvironmentChange;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}\cli'));
    BroadcastEnvironmentChange;
  end;
end;
