; Vesper's Windows installer (Inno Setup 6). Built by scripts\package.ps1, which passes:
;   /DAppVersion=0.2.0  /DArch=x64|arm64  /DPublishDir=<self-contained publish folder>  /DOutputDir=<dist>
;
; Installs for the current user only (no administrator prompt) into
; %LOCALAPPDATA%\Programs\Vesper, with a Start menu entry and an entry in Settings > Apps for
; uninstalling. Vesper's in-app updater runs the next version's Setup silently, with
; /RELAUNCH=yes or no deciding whether Vesper opens again afterwards.

#ifndef AppVersion
  #error AppVersion must be defined
#endif

[Setup]
AppId={{252CF140-F045-423B-AF1A-1E602A5CB001}
AppName=Vesper
AppVersion={#AppVersion}
AppVerName=Vesper {#AppVersion}
AppPublisher=Totaie
AppPublisherURL=https://github.com/Totaie/Vesper-releases
AppSupportURL=https://github.com/Totaie/Vesper-releases
AppUpdatesURL=https://github.com/Totaie/Vesper-releases/releases
AppCopyright=Copyright (c) 2026 Totaie
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Vesper Setup
DefaultDirName={localappdata}\Programs\Vesper
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Vesper-{#AppVersion}-Setup-{#Arch}
SetupIconFile=..\src\Vesper.App\Assets\Vesper.ico
UninstallDisplayIcon={app}\Vesper.exe
UninstallDisplayName=Vesper
WizardStyle=modern
WizardSizePercent=100
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
; Vesper is closed (by the updater, or by Setup asking) before its files are replaced.
CloseApplications=force
RestartApplications=no
SetupLogging=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Put Vesper on the desktop"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; A clean slate, so nothing a previous version had and this one doesn't lingers. The folder is
; fixed (no directory page) and holds only Vesper; your data lives elsewhere.
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{userprograms}\Vesper"; Filename: "{app}\Vesper.exe"; WorkingDir: "{app}"; Comment: "Apple Remote Desktop for Windows"
Name: "{userdesktop}\Vesper"; Filename: "{app}\Vesper.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Vesper.exe"; Description: "Open Vesper"; Flags: nowait postinstall; Check: ShouldOpen

[UninstallDelete]
; Updates Vesper downloaded but hadn't installed. Your Macs, settings and saved passwords stay in
; %LOCALAPPDATA%\Vesper unless you delete that folder too.
Type: filesandordirs; Name: "{localappdata}\Vesper\updates"

[Code]
function ShouldOpen: Boolean;
begin
  { The updater asks whether to open Vesper afterwards; an ordinary install offers it. }
  Result := CompareText(ExpandConstant('{param:RELAUNCH|yes}'), 'no') <> 0;
end;
