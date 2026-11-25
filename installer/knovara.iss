; Inno Setup script for Knovara Desktop. Build with installer\build-installer.ps1, which publishes the app
; and passes the version and (optionally) the control plane address:
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
AppName=Knovara Desktop
AppVersion={#AppVersion}
AppVerName=Knovara Desktop {#AppVersion}
AppPublisher=Knovara
DefaultDirName={autopf}\Knovara
DefaultGroupName=Knovara
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=Knovara-Desktop-Setup-{#AppVersion}
; "x64" is accepted by every Inno Setup 6.x ("x64compatible" needs 6.3+).
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=assets\knovara.ico
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
UninstallDisplayName=Knovara Desktop
UninstallDisplayIcon={app}\Knovara.Desktop.exe
VersionInfoVersion={#AppVersion}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
; knovara.config.json is the developer's local settings (a sample tenant and localhost addresses); it is
; replaced below by a generated file so customers never inherit it.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "knovara.config.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Knovara Desktop"; Filename: "{app}\Knovara.Desktop.exe"
Name: "{autodesktop}\Knovara Desktop"; Filename: "{app}\Knovara.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Knovara.Desktop.exe"; Description: "Launch Knovara Desktop"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings written by the app next to itself. The enrollment (%LOCALAPPDATA%\Knovara\device.json) is deliberately
; kept, so reinstalling or upgrading does not make the user enroll again; "Forget this device" in the app removes it.
Type: files; Name: "{app}\knovara.config.json"

[Code]
// Pre-fills the "Control plane address" box in the app. Only written for a fresh install, so an upgrade
// never overwrites an address the user already changed.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Url, Json, Path: String;
begin
  if CurStep <> ssPostInstall then Exit;

  Url := '{#ControlPlaneUrl}';
  Path := ExpandConstant('{app}\knovara.config.json');
  if (Url = '') or FileExists(Path) then Exit;

  Json := '{' + #13#10 + '  "ControlPlaneUrl": "' + Url + '"' + #13#10 + '}' + #13#10;
  SaveStringToFile(Path, Json, False);
end;
